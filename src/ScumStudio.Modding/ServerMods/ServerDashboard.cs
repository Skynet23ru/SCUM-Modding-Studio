using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ScumStudio.Modding.ServerMods;

/// <summary>Which part of the logs the dashboard reads.</summary>
public enum DashboardScope
{
    /// <summary>Since the last server start (the newest set of game logs).</summary>
    CurrentStart,

    /// <summary>Every game log the server kept.</summary>
    AllLogs,
}

/// <summary>A player who is in the game.</summary>
/// <param name="Name">The name.</param>
/// <param name="SteamId">The Steam id (empty when only the bridge named the player).</param>
/// <param name="Since">When the player logged in (empty when unknown).</param>
/// <param name="X">The last known place, X (cm).</param>
/// <param name="Y">Y (cm).</param>
/// <param name="Z">Z (cm).</param>
/// <param name="FromBridge">The place is from the studio bridge's last look (live), not from the login log.</param>
public sealed record DashboardPlayer(string Name, string SteamId, string Since, double X, double Y, double Z, bool FromBridge);

/// <summary>One sale or purchase at a trader (the <c>economy</c> log).</summary>
/// <param name="Time">When (server time as logged).</param>
/// <param name="Player">Who.</param>
/// <param name="Sold">True when the player sold, false when the player bought.</param>
/// <param name="Item">What (with its count when it is more than one).</param>
/// <param name="Price">The money.</param>
/// <param name="Trader">The trader (an outpost's trader, for example <c>B_4_Trader</c>).</param>
public sealed record DashboardTrade(string Time, string Player, bool Sold, string Item, int Quantity, long Price, string Trader);

/// <summary>A bunker as the last <c>[LogBunkerLock]</c> report of the game logs it.</summary>
/// <param name="Name">The sector name (A1, D4 …).</param>
/// <param name="Active">True when active, false when locked.</param>
/// <param name="ChangedAt">When it was activated (active) or locked (locked), as time of the server.</param>
/// <param name="NextActivation">When it activates next (locked only).</param>
/// <param name="X">Where, X (cm).</param>
/// <param name="Y">Y (cm).</param>
/// <param name="Z">Z (cm).</param>
public sealed record DashboardBunker(string Name, bool Active, DateTime? ChangedAt, DateTime? NextActivation, double X, double Y, double Z);

/// <summary>A death (the <c>kill</c> log).</summary>
/// <param name="Time">When.</param>
/// <param name="Victim">Who died.</param>
/// <param name="Killer">Who killed (a player's name or the NPC's class).</param>
/// <param name="KillerIsNpc">The killer is an NPC.</param>
/// <param name="Weapon">With what.</param>
/// <param name="Distance">From how far (m), or -1.</param>
public sealed record DashboardKill(string Time, string Victim, string Killer, bool KillerIsNpc, string Weapon, double Distance);

/// <summary>A chat message (the <c>chat</c> log).</summary>
/// <param name="Time">When.</param>
/// <param name="Player">Who.</param>
/// <param name="Channel">Local, Global, Squad, Admin ….</param>
/// <param name="Text">The text.</param>
public sealed record DashboardChat(string Time, string Player, string Channel, string Text);

/// <summary>An admin command (the <c>admin</c> log).</summary>
/// <param name="Time">When.</param>
/// <param name="Player">Who.</param>
/// <param name="Command">The command.</param>
public sealed record DashboardAdmin(string Time, string Player, string Command);

/// <summary>Any other line of the game's logs that tells something happened (traps, fame, violations, vehicles …).</summary>
/// <param name="Time">When.</param>
/// <param name="Kind">The log it came from.</param>
/// <param name="Text">The line.</param>
public sealed record DashboardEvent(string Time, string Kind, string Text);

/// <summary>How the server runs (the last <c>Global Stats</c> line of <c>SCUM.log</c>).</summary>
/// <param name="Fps">Frames per second of the server.</param>
/// <param name="Players">Players the server counts.</param>
/// <param name="Zombies">Zombies the server counts.</param>
/// <param name="At">When it was written.</param>
public sealed record DashboardHealth(double Fps, int Players, int Zombies, string At);

