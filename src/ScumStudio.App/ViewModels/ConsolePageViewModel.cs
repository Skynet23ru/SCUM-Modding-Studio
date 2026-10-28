using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.App.ViewModels;

/// <summary>One line of the console.</summary>
/// <param name="Text">The line.</param>
/// <param name="Level"><c>error</c>, <c>warning</c> or empty (it sets the colour).</param>
public sealed record ConsoleLine(string Text, string Level)
{
    /// <summary>True for a line to paint as an error.</summary>
    public bool IsError => Level == "error";

    /// <summary>True for a line to paint as a warning.</summary>
    public bool IsWarning => Level == "warning";
}

/// <summary>A log the console can follow.</summary>
/// <param name="Id">The log's key.</param>
/// <param name="Title">Its name in the current language.</param>
public sealed record ConsoleSource(string Id, string Title);

/// <summary>
/// The Console page: the server's logs as they are written (the engine log, the game's chat, admin, login, kill … logs,
/// UE4SS and the bridge), and a box to send a message to one player or to everyone and an admin command. Sending goes through
/// the studio bridge (<see cref="ServerBridge"/>), a Lua mod inside the server; the page never starts or stops the server.
/// </summary>
public sealed partial class ConsolePageViewModel : PageViewModel, IDisposable
{
    private const int MaxShown = 4000;
    private const int MaxKept = 20000;

    private readonly AppServices _services;
    private readonly List<ConsoleLine> _kept = [];
    private readonly DispatcherTimer? _timer;
    private LogTail? _tail;
    private int _ticks;

