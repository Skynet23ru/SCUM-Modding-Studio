using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Level.Saves;
using ScumStudio.Level.World;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.App.ViewModels;

/// <summary>A row of the players list.</summary>
public sealed record DashPlayerRow(string Name, string SteamId, string Since, string Place, string Source);

/// <summary>A row of the deals list.</summary>
public sealed record DashTradeRow(string Time, string Player, string Deal, string Item, string Price, string Trader);

/// <summary>A row of the bunkers list.</summary>
public sealed record DashBunkerRow(string Name, string State, string Detail, string Place, bool Active);

/// <summary>A row of the deaths list.</summary>
public sealed record DashKillRow(string Time, string Killer, string Victim, string Weapon, string Distance);

/// <summary>A row of the chat and admin list.</summary>
public sealed record DashChatRow(string Time, string Player, string Channel, string Text);

/// <summary>A row of the vehicles list.</summary>
/// <param name="Id">The vehicle's id in the save.</param>
/// <param name="Type">Its type.</param>
/// <param name="Owner">The player whose lock is on it (the owner), or empty.</param>
/// <param name="SteamId">The owner's Steam id.</param>
/// <param name="Lock">The lock fitted.</param>
/// <param name="LastUser">The player the activity log saw with it last, with when.</param>
/// <param name="Sector">The map sector.</param>
/// <param name="Place">Where it stands (X Y, cm).</param>
/// <param name="LastUsed">When the game last counted it used.</param>
/// <param name="Works">Whether the game counts it functional.</param>
/// <param name="Alias">The name somebody gave it.</param>
public sealed record DashVehicleRow(string Id, string Type, string Owner, string SteamId, string Lock, string LastUser, string Sector, string Place, string LastUsed, string Works, string Alias);

/// <summary>A row of the activity list.</summary>
public sealed record DashActivityRow(string Time, string Player, string Action, string Item, string ItemId, string Stats, string To, string From, string Place, string Detail)
{
    /// <summary>Where the player was (world cm).</summary>
    public double X { get; init; }

    /// <summary>Where the player was, Y (world cm).</summary>
    public double Y { get; init; }

    /// <summary>Where the player was, Z (world cm).</summary>
    public double Z { get; init; }

    /// <summary>The place of the container's owner (a vehicle), X (world cm), when known.</summary>
    public double ToX { get; init; }

    /// <summary>The place of the container's owner, Y (world cm).</summary>
    public double ToY { get; init; }

    /// <summary>The container owner's place is known.</summary>
    public bool HasToPlace { get; init; }

    /// <summary>The time as the log wrote it (to find the line again).</summary>
    public string LogTime { get; init; } = string.Empty;
}

/// <summary>A row of the events list.</summary>
public sealed record DashEventRow(string Time, string Kind, string Text);