/// <summary>Everything the dashboard shows.</summary>
public sealed class DashboardSnapshot
{
    /// <summary>The server process is running.</summary>
    public bool ServerRunning { get; init; }

    /// <summary>The server's player limit (<c>scum.MaxPlayers</c>), or 0 when not logged.</summary>
    public int MaxPlayers { get; init; }

    /// <summary>The players in the game now.</summary>
    public IReadOnlyList<DashboardPlayer> Players { get; init; } = [];

    /// <summary>True when the list is the bridge's (live), false when it comes from the login log.</summary>
    public bool PlayersFromBridge { get; init; }

    /// <summary>The server's health, or null when <c>SCUM.log</c> has no such line yet.</summary>
    public DashboardHealth? Health { get; init; }

    /// <summary>Sales and purchases, newest first.</summary>
    public IReadOnlyList<DashboardTrade> Trades { get; init; } = [];

    /// <summary>The bunkers by name.</summary>
    public IReadOnlyList<DashboardBunker> Bunkers { get; init; } = [];

    /// <summary>Deaths, newest first.</summary>
    public IReadOnlyList<DashboardKill> Kills { get; init; } = [];

    /// <summary>Chat, newest first.</summary>
    public IReadOnlyList<DashboardChat> Chat { get; init; } = [];

    /// <summary>Admin commands, newest first.</summary>
    public IReadOnlyList<DashboardAdmin> Admin { get; init; } = [];

    /// <summary>Everything else that happened, newest first.</summary>
    public IReadOnlyList<DashboardEvent> Events { get; init; } = [];

    /// <summary>What the players did in the game (the activity mod's log), newest first.</summary>
    public IReadOnlyList<ActivityEvent> Activity { get; init; } = [];

    /// <summary>How many times the players logged in.</summary>
    public int Logins { get; init; }

    /// <summary>When the snapshot was made.</summary>
    public DateTime MadeAt { get; init; }

    /// <summary>The newest log write found (server time of the line), or empty.</summary>
    public string LastLogTime { get; init; } = string.Empty;
}

/// <summary>
/// The dashboard: reads the server's logs and tells what is going on: who is online, which bunkers are active, who sold and
/// bought what, who died, what was said and run. The logs are the game's own (<c>Saved\SaveFiles\Logs\&lt;kind&gt;_&lt;start&gt;.log</c>, UTF-16,
/// one set per server start) and <c>Saved\Logs\SCUM.log</c>; the studio bridge adds the live player list. Files are read
/// again only when they grew.
/// </summary>
public sealed partial class ServerDashboard
{
    private const int KeepPerList = 300;

    private readonly ServerInstall _server;
    private readonly Func<bool> _isRunning;
    private readonly Dictionary<string, (long Length, DateTime Stamp, string[] Lines)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the logs of <paramref name="server"/>; <paramref name="isRunning"/> tells whether the server process runs (default: looks for <c>SCUMServer</c>).</summary>
    public ServerDashboard(ServerInstall server, Func<bool>? isRunning = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _isRunning = isRunning ?? IsServerRunning;
    }