    /// <summary>Creates the page; it follows the log of the server in the settings, twice a second while it is open.</summary>
    public ConsolePageViewModel(AppServices services, bool live = true)
        : base("console", "Console", "The server's logs as they are written, and messages and admin commands sent into the game.")
    {
        _services = services;
        _services.SettingsChanged += OnSettingsChanged;
        Refresh();
        if (live)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }
    }

    /// <summary>The logs that exist.</summary>
    public ObservableCollection<ConsoleSource> Sources { get; } = [];

    /// <summary>The log followed.</summary>
    [ObservableProperty]
    private ConsoleSource? _selectedSource;

    /// <summary>The lines shown (the filter applied, the last ones).</summary>
    public ObservableCollection<ConsoleLine> Lines { get; } = [];

    /// <summary>Only the lines with every word of this text.</summary>
    [ObservableProperty]
    private string _filter = string.Empty;

    /// <summary>New lines are held back (the view stays where it is).</summary>
    [ObservableProperty]
    private bool _isPaused;

    /// <summary>The view follows the newest line.</summary>
    [ObservableProperty]
    private bool _follow = true;

    /// <summary>The players online (from the bridge).</summary>
    public ObservableCollection<string> Players { get; } = [];

    /// <summary>The player a message goes to.</summary>
    [ObservableProperty]
    private string? _selectedPlayer;

    /// <summary>What is sent: 0 an admin command, 1 a message to the player picked, 2 a message to everyone, 3 an item given to the player picked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsPlayer), nameof(TextHint), nameof(IsGive))]
    private int _mode = 1;

    /// <summary>The text to send.</summary>
    [ObservableProperty]
    private string _sendText = string.Empty;

    /// <summary>What the last action did.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>A server was found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBridgeNote), nameof(CanSend))]
    private bool _hasServer;

    /// <summary>The bridge mod is installed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBridgeNote), nameof(CanSend))]
    private bool _bridgeInstalled;

    /// <summary>The line over the log (the server, how many lines).</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>The message mode needs a player.</summary>
    public bool NeedsPlayer => Mode is 1 or 3;

    /// <summary>The mode that gives an item to the player picked (the item shows on the ground at the player's place).</summary>
    public bool IsGive => Mode == 3;

    /// <summary>The item to give: its id as <c>SpawnItem</c> takes it (<c>MRE_Stew</c>).</summary>
    [ObservableProperty]
    private string _giveItem = string.Empty;

    /// <summary>How many to give.</summary>
    [ObservableProperty]
    private string _giveCount = "1";

    /// <summary>The ids of the game's items for the suggestions (empty until the game files are open).</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _itemNames = [];

    /// <summary>Reads the item ids of the game files for the suggestions.</summary>
    private void LoadItemNames()
    {
        if (ItemNames.Count > 0 || _services.Workspace.Catalog is not { } catalog)
        {
            return;
        }

        try
        {
            ItemNames = [.. ModdableAssets.Find(catalog).Where(a => a.Kind != ModdableKind.Vehicle).Select(a => a.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            ItemNames = [];
        }
    }

    partial void OnModeChanged(int value)
    {
        if (value == 3)
        {
            LoadItemNames();
        }
    }

    /// <summary>The box's hint for the mode.</summary>
    public string TextHint => Loc.T(Mode switch { 0 => "Console.Text.Admin", 1 => "Console.Text.Message", 3 => "Console.Text.Give", _ => "Console.Text.All" });

    /// <summary>The note about a missing server or bridge is shown.</summary>
    public bool ShowBridgeNote => !HasServer || !BridgeInstalled;

    /// <summary>Sending is possible.</summary>
    public bool CanSend => HasServer && BridgeInstalled;

    private ServerInstall? Server => ServerInstall.Find(_services.Settings.Load().ServerPaksFolder);

    /// <inheritdoc />
    public override void OnNavigatedTo() => Refresh();

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(TextHint));
        Refresh();
    }

    private string? _serverKey;

    private void OnSettingsChanged(object? sender, EventArgs e) => _services.Dispatcher.Invoke(Refresh);

    /// <summary>Looks for the server, its logs and the bridge again.</summary>
    [RelayCommand]
    private void Refresh()
    {
        var server = Server;
        if (server?.Win64 != _serverKey)
        {
            // Another server is in use: nothing of the last one's logs or players stays.
            _serverKey = server?.Win64;
            _kept.Clear();
            Lines.Clear();
            _tail = null;
            Sources.Clear();
            Players.Clear();
            SelectedSource = null;
        }

        HasServer = server is not null;
        BridgeInstalled = server is not null && ServerBridge.IsInstalled(server);
        var found = server is null ? [] : ServerLogs.Sources(server);
        var keep = SelectedSource?.Id;
        var titles = found.Select(s => new ConsoleSource(s.Id, SourceTitle(s.Id))).ToList();
        if (!Sources.Select(s => s.Id).SequenceEqual(titles.Select(t => t.Id)) || Sources.Zip(titles).Any(p => p.First.Title != p.Second.Title))
        {
            Sources.Clear();
            foreach (var t in titles)
            {
                Sources.Add(t);
            }
        }

        var pick = Sources.FirstOrDefault(s => s.Id == keep) ?? Sources.FirstOrDefault();
        if (!Equals(pick, SelectedSource))
        {
            SelectedSource = pick;
        }
        else
        {
            FollowNewest(found);
        }

        RefreshPlayers(server);
    }

    private static string SourceTitle(string id) => id switch
    {
        "server" => Loc.T("Console.Src.server"),
        "ue4ss" => Loc.T("Console.Src.ue4ss"),
        "bridge" => Loc.T("Console.Src.bridge"),
        _ => char.ToUpperInvariant(id[0]) + id[1..],
    };

    partial void OnSelectedSourceChanged(ConsoleSource? value)
    {
        _kept.Clear();
        Lines.Clear();
        _tail = null;
        if (value is not null && Server is { } server && ServerLogs.Sources(server).FirstOrDefault(s => s.Id == value.Id) is { } source)
        {
            _tail = new LogTail(source.Path);
            Pull();
        }

        UpdateSummary();
    }

    partial void OnFilterChanged(string value) => Rebuild();

    partial void OnIsPausedChanged(bool value)
    {
        if (!value)
        {
            Rebuild();
        }
    }

    /// <summary>One look at the log: the new lines are added (unless paused) and the player list is read now and then.</summary>
    public void Tick()
    {
        _ticks++;
        if (_ticks % 8 == 0 && Server is { } server)
        {
            // A new server start writes new game logs: follow the newest file of the kind.
            var found = ServerLogs.Sources(server);
            FollowNewest(found);
            RefreshPlayers(server);
        }

        Pull();
    }

    /// <summary>Follows the newest file of the log shown (a server start made a new one).</summary>
    private void FollowNewest(IReadOnlyList<LogSource> found)
    {
        if (SelectedSource is { } shown && _tail is not null && found.FirstOrDefault(s => s.Id == shown.Id) is { } now
            && !string.Equals(now.Path, _tail.Path, StringComparison.OrdinalIgnoreCase))
        {
            _tail = new LogTail(now.Path);
        }
    }

    private void Pull()
    {
        if (_tail is null)
        {
            return;
        }

        var fresh = _tail.ReadNew();
        if (fresh.Count == 0)
        {
            return;
        }

        foreach (var text in fresh)
        {
            _kept.Add(new ConsoleLine(text, LogTail.LevelOf(text)));
        }

        if (_kept.Count > MaxKept)
        {
            _kept.RemoveRange(0, _kept.Count - MaxKept);
        }

        if (IsPaused)
        {
            return;
        }

        foreach (var text in fresh)
        {
            if (LogTail.Matches(text, Filter))
            {
                Lines.Add(new ConsoleLine(text, LogTail.LevelOf(text)));
            }
        }

        while (Lines.Count > MaxShown)
        {
            Lines.RemoveAt(0);
        }

        UpdateSummary();
    }

    private void Rebuild()
    {
        Lines.Clear();
        foreach (var line in _kept.Where(l => LogTail.Matches(l.Text, Filter)).TakeLast(MaxShown))
        {
            Lines.Add(line);
        }

        UpdateSummary();
    }

    private void UpdateSummary() =>
        Summary = SelectedSource is null ? Loc.T("Console.NoLogs") : Loc.F("Console.Summary", SelectedSource.Title, Lines.Count);

    private void RefreshPlayers(ServerInstall? server)
    {
        var names = server is null ? [] : ServerBridge.ReadPlayers(server);
        if (Players.SequenceEqual(names))
        {
            return;
        }

        var keep = SelectedPlayer;
        Players.Clear();
        foreach (var n in names)
        {
            Players.Add(n);
        }

        SelectedPlayer = keep is not null && names.Contains(keep) ? keep : names.FirstOrDefault();
    }

    /// <summary>Empties the view (the log itself is not touched).</summary>
    [RelayCommand]
    private void Clear()
    {
        _kept.Clear();
        Lines.Clear();
        UpdateSummary();
    }

    /// <summary>The shown lines as one text, for the clipboard.</summary>
    public string ShownText() => string.Join(Environment.NewLine, Lines.Select(l => l.Text));

    /// <summary>Writes the bridge mod into the server (it loads when the server starts).</summary>
    [RelayCommand]
    private void InstallBridge()
    {
        if (Server is not { } server)
        {
            return;
        }

        if (!server.HasUe4ss)
        {
            Status = Loc.T("Console.NeedUe4ss");
            return;
        }

        try
        {
            ServerBridge.Install(server);
            Status = Loc.T("Console.BridgeInstalled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = Loc.F("Console.SendFailed", ex.Message);
        }

        Refresh();
    }

    /// <summary>Sends the text (an admin command, a message to the player picked or to everyone) to the bridge.</summary>
    [RelayCommand]
    private void Send()
    {
        if (Server is not { } server)
        {
            return;
        }

        if (Mode == 3)
        {
            Give(server);
            return;
        }

        try
        {
            var kind = Mode switch { 0 => BridgeKind.Admin, 1 => BridgeKind.Message, _ => BridgeKind.Say };
            ServerBridge.Enqueue(server, kind, kind == BridgeKind.Message ? SelectedPlayer : null, SendText);
            Status = Loc.F("Console.Sent", SendText.Trim());
            SendText = string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Status = Loc.F("Console.SendFailed", ex.Message);
        }
    }

    /// <summary>
    /// Gives <see cref="GiveItem"/> to the player picked: an admin <c>SpawnItem</c> with the player's place (the item lies on the
    /// ground under the player), then a message to the player. The place is the one the bridge last wrote.
    /// </summary>
    private void Give(ServerInstall server)
    {
        var item = GiveItem.Trim();
        if (item.Length is 0 or > 80 || !item.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
        {
            Status = Loc.T("Console.Give.BadItem");
            return;
        }

        if (!int.TryParse(GiveCount.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 1 or > 1000)
        {
            Status = Loc.T("Console.Give.BadCount");
            return;
        }

        var player = SelectedPlayer;
        var at = player is null ? null : ServerBridge.ReadPlayerPositions(server).FirstOrDefault(p => string.Equals(p.Name, player, StringComparison.Ordinal));
        if (player is null || at is null || (at.X == 0 && at.Y == 0))
        {
            Status = Loc.F("Console.Give.NoPlace", player ?? "?");
            return;
        }

        try
        {
            var place = string.Create(CultureInfo.InvariantCulture, $"X={at.X:0} Y={at.Y:0} Z={at.Z:0}");
            ServerBridge.Enqueue(server, BridgeKind.Admin, null, $"SpawnItem {item} {count.ToString(CultureInfo.InvariantCulture)} Location \"{place}\"");
            var message = SendText.Trim().Length > 0 ? SendText.Trim() : Loc.F("Console.Give.Message", item, count);
            ServerBridge.Enqueue(server, BridgeKind.Message, player, message);
            Status = Loc.F("Console.Give.Sent", item, count, player, place);
            SendText = string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Status = Loc.F("Console.SendFailed", ex.Message);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _services.SettingsChanged -= OnSettingsChanged;
        _timer?.Stop();
    }
}