/// <summary>
/// The Dashboard: what is going on on the server, read from its logs (and the studio bridge's live player list): who is
/// online, the server's frame rate, which bunkers are active, who sold and bought what, deaths, chat, admin commands and every
/// other event the game writes. It only reads; the server is never started or stopped from here.
/// </summary>
public sealed partial class DashboardPageViewModel : PageViewModel, IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer? _timer;
    private readonly Func<bool>? _runningProbe;
    private ServerDashboard? _reader;
    private string _readerFor = string.Empty;
    private bool _busy;

    /// <summary>Creates the page; with <paramref name="live"/> it looks at the logs every few seconds.</summary>
    public DashboardPageViewModel(AppServices services, bool live = true, Func<bool>? runningProbe = null)
        : base("dashboard", "Dashboard", "What is going on on the server: players, bunkers, deals, deaths, chat and events, read from its logs.")
    {
        _services = services;
        _runningProbe = runningProbe;
        _services.SettingsChanged += OnSettingsChanged;
        RefreshNow();
        if (live)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
            _timer.Start();
        }
    }

    /// <summary>Players in the game now.</summary>
    public ObservableCollection<DashPlayerRow> Players { get; } = [];

    /// <summary>Sales and purchases, newest first.</summary>
    public ObservableCollection<DashTradeRow> Trades { get; } = [];

    /// <summary>What each player sold and bought.</summary>
    public ObservableCollection<string> TradeSummary { get; } = [];

    /// <summary>The bunkers.</summary>
    public ObservableCollection<DashBunkerRow> Bunkers { get; } = [];

    /// <summary>Deaths, newest first.</summary>
    public ObservableCollection<DashKillRow> Kills { get; } = [];

    /// <summary>Chat, newest first.</summary>
    public ObservableCollection<DashChatRow> Chat { get; } = [];

    /// <summary>Admin commands, newest first.</summary>
    public ObservableCollection<DashChatRow> Admin { get; } = [];

    /// <summary>Other events, newest first.</summary>
    public ObservableCollection<DashEventRow> Events { get; } = [];

    /// <summary>The server's vehicles (from its save), the filter applied.</summary>
    public ObservableCollection<DashVehicleRow> Vehicles { get; } = [];

    private IReadOnlyList<DashVehicleRow> _allVehicles = [];
    private DateTime _vehiclesAt = DateTime.MinValue;

    /// <summary>The tab of the vehicles (it reads the save only when it is shown).</summary>
    public const int VehiclesTab = 3;

    /// <summary>Only the vehicles with every word of this text (an id, a type, an owner, a Steam id, a sector).</summary>
    [ObservableProperty]
    private string _vehicleFilter = string.Empty;

    /// <summary>How many vehicles, how many locked.</summary>
    [ObservableProperty]
    private string _vehiclesSummary = string.Empty;

    partial void OnVehicleFilterChanged(string value) => ShowVehicles();

    partial void OnSelectedTabChanged(int value)
    {
        if (value == VehiclesTab)
        {
            LoadVehicles(force: false);
        }
    }

    /// <summary>Reads the vehicles of the server's save (a copy of it is read, the save is not touched); at most every 20 seconds unless forced.</summary>
    public void LoadVehicles(bool force)
    {
        if (!force && DateTime.UtcNow - _vehiclesAt < TimeSpan.FromSeconds(20))
        {
            return;
        }

        _vehiclesAt = DateTime.UtcNow;
        if (Server is not { } server)
        {
            _allVehicles = [];
            ShowVehicles();
            return;
        }

        try
        {
            var (vehicles, players) = ServerVehicles.Read(server);
            var mentions = VehicleUsers.Mentions(ServerActivity.Read(server, string.Empty, 6000));
            string SteamOf(string name) => players.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.SteamId ?? string.Empty;
            _allVehicles =
            [
                .. vehicles.Select(x =>
                {
                    var use = VehicleUsers.UserOf(x.Type, x.X, x.Y, mentions);
                    return new DashVehicleRow(
                        x.Id.ToString(CultureInfo.InvariantCulture), x.Type.StartsWith("BPC_", StringComparison.Ordinal) ? x.Type[4..] : x.Type,
                        x.LockOwner, x.LockOwnerSteamId.Length > 0 ? x.LockOwnerSteamId : SteamOf(x.LockOwner), x.Lock,
                        use is null ? string.Empty : use.Player + " (" + Clock(use.Time) + ")",
                        IslandMap.CellAt((float)x.X, (float)x.Y)?.ToString() ?? string.Empty, Place(x.X, x.Y),
                        x.LastUsed is { } t ? t.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture) : string.Empty,
                        Loc.T(x.Works ? "Dash.Yes" : "Dash.No"), x.Alias);
                }),
            ];
        }
        catch (IOException ex)
        {
            _services.Logger.LogWarning("Dashboard: vehicles not read: {Message}", ex.Message);
            _allVehicles = [];
        }

        ShowVehicles();
    }

    private void ShowVehicles()
    {
        Vehicles.Clear();
        foreach (var row in _allVehicles.Where(r => LogTail.Matches(string.Join(' ', r.Id, r.Type, r.Owner, r.SteamId, r.Lock, r.LastUser, r.Sector, r.Alias), VehicleFilter)))
        {
            Vehicles.Add(row);
        }

        VehiclesSummary = Loc.F("Dash.Vehicles.Summary", _allVehicles.Count, _allVehicles.Count(r => r.Owner.Length > 0), Vehicles.Count);
    }

    /// <summary>What the players did (picked up, dropped, moved, opened, crafted, built …), newest first, the filter applied.</summary>
    public ObservableCollection<DashActivityRow> Activity { get; } = [];

    private IReadOnlyList<DashActivityRow> _allActivity = [];

    /// <summary>The card of one activity line: all its data, the map with the point and the history of its item.</summary>
    public ActivityCardViewModel CreateCard(DashActivityRow row) =>
        new(_services, row, [.. _allActivity.Where(r => row.ItemId.Length > 0 && r.ItemId == row.ItemId).OrderBy(r => r.LogTime, StringComparer.Ordinal)]);

    /// <summary>Only the activity lines with every word of this text (a player, an item, a container, an action).</summary>
    [ObservableProperty]
    private string _activityFilter = string.Empty;

    partial void OnActivityFilterChanged(string value) => ShowActivity();

    private void ShowActivity()
    {
        Activity.Clear();
        foreach (var row in _allActivity.Where(r => LogTail.Matches(string.Join(' ', r.Player, r.Action, r.Item, r.ItemId, r.Stats, r.To, r.From, r.Detail), ActivityFilter)).Take(400))
        {
            Activity.Add(row);
        }
    }

    /// <summary>0 since the last server start, 1 all the logs the server kept.</summary>
    [ObservableProperty]
    private int _scope;

    /// <summary>Players online, as "3 / 64".</summary>
    [ObservableProperty]
    private string _onlineText = "-";

    /// <summary>The server's frame rate.</summary>
    [ObservableProperty]
    private string _fpsText = "-";

    /// <summary>Active bunkers, as "2 of 12".</summary>
    [ObservableProperty]
    private string _bunkersText = "-";

    /// <summary>Deals and the money, as "5 deals, 1234".</summary>
    [ObservableProperty]
    private string _tradesText = "-";

    /// <summary>Deaths.</summary>
    [ObservableProperty]
    private string _killsText = "-";

    /// <summary>The server is running now (its process exists).</summary>
    [ObservableProperty]
    private string _runningText = string.Empty;

    /// <summary>When the page last read the logs.</summary>
    [ObservableProperty]
    private string _updated = string.Empty;

    /// <summary>A server was found.</summary>
    [ObservableProperty]
    private bool _hasServer;

    /// <summary>The picked tab.</summary>
    [ObservableProperty]
    private int _selectedTab;

    private ServerInstall? Server => ServerInstall.Find(_services.Settings.Load().ServerPaksFolder);

    /// <inheritdoc />
    public override void OnNavigatedTo() => RefreshNow();

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        RefreshNow();
    }

    partial void OnScopeChanged(int value) => RefreshNow();

    /// <summary>Reads the logs again and shows what they say.</summary>
    [RelayCommand]
    private void Refresh() => RefreshNow();

    /// <summary>Reads the logs on a worker and shows the result (the timer's call).</summary>
    private async Task RefreshAsync()
    {
        if (_busy || Server is not { } server)
        {
            return;
        }

        _busy = true;
        try
        {
            var reader = ReaderFor(server);
            var scope = Scope == 1 ? DashboardScope.AllLogs : DashboardScope.CurrentStart;
            var snapshot = await Task.Run(() => reader.Read(scope)).ConfigureAwait(true);
            Show(snapshot);
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => _services.Dispatcher.Invoke(() =>
    {
        _vehiclesAt = DateTime.MinValue;   // another server may be in use: read its save again
        _allVehicles = [];
        ShowVehicles();
        RefreshNow();
    });

    /// <summary>Reads the logs now and shows what they say.</summary>
    public void RefreshNow()
    {
        var server = Server;
        HasServer = server is not null;
        if (server is null)
        {
            Show(null);
            return;
        }

        Show(ReaderFor(server).Read(Scope == 1 ? DashboardScope.AllLogs : DashboardScope.CurrentStart));
        if (SelectedTab == VehiclesTab)
        {
            LoadVehicles(force: false);
        }
    }

    private ServerDashboard ReaderFor(ServerInstall server)
    {
        if (_reader is null || _readerFor != server.Win64)
        {
            _reader = new ServerDashboard(server, _runningProbe ?? _services.Mirror.RunningProbeForActive());
            _readerFor = server.Win64;
        }

        return _reader;
    }

    private static string Clock(string logTime) =>
        ServerDashboard.ParseTime(logTime) is { } t ? t.ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture) : logTime;

    private static string Place(double x, double y) => string.Create(CultureInfo.InvariantCulture, $"{x:0} {y:0}");

    private static string Span(TimeSpan span)
    {
        var s = span < TimeSpan.Zero ? TimeSpan.Zero : span;
        return s.TotalDays >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)s.TotalDays}d {s.Hours:00}h {s.Minutes:00}m")
            : string.Create(CultureInfo.InvariantCulture, $"{s.Hours}h {s.Minutes:00}m");
    }

    private void Show(DashboardSnapshot? s)
    {
        Players.Clear();
        Trades.Clear();
        TradeSummary.Clear();
        Bunkers.Clear();
        Kills.Clear();
        Chat.Clear();
        Admin.Clear();
        Events.Clear();
        _allActivity = [];
        if (s is null)
        {
            OnlineText = FpsText = BunkersText = TradesText = KillsText = "-";
            RunningText = Loc.T("Dash.NoServer");
            Updated = string.Empty;
            return;
        }

        foreach (var p in s.Players)
        {
            Players.Add(new DashPlayerRow(p.Name, p.SteamId, p.Since.Length > 0 ? Clock(p.Since) : string.Empty, p.X == 0 && p.Y == 0 ? string.Empty : Place(p.X, p.Y),
                Loc.T(p.FromBridge ? "Dash.Live" : "Dash.Log")));
        }

        foreach (var t in s.Trades)
        {
            Trades.Add(new DashTradeRow(Clock(t.Time), t.Player, Loc.T(t.Sold ? "Dash.Sold" : "Dash.Bought"), t.Quantity > 1 ? $"{t.Item} x{t.Quantity}" : t.Item,
                t.Price.ToString("N0", CultureInfo.InvariantCulture), t.Trader));
        }

        foreach (var group in s.Trades.GroupBy(t => t.Player, StringComparer.Ordinal).OrderByDescending(g => g.Sum(t => t.Price)))
        {
            var sold = group.Where(t => t.Sold).ToList();
            var bought = group.Where(t => !t.Sold).ToList();
            TradeSummary.Add(Loc.F("Dash.TradeLine", group.Key, sold.Count, sold.Sum(t => t.Price).ToString("N0", CultureInfo.InvariantCulture),
                bought.Count, bought.Sum(t => t.Price).ToString("N0", CultureInfo.InvariantCulture)));
        }

        var now = DateTime.Now;
        foreach (var b in s.Bunkers)
        {
            var detail = b.Active
                ? (b.ChangedAt is { } since ? Loc.F("Dash.ActiveFor", Span(now - since)) : string.Empty)
                : (b.NextActivation is { } next ? (next > now ? Loc.F("Dash.OpensIn", Span(next - now)) : Loc.T("Dash.OpenNow")) : string.Empty);
            Bunkers.Add(new DashBunkerRow(b.Name, Loc.T(b.Active ? "Dash.BunkerActive" : "Dash.BunkerLocked"), detail, Place(b.X, b.Y), b.Active));
        }

        foreach (var k in s.Kills)
        {
            Kills.Add(new DashKillRow(Clock(k.Time), k.KillerIsNpc ? Loc.F("Dash.Npc", k.Killer) : k.Killer, k.Victim, k.Weapon,
                k.Distance >= 0 ? k.Distance.ToString("0.0", CultureInfo.InvariantCulture) + " m" : string.Empty));
        }

        foreach (var c in s.Chat)
        {
            Chat.Add(new DashChatRow(Clock(c.Time), c.Player, c.Channel, c.Text));
        }

        foreach (var a in s.Admin)
        {
            Admin.Add(new DashChatRow(Clock(a.Time), a.Player, string.Empty, a.Command));
        }

        foreach (var e in s.Events)
        {
            Events.Add(new DashEventRow(Clock(e.Time), e.Kind, e.Text));
        }

        _allActivity = [.. s.Activity.Select(a =>
        {
            var f = ServerActivity.Describe(a.Detail);
            return new DashActivityRow(Clock(a.Time), a.Player, a.Kind, f.Item, f.ItemId, f.Stats, f.To, f.From, a.X == 0 && a.Y == 0 ? string.Empty : Place(a.X, a.Y), a.Detail)
            {
                X = a.X, Y = a.Y, Z = a.Z, ToX = f.ToX, ToY = f.ToY, HasToPlace = f.HasToPlace, LogTime = a.Time,
            };
        })];
        ShowActivity();

        var active = s.Bunkers.Count(b => b.Active);
        OnlineText = s.MaxPlayers > 0 ? Loc.F("Dash.Of", s.Players.Count, s.MaxPlayers) : s.Players.Count.ToString(CultureInfo.InvariantCulture);
        FpsText = s.Health is { } h ? h.Fps.ToString("0.0", CultureInfo.InvariantCulture) : "-";
        BunkersText = s.Bunkers.Count > 0 ? Loc.F("Dash.Of", active, s.Bunkers.Count) : "-";
        TradesText = Loc.F("Dash.TradesSum", s.Trades.Count, s.Trades.Sum(t => t.Price).ToString("N0", CultureInfo.InvariantCulture));
        KillsText = s.Kills.Count.ToString(CultureInfo.InvariantCulture);
        RunningText = Loc.T(s.ServerRunning ? "Dash.Running" : "Dash.Stopped");
        Updated = Loc.F("Dash.Updated", s.MadeAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _services.SettingsChanged -= OnSettingsChanged;
        _timer?.Stop();
    }
}
