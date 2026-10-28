using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScumStudio.Modding.Remote;

/// <summary>What one pass of the mirror did.</summary>
/// <param name="Downloaded">Files fetched or extended.</param>
/// <param name="Uploaded">Files sent to the host.</param>
/// <param name="ServerLogUtc">When the host last wrote <c>SCUM.log</c> (null when it has none); a fresh time means the server runs.</param>
public sealed record MirrorReport(int Downloaded, int Uploaded, DateTime? ServerLogUtc);

/// <summary>What a check of a host found.</summary>
/// <param name="RootFound">The folder holds <c>SCUM\Binaries\Win64</c>.</param>
/// <param name="HasUe4ss">UE4SS is installed there.</param>
/// <param name="HasDatabase">The save (<c>SCUM.db</c>) is there.</param>
/// <param name="ServerLogUtc">When <c>SCUM.log</c> was last written, or null.</param>
public sealed record MirrorProbe(bool RootFound, bool HasUe4ss, bool HasDatabase, DateTime? ServerLogUtc);

/// <summary>
/// Keeps a folder on this PC in step with the files of a remote server that the app uses (logs, the save, the mod folders, the
/// settings) so every page works on the local copy as it does for a local server. Logs are fetched by appending what is new,
/// the save when it changed, mods and settings both ways (when writing is allowed). Files changed on this PC are never
/// overwritten by the host; a command queue is added to the host's queue, not replacing it.
/// </summary>
public sealed class ServerMirror
{
    /// <summary>Where the server's executable and UE4SS live below the install folder.</summary>
    public const string Win64 = "SCUM/Binaries/Win64";

    private const string Mods = Win64 + "/ue4ss/Mods";
    private const string Config = "SCUM/Saved/Config/WindowsServer";
    private const string GameLogs = "SCUM/Saved/SaveFiles/Logs";
    private const string Queue = Mods + "/StudioBridge/commands.txt";
    private const string StateFile = ".mirror.json";
    private const long TailBytes = 1024 * 1024;
    private const long SmallFile = 256 * 1024;
    private const int KeepGameLogs = 24;

    private static readonly string[] AppendedLogs =
    [
        "SCUM/Saved/Logs/SCUM.log",
        Mods + "/StudioBridge/results.log",
        Mods + "/StudioBridge/events.log",
        Mods + "/StudioActivity/activity.log",
        Win64 + "/ue4ss/UE4SS.log",
    ];

    private static readonly string[] WholeFiles = [Mods + "/StudioBridge/players.txt"];
    private static readonly string[] Database = ["SCUM/Saved/SaveFiles/SCUM.db", "SCUM/Saved/SaveFiles/SCUM.db-wal"];
    private static readonly string[] Markers = [Win64 + "/dwmapi.dll", Win64 + "/dwmapi.dll.off", Win64 + "/ue4ss/UE4SS.dll"];

    private readonly IRemoteFiles _remote;
    private readonly string _remoteRoot;
    private readonly string _localRoot;
    private readonly bool _allowWrite;
    private Dictionary<string, FileState> _state;

    /// <summary>A mirror of <paramref name="remoteRoot"/> (the folder that holds <c>SCUM</c>) in <paramref name="localRoot"/>.</summary>
    public ServerMirror(IRemoteFiles remote, string remoteRoot, string localRoot, bool allowWrite)
    {
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _remoteRoot = (remoteRoot ?? throw new ArgumentNullException(nameof(remoteRoot))).Trim().TrimEnd('/');
        _localRoot = Path.GetFullPath(localRoot);
        _allowWrite = allowWrite;
        _state = LoadState();
    }

    private sealed class FileState
    {
        public long Size { get; set; }

        public long Modified { get; set; }

        public string? Hash { get; set; }
    }

    private string Remote(string rel) => _remoteRoot + "/" + rel;