    /// <summary>Reads the logs and builds the snapshot.</summary>
    public DashboardSnapshot Read(DashboardScope scope)
    {
        var folder = Path.Combine(ServerLogs.SavedFolder(_server), "SaveFiles", "Logs");
        var files = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.log").Select(f => (Path: f, M: GameLogName().Match(System.IO.Path.GetFileName(f)))).Where(f => f.M.Success)
                .Select(f => (f.Path, Kind: f.M.Groups["kind"].Value.ToLowerInvariant(), Start: f.M.Groups["stamp"].Value)).ToList()
            : [];
        var newest = files.Select(f => f.Start).DefaultIfEmpty(string.Empty).Max()!;
        var chosen = scope == DashboardScope.CurrentStart ? files.Where(f => f.Start == newest).ToList() : files;
        IEnumerable<(string Time, string Text)> LinesOf(string kind) =>
            chosen.Where(f => f.Kind == kind).OrderBy(f => f.Start, StringComparer.Ordinal).SelectMany(f => Read(f.Path)).Select(Split).Where(l => l.Time.Length > 0);

        var kinds = files.Select(f => f.Kind).Distinct().ToList();
        var trades = new List<DashboardTrade>();
        foreach (var (time, text) in LinesOf("economy"))
        {
            if (ParseTrade(time, text) is { } trade)
            {
                trades.Add(trade);
            }
        }

        var chat = LinesOf("chat").Select(l => ParseChat(l.Time, l.Text)).OfType<DashboardChat>().ToList();
        var admin = LinesOf("admin").Select(l => ParseAdmin(l.Time, l.Text)).OfType<DashboardAdmin>().ToList();
        var kills = LinesOf("kill").Select(l => ParseKill(l.Time, l.Text)).OfType<DashboardKill>().ToList();

        // The login log of the newest start tells who is in the game; the other starts only count the logins.
        var online = new Dictionary<string, DashboardPlayer>(StringComparer.Ordinal);
        var logins = 0;
        foreach (var f in chosen.Where(f => f.Kind == "login").OrderBy(f => f.Start, StringComparer.Ordinal))
        {
            if (f.Start == newest)
            {
                online.Clear();
            }

            foreach (var (time, text) in Read(f.Path).Select(Split).Where(l => l.Time.Length > 0))
            {
                if (ParseLogin(text) is not { } login)
                {
                    continue;
                }

                if (login.In)
                {
                    logins++;
                    if (f.Start == newest)
                    {
                        online[login.Id] = new DashboardPlayer(login.Name, login.Id, time, login.X, login.Y, login.Z, false);
                    }
                }
                else if (f.Start == newest)
                {
                    online.Remove(login.Id);
                }
            }
        }

        var bunkers = new Dictionary<string, DashboardBunker>(StringComparer.OrdinalIgnoreCase);
        var events = new List<DashboardEvent>();
        foreach (var (time, text) in LinesOf("gameplay"))
        {
            if (ParseBunker(time, text) is { } bunker)
            {
                bunkers[bunker.Name] = bunker;
            }
            else if (text.Contains("[LogBunkerLock]", StringComparison.Ordinal))
            {
                if (!text.Contains("activations:", StringComparison.OrdinalIgnoreCase) && !text.Contains("bunkers:", StringComparison.OrdinalIgnoreCase))
                {
                    events.Add(new DashboardEvent(time, "bunker", text.Replace("[LogBunkerLock] ", string.Empty, StringComparison.Ordinal)));
                }
            }
            else
            {
                events.Add(new DashboardEvent(time, "gameplay", text));
            }
        }

        // The other logs: whatever the game writes there is an event (violations, vehicle and base destruction, quests, sentries ...).
        string[] known = ["economy", "chat", "admin", "kill", "login", "gameplay", "loot"];
        foreach (var kind in kinds.Where(k => !known.Contains(k)))
        {
            events.AddRange(LinesOf(kind).Where(l => !l.Text.StartsWith("Game version", StringComparison.Ordinal) && l.Text.Trim('-', ' ').Length > 0)
                .Select(l => new DashboardEvent(l.Time, kind, l.Text)));
        }

        // What the studio bridge saw inside the game (somebody joined or left, and where).
        var since = scope == DashboardScope.CurrentStart && newest.Length == 14
            ? $"{newest[..4]}.{newest[4..6]}.{newest[6..8]}-{newest[8..10]}.{newest[10..12]}.{newest[12..14]}"
            : string.Empty;
        foreach (var line in ReadLua(ServerBridge.EventsFile(_server)).TakeLast(KeepPerList))
        {
            var p = line.Split('	');
            if (p.Length >= 3 && string.CompareOrdinal(p[0], since) >= 0)
            {
                events.Add(new DashboardEvent(p[0], "lua", p[1] + " " + p[2] + (p.Length >= 5 ? " (" + p[3] + " " + p[4] + ")" : string.Empty)));
            }
        }

        // The bridge names the players but not their Steam ids: the login logs of every start know them by name.
        var steamIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files.Where(f => f.Kind == "login").OrderBy(f => f.Start, StringComparer.Ordinal))
        {
            foreach (var line in Read(f.Path).Select(Split))
            {
                if (line.Time.Length > 0 && ParseLogin(line.Text) is { } seen)
                {
                    steamIds[seen.Name] = seen.Id;
                }
            }
        }

