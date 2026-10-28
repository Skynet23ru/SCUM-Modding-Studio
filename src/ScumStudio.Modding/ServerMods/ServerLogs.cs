using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ScumStudio.Modding.ServerMods;

/// <summary>One log the console can follow.</summary>
/// <param name="Id">Stable key (<c>server</c>, <c>ue4ss</c>, <c>bridge</c> or a game log kind such as <c>chat</c>).</param>
/// <param name="Path">The file now written (the newest of its kind).</param>
public sealed record LogSource(string Id, string Path);

/// <summary>
/// The logs of a dedicated SCUM server: the engine log <c>Saved\Logs\SCUM.log</c> (UTF-8), the game's own logs
/// <c>Saved\SaveFiles\Logs\&lt;kind&gt;_&lt;yyyyMMddHHmmss&gt;.log</c> (chat, admin, login, kill, loot, economy … one set per
/// server start, UTF-16), UE4SS's log and the studio bridge's results.
/// </summary>
public static partial class ServerLogs
{
    /// <summary>The server's <c>Saved</c> folder.</summary>
    public static string SavedFolder(ServerInstall server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return Path.GetFullPath(Path.Combine(server.Win64, "..", "..", "Saved"));
    }

    /// <summary>The logs that exist now: the server's, then the game's by kind, then UE4SS's and the bridge's.</summary>
    public static IReadOnlyList<LogSource> Sources(ServerInstall server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var saved = SavedFolder(server);
        var list = new List<LogSource>();
        void Add(string id, string path)
        {
            if (File.Exists(path))
            {
                list.Add(new LogSource(id, path));
            }
        }

        Add("server", Path.Combine(saved, "Logs", "SCUM.log"));
        var folder = Path.Combine(saved, "SaveFiles", "Logs");
        if (Directory.Exists(folder))
        {
            var newest = new Dictionary<string, (string Stamp, string Path)>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(folder, "*.log"))
            {
                if (GameLogName().Match(System.IO.Path.GetFileName(file)) is { Success: true } m
                    && (!newest.TryGetValue(m.Groups["kind"].Value, out var known) || string.CompareOrdinal(m.Groups["stamp"].Value, known.Stamp) > 0))
                {
                    newest[m.Groups["kind"].Value] = (m.Groups["stamp"].Value, file);
                }
            }

            // The logs people look at first, then the rest by name.
            string[] first = ["chat", "admin", "login", "kill", "gameplay"];
            foreach (var kind in newest.Keys.OrderBy(k => Array.IndexOf(first, k.ToLowerInvariant()) is var i and >= 0 ? i : first.Length).ThenBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(new LogSource(kind, newest[kind].Path));
            }
        }

        Add("ue4ss", server.LogFile);
        Add("bridge", ServerBridge.ResultsFile(server));
        return list;
    }

    [GeneratedRegex(@"^(?<kind>.+)_(?<stamp>\d{14})\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GameLogName();
}

/// <summary>
/// Follows the end of a log file while the server writes it: <see cref="ReadNew"/> returns the lines added since the last
/// call. Opens the file shared (the server keeps it open), picks UTF-16 or UTF-8 itself, and starts over when the file shrinks
/// (a new server start writes it again).
/// </summary>
public sealed class LogTail
{
    private readonly int _initialBytes;
    private long _offset = -1;
    private string _partial = string.Empty;
    private Encoding? _encoding;

    /// <summary>Follows <paramref name="path"/>; the first read returns the last <paramref name="initialBytes"/> bytes.</summary>
    public LogTail(string path, int initialBytes = 64 * 1024)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        _initialBytes = Math.Max(0, initialBytes);
    }

    /// <summary>The file followed.</summary>
    public string Path { get; }

    /// <summary>The complete lines written since the last call (empty when none, or the file cannot be read now).</summary>
    public IReadOnlyList<string> ReadNew()
    {
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            if (_encoding is null)
            {
                var head = new byte[Math.Min(4, length)];
                _ = stream.Read(head, 0, head.Length);
                _encoding = head.Length >= 2 && (head[1] == 0 || (head[0] == 0xFF && head[1] == 0xFE)) ? new UnicodeEncoding(false, false) : new UTF8Encoding(false);
            }

            var wide = _encoding is UnicodeEncoding;
            var skipFirst = false;
            if (_offset < 0)
            {
                _offset = Math.Max(0, length - _initialBytes);
                skipFirst = _offset > 0;
            }
            else if (length < _offset)
            {
                _offset = 0;
                _partial = string.Empty;
            }

            if (wide)
            {
                _offset &= ~1L;
            }

            var count = (int)Math.Min(length - _offset, 8 * 1024 * 1024);
            if (wide)
            {
                count &= ~1;
            }

            if (count <= 0)
            {
                return [];
            }

            stream.Seek(_offset, SeekOrigin.Begin);
            var buffer = new byte[count];
            var read = stream.Read(buffer, 0, count);
            if (wide)
            {
                read &= ~1;
            }

            _offset += read;
            var text = _partial + _encoding.GetString(buffer, 0, read).TrimStart('﻿');
            var parts = text.Split('\n');
            _partial = parts[^1];
            var lines = new List<string>(parts.Length);
            for (var i = skipFirst ? 1 : 0; i < parts.Length - 1; i++)
            {
                var line = parts[i].TrimEnd('\r');
                if (line.Length > 0)
                {
                    lines.Add(line);
                }
            }

            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The level of a line for colouring: <c>error</c>, <c>warning</c> or empty.</summary>
    public static string LevelOf(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Contains("Error", StringComparison.Ordinal) || line.Contains("Fatal", StringComparison.Ordinal) || line.Contains("Exception", StringComparison.Ordinal)
            || line.Contains("Failed", StringComparison.Ordinal) || line.Contains("Crash", StringComparison.Ordinal))
        {
            return "error";
        }

        return line.Contains("Warning", StringComparison.Ordinal) || line.Contains("Violation", StringComparison.OrdinalIgnoreCase) ? "warning" : string.Empty;
    }

    /// <summary>True when <paramref name="line"/> passes the filter (every word of it, any case; empty passes all).</summary>
    public static bool Matches(string line, string? filter)
    {
        ArgumentNullException.ThrowIfNull(line);
        return string.IsNullOrWhiteSpace(filter)
            || filter.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => line.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The stamp now, for the bridge's lines.</summary>
    internal static string Stamp() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