    private string Local(string rel) => Path.Combine(_localRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    private Dictionary<string, FileState> LoadState()
    {
        try
        {
            var file = Path.Combine(_localRoot, StateFile);
            if (File.Exists(file))
            {
                return JsonSerializer.Deserialize<Dictionary<string, FileState>>(File.ReadAllText(file)) ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken state file only means everything is checked again.
        }

        return [];
    }

    private void SaveState()
    {
        Directory.CreateDirectory(_localRoot);
        File.WriteAllText(Path.Combine(_localRoot, StateFile), JsonSerializer.Serialize(_state));
    }

    /// <summary>Looks at a host: is the folder a SCUM server, does it have UE4SS and a save.</summary>
    public static MirrorProbe Probe(IRemoteFiles remote, string remoteRoot)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var root = remoteRoot.Trim().TrimEnd('/');
        var found = remote.Stat(root + "/" + Win64) is { IsDirectory: true };
        if (!found)
        {
            return new MirrorProbe(false, false, false, null);
        }

        return new MirrorProbe(
            true,
            remote.Stat(root + "/" + Win64 + "/ue4ss/UE4SS.dll") is not null,
            remote.Stat(root + "/" + Database[0]) is not null,
            remote.Stat(root + "/" + AppendedLogs[0])?.ModifiedUtc);
    }

    /// <summary>
    /// One pass: sends what changed here (when writing is allowed), then fetches what is new. <paramref name="full"/> also looks at
    /// the save, the mod folders and the settings, which change rarely.
    /// </summary>
    /// <exception cref="IOException">The connection is lost; the next pass starts again.</exception>
    public MirrorReport Sync(bool full)
    {
        Directory.CreateDirectory(_localRoot);
        var down = 0;
        var up = 0;
        if (_allowWrite)
        {
            up += PushQueue();
            up += PushChanged();
        }

        foreach (var rel in AppendedLogs)
        {
            down += Append(rel, false);
        }

        foreach (var entry in _remote.List(Remote(GameLogs)).Where(e => !e.IsDirectory && e.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(e => e.ModifiedUtc).Take(KeepGameLogs))
        {
            down += Append(GameLogs + "/" + entry.Name, true);
        }

        foreach (var rel in WholeFiles)
        {
            down += Whole(rel);
        }

        if (full)
        {
            down += FullPass();
        }

        SaveState();
        return new MirrorReport(down, up, _remote.Stat(Remote(AppendedLogs[0]))?.ModifiedUtc);
    }

    private int FullPass()
    {
        var down = 0;
        Directory.CreateDirectory(Local(Win64));
        File.WriteAllBytes(Local(Win64 + "/SCUMServer.exe"), []);   // the app finds a server by this file
        Directory.CreateDirectory(Local("SCUM/Content/Paks"));
        foreach (var rel in Markers)
        {
            if (_remote.Stat(Remote(rel)) is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Local(rel))!);
                if (!File.Exists(Local(rel)))
                {
                    File.WriteAllBytes(Local(rel), []);
                }
            }
            else if (File.Exists(Local(rel)))
            {
                File.Delete(Local(rel));
            }
        }

        foreach (var rel in Database)
        {
            down += Whole(rel, SmallFile * 1024);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        down += Tree(Mods, Mods, 4, seen);
        down += Tree(Config, Config, 1, seen, ".ini");
        var ini = Win64 + "/ue4ss/UE4SS-settings.ini";
        if (_remote.Stat(Remote(ini)) is not null)
        {
            seen.Add(ini);
            down += Whole(ini);
        }

        // What the host no longer has and was not changed here goes too.
        foreach (var rel in _state.Keys.Where(k => IsManaged(k) && !seen.Contains(k)).ToList())
        {
            if (File.Exists(Local(rel)) && Hash(Local(rel)) == _state[rel].Hash)
            {
                File.Delete(Local(rel));
                _state.Remove(rel);
            }
        }

        return down;
    }

    private static bool IsAppended(string rel) =>
        AppendedLogs.Contains(rel, StringComparer.Ordinal) || rel.StartsWith(GameLogs + "/", StringComparison.Ordinal) ||
        WholeFiles.Contains(rel, StringComparer.Ordinal) || rel == Queue;

    private static bool IsManaged(string rel) =>
        !IsAppended(rel) && !Database.Contains(rel) &&
        (rel.StartsWith(Mods + "/", StringComparison.Ordinal) || rel.StartsWith(Config + "/", StringComparison.Ordinal) || rel == Win64 + "/ue4ss/UE4SS-settings.ini");

    private int Tree(string rootRel, string folderRel, int depth, HashSet<string> seen, string? extension = null)
    {
        var down = 0;
        foreach (var entry in _remote.List(Remote(folderRel)))
        {
            var rel = folderRel + "/" + entry.Name;
            if (entry.IsDirectory)
            {
                if (depth > 1)
                {
                    down += Tree(rootRel, rel, depth - 1, seen, extension);
                }

                continue;
            }

            if (IsAppended(rel) || entry.Size > SmallFile || (extension is not null && !entry.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            seen.Add(rel);
            down += Whole(rel);
        }

        return down;
    }

    private static string Hash(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA1.HashData(stream));
    }

    private int Append(string rel, bool utf16)
    {
        var entry = _remote.Stat(Remote(rel));
        if (entry is null || entry.IsDirectory)
        {
            return 0;
        }

        var local = Local(rel);
        _state.TryGetValue(rel, out var state);
        var consumed = state is not null && File.Exists(local) ? state.Size : -1;
        if (consumed == entry.Size)
        {
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        if (consumed < 0 || entry.Size < consumed)
        {
            // First time, or the host started the file again: take the last part.
            var start = Math.Max(0, entry.Size - TailBytes) & ~1L;
            using var fresh = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            if (utf16 && start > 0)
            {
                fresh.Write([0xFF, 0xFE]);
            }

            var before = fresh.Length;
            _remote.Download(Remote(rel), fresh, start);
            _state[rel] = new FileState { Size = start + (fresh.Length - before) };
            return 1;
        }

        using var more = new FileStream(local, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var length = more.Length;
        _remote.Download(Remote(rel), more, consumed);
        _state[rel] = new FileState { Size = consumed + (more.Length - length) };
        return 1;
    }

    private int Whole(string rel, long limit = long.MaxValue)
    {
        var entry = _remote.Stat(Remote(rel));
        if (entry is null || entry.IsDirectory || entry.Size > limit)
        {
            return 0;
        }

        var local = Local(rel);
        _state.TryGetValue(rel, out var state);
        var exists = File.Exists(local);
        if (state is not null && exists)
        {
            if (state.Hash is not null && Hash(local) != state.Hash)
            {
                return 0;   // changed here: the host does not overwrite it
            }

            if (state.Size == entry.Size && state.Modified == entry.ModifiedUtc.Ticks)
            {
                return 0;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        var temp = local + ".part";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            _remote.Download(Remote(rel), stream);
        }

        File.Move(temp, local, true);
        _state[rel] = new FileState { Size = entry.Size, Modified = entry.ModifiedUtc.Ticks, Hash = Hash(local) };
        return 1;
    }

    private int PushQueue()
    {
        var local = Local(Queue);
        if (!File.Exists(local))
        {
            return 0;
        }

        byte[] mine;
        try
        {
            mine = File.ReadAllBytes(local);
        }
        catch (IOException)
        {
            return 0;   // the app is writing it just now; the next pass takes it
        }

        if (mine.Length == 0)
        {
            return 0;
        }

        var all = new List<byte>();
        if (_remote.Stat(Remote(Queue)) is { IsDirectory: false })
        {
            using var theirs = new MemoryStream();
            _remote.Download(Remote(Queue), theirs);
            all.AddRange(theirs.ToArray());
            if (all.Count > 0 && all[^1] != (byte)'\n')
            {
                all.Add((byte)'\n');
            }
        }

        all.AddRange(mine);
        using (var body = new MemoryStream([.. all]))
        {
            _remote.Upload(Remote(Queue), body);
        }

        try
        {
            File.Delete(local);
        }
        catch (IOException)
        {
            File.WriteAllBytes(local, []);
        }

        return 1;
    }

    private int PushChanged()
    {
        var up = 0;
        foreach (var (folder, depth) in new[] { (Mods, 4), (Config, 1) })
        {
            var root = Local(folder);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", depth > 1 ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            {
                var rel = Path.GetRelativePath(_localRoot, file).Replace('\\', '/');
                if (!IsManaged(rel) || rel.EndsWith(".part", StringComparison.Ordinal) || new FileInfo(file).Length > SmallFile)
                {
                    continue;
                }

                up += PushOne(rel);
            }
        }

        var ini = Win64 + "/ue4ss/UE4SS-settings.ini";
        if (File.Exists(Local(ini)))
        {
            up += PushOne(ini);
        }

        // A file the app removed here is removed on the host too.
        foreach (var rel in _state.Keys.Where(k => IsManaged(k) && !File.Exists(Local(k))).ToList())
        {
            _remote.Delete(Remote(rel));
            _state.Remove(rel);
            up++;
        }

        return up;
    }

    private int PushOne(string rel)
    {
        var local = Local(rel);
        string hash;
        try
        {
            hash = Hash(local);
        }
        catch (IOException)
        {
            return 0;
        }

        if (_state.TryGetValue(rel, out var state) && state.Hash == hash)
        {
            return 0;
        }

        using (var stream = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            _remote.Upload(Remote(rel), stream);
        }

        var entry = _remote.Stat(Remote(rel));
        _state[rel] = new FileState { Size = entry?.Size ?? new FileInfo(local).Length, Modified = entry?.ModifiedUtc.Ticks ?? 0, Hash = hash };
        return 1;
    }

    /// <summary>The Paks folder of the mirror (what the profile's folder points at).</summary>
    public static string PaksOf(string localRoot) => Path.Combine(localRoot, "SCUM", "Content", "Paks");

    /// <summary>For tests and tools: the text of a file as the mirror holds it.</summary>
    public string ReadLocal(string rel) => Encoding.UTF8.GetString(File.ReadAllBytes(Local(rel)));
}