        var bridgePlayers = ReadBridgePlayers()?.Select(p => p.SteamId.Length == 0 && steamIds.TryGetValue(p.Name, out var id) ? p with { SteamId = id } : p).ToList();
        var (health, max) = ReadServerLog();
        var running = _isRunning();
        IReadOnlyList<DashboardPlayer> players = bridgePlayers is { Count: > 0 } live
            ? live
            : (running ? [.. online.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)] : []);

        return new DashboardSnapshot
        {
            ServerRunning = running,
            MaxPlayers = max,
            Players = players,
            PlayersFromBridge = bridgePlayers is { Count: > 0 },
            Health = health,
            Trades = [.. trades.AsEnumerable().Reverse()],
            Bunkers = [.. bunkers.Values.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)],
            Kills = [.. kills.AsEnumerable().Reverse().Take(KeepPerList)],
            Chat = [.. chat.AsEnumerable().Reverse().Take(KeepPerList)],
            Admin = [.. admin.AsEnumerable().Reverse().Take(KeepPerList)],
            Events = [.. events.OrderByDescending(e => e.Time, StringComparer.Ordinal).Take(KeepPerList)],
            Logins = logins,
            Activity = ServerActivity.Read(_server, since, 600),
            MadeAt = DateTime.Now,
            LastLogTime = new[] { trades.FirstOrDefault()?.Time, chat.FirstOrDefault()?.Time, health?.At }.OfType<string>().DefaultIfEmpty(string.Empty).Max()!,
        };
    }

    private string[] ReadLua(string path) => Read(path);

    private IReadOnlyList<DashboardPlayer>? ReadBridgePlayers()
    {
        var written = ServerBridge.PlayersWrittenAt(_server);
        if (written is null || DateTime.UtcNow - written.Value > TimeSpan.FromSeconds(30))
        {
            return null;
        }

        return ServerBridge.ReadPlayerPositions(_server);
    }

    private static bool IsServerRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("SCUMServer").Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private (DashboardHealth? Health, int Max) ReadServerLog()
    {
        var path = Path.Combine(ServerLogs.SavedFolder(_server), "Logs", "SCUM.log");
        string[] lines;
        try
        {
            lines = Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, 0);
        }

        DashboardHealth? health = null;
        var max = 0;
        for (var i = lines.Length - 1; i >= 0 && (health is null || max == 0); i--)
        {
            var line = lines[i];
            if (health is null && line.Contains("Global Stats:", StringComparison.Ordinal) && GlobalStats().Match(line) is { Success: true } g)
            {
                health = new DashboardHealth(Number(g.Groups["fps"].Value), (int)Number(g.Groups["p"].Value), (int)Number(g.Groups["z"].Value), g.Groups["t"].Value);
            }
            else if (max == 0 && line.Contains("scum.MaxPlayers:", StringComparison.Ordinal) && MaxPlayersLine().Match(line) is { Success: true } m)
            {
                max = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            }
        }

        return (health, max);
    }

    /// <summary>The lines of a log file, read again only when it grew (UTF-16 game logs and the UTF-8 engine log).</summary>
    private string[] Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return [];
            }

            if (_cache.TryGetValue(path, out var kept) && kept.Length == info.Length && kept.Stamp == info.LastWriteTimeUtc)
            {
                return kept.Lines;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[(int)Math.Min(stream.Length, 64 * 1024 * 1024)];
            var read = stream.Read(bytes, 0, bytes.Length);
            var wide = read > 1 && (bytes[1] == 0 || (bytes[0] == 0xFF && bytes[1] == 0xFE));
            var text = wide ? new UnicodeEncoding(false, false).GetString(bytes, 0, read & ~1) : new UTF8Encoding(false).GetString(bytes, 0, read);
            var lines = text.TrimStart('\uFEFF').Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
            _cache[path] = (info.Length, info.LastWriteTimeUtc, lines);
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static (string Time, string Text) Split(string line) =>
        LinePrefix().Match(line) is { Success: true } m ? (m.Groups["t"].Value, m.Groups["rest"].Value) : (string.Empty, line);

    private static double Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Parses a log time (<c>2026.10.25-13.21.35</c>), or null.</summary>
    public static DateTime? ParseTime(string time) =>
        DateTime.TryParseExact(time, "yyyy.MM.dd-HH.mm.ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    /// <summary>A <c>[Trade]</c> sale or purchase line, or null for any other line (the before and after balance lines too).</summary>
    public static DashboardTrade? ParseTrade(string time, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (TradeSold().Match(text) is { Success: true } s)
        {
            var (item, qty) = ItemAndCount(s.Groups["item"].Value);
            return new DashboardTrade(time, s.Groups["name"].Value, true, item, qty, long.Parse(s.Groups["price"].Value, CultureInfo.InvariantCulture), s.Groups["trader"].Value);
        }

        if (TradeBought().Match(text) is { Success: true } b)
        {
            var (item, qty) = ItemAndCount(b.Groups["item"].Value);
            return new DashboardTrade(time, b.Groups["name"].Value, false, item, qty, long.Parse(b.Groups["price"].Value, CultureInfo.InvariantCulture), b.Groups["trader"].Value);
        }

        return null;
    }

    /// <summary><c>Ear_Plugs (x3)</c> → (<c>Ear_Plugs</c>, 3); <c>Pants (health: 100.00, uses: 1)</c> → (<c>Pants</c>, 1).</summary>
    private static (string Item, int Quantity) ItemAndCount(string text)
    {
        var open = text.IndexOf(" (", StringComparison.Ordinal);
        var item = open > 0 ? text[..open] : text;
        return (item, ItemCount().Match(text) is { Success: true } m ? int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) : 1);
    }

    /// <summary>A chat line, or null.</summary>
    public static DashboardChat? ParseChat(string time, string text) =>
        ChatLine().Match(text) is { Success: true } m ? new DashboardChat(time, m.Groups["name"].Value, m.Groups["chan"].Value, m.Groups["text"].Value) : null;

    /// <summary>An admin command line, or null.</summary>
    public static DashboardAdmin? ParseAdmin(string time, string text) =>
        AdminLine().Match(text) is { Success: true } m ? new DashboardAdmin(time, m.Groups["name"].Value, m.Groups["cmd"].Value) : null;

    /// <summary>A death line, or null (the JSON line that follows each death is ignored).</summary>
    public static DashboardKill? ParseKill(string time, string text)
    {
        if (KillLine().Match(text) is not { Success: true } m)
        {
            return null;
        }

        var killerId = m.Groups["kid"].Value;
        return new DashboardKill(time, m.Groups["victim"].Value, m.Groups["killer"].Value, killerId.Equals("NPC", StringComparison.OrdinalIgnoreCase), m.Groups["weapon"].Value,
            KillDistance().Match(text) is { Success: true } d ? Number(d.Groups["dist"].Value) : -1);
    }

    /// <summary>A login or logout line, or null.</summary>
    public static (bool In, string Id, string Name, double X, double Y, double Z)? ParseLogin(string text) =>
        LoginLine().Match(text) is { Success: true } m
            ? (m.Groups["dir"].Value == "in", m.Groups["id"].Value, m.Groups["name"].Value, Number(m.Groups["x"].Value), Number(m.Groups["y"].Value), Number(m.Groups["z"].Value))
            : null;

    /// <summary>A bunker state line of the <c>[LogBunkerLock]</c> report, or null.</summary>
    public static DashboardBunker? ParseBunker(string time, string text)
    {
        var at = ParseTime(time);
        if (BunkerActive().Match(text) is { Success: true } a)
        {
            return new DashboardBunker(a.Groups["b"].Value, true, at - Span(a, "a"), null, Number(a.Groups["x"].Value), Number(a.Groups["y"].Value), Number(a.Groups["z"].Value));
        }

        if (BunkerLocked().Match(text) is { Success: true } l)
        {
            return new DashboardBunker(l.Groups["b"].Value, false, at - Span(l, "l"), at + Span(l, "n"), Number(l.Groups["x"].Value), Number(l.Groups["y"].Value), Number(l.Groups["z"].Value));
        }

        return null;
    }

    private static TimeSpan Span(Match m, string prefix) => new(
        int.Parse(m.Groups[prefix + "h"].Value, CultureInfo.InvariantCulture) / 24, int.Parse(m.Groups[prefix + "h"].Value, CultureInfo.InvariantCulture) % 24,
        int.Parse(m.Groups[prefix + "m"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[prefix + "s"].Value, CultureInfo.InvariantCulture));

    [GeneratedRegex(@"^(?<kind>.+)_(?<stamp>\d{14})\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GameLogName();

    [GeneratedRegex(@"^\s*(?<t>\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}): (?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LinePrefix();

    [GeneratedRegex(@"\[Trade\] Tradeable \((?<item>.*)\) sold by (?<name>.*?)\((?<id>\d{5,})\) for (?<price>-?\d+) .*? to trader (?<trader>\S+?),", RegexOptions.CultureInvariant)]
    private static partial Regex TradeSold();

    [GeneratedRegex(@"\[Trade\] Tradeable \((?<item>.*)\) purchased by (?<name>.*?)\((?<id>\d{5,})\) for (?<price>-?\d+) money from trader (?<trader>\S+?),", RegexOptions.CultureInvariant)]
    private static partial Regex TradeBought();

    [GeneratedRegex(@"\(x(?<n>\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ItemCount();

    [GeneratedRegex(@"^'(?<id>\d{5,}):(?<name>.*?)\(\d+\)' '(?<chan>[^:']+): (?<text>.*)'\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ChatLine();

    [GeneratedRegex(@"^'(?<id>\d{5,}):(?<name>.*?)\(\d+\)' Command: '(?<cmd>.*)'\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex AdminLine();

    [GeneratedRegex(@"^Died: (?<victim>.*?) \((?<vid>[^)]*)\), Killer: (?<killer>.*?) \((?<kid>[^)]*)\) Weapon: (?<weapon>\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex KillLine();

    [GeneratedRegex(@"Distance: (?<dist>[\d.]+) m", RegexOptions.CultureInvariant)]
    private static partial Regex KillDistance();

    [GeneratedRegex(@"^'(?:[\d.:a-fA-F]+ )?(?<id>\d{5,}):(?<name>.*?)\(\d+\)'.*? logged (?<dir>in|out) at: X=(?<x>-?[\d.]+) Y=(?<y>-?[\d.]+) Z=(?<z>-?[\d.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex LoginLine();

    [GeneratedRegex(@"\[LogBunkerLock\] (?<b>\w+) Bunker is Active\. Activated (?<ah>\d+)h (?<am>\d+)m (?<as>\d+)s ago\. X=(?<x>-?[\d.]+) Y=(?<y>-?[\d.]+) Z=(?<z>-?[\d.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex BunkerActive();

    [GeneratedRegex(@"\[LogBunkerLock\] (?<b>\w+) Bunker is Locked\. Locked (?<lh>\d+)h (?<lm>\d+)m (?<ls>\d+)s ago, next Activation in (?<nh>\d+)h (?<nm>\d+)m (?<ns>\d+)s\. X=(?<x>-?[\d.]+) Y=(?<y>-?[\d.]+) Z=(?<z>-?[\d.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex BunkerLocked();

    [GeneratedRegex(@"^\[(?<t>\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}):\d+\]\[\s*\d+\]LogSCUM: Global Stats:\s*[\d.]+ms \(\s*(?<fps>[\d.]+)FPS\).*?\| C:\s*\d+ \(\s*\d+\), P:\s*(?<p>\d+) \(\s*\d+\), Z:\s*(?<z>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex GlobalStats();

    [GeneratedRegex(@"scum\.MaxPlayers:(?<n>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex MaxPlayersLine();
}
