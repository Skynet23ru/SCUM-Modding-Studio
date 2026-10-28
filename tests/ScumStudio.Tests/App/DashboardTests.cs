using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.App.Views.Pages;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (2026-10-27): "a dashboard where I see everything that happens: how many players, which bunkers are active, who
/// sold and bought what, everything that can be collected from the server logs". The lines below are copied from the logs of
/// the owner's server (game version 1.3.4.2).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class DashboardTests
{
    private static void Utf16(string path, params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new UnicodeEncoding(false, false).GetBytes("\n" + string.Join("\n", lines) + "\n"));
    }

    [Fact]
    public void ASaleAndAPurchaseAreRead()
    {
        var sold = ServerDashboard.ParseTrade("2026.10.25-13.21.35",
            "[Trade] Tradeable (Tracksuit_Pants_01_01 (health: 100.00, uses: 1)) sold by Skynet23rus(76561198104587560) for 38 (38 + 0 worth of contained items) to trader B_4_Trader, old amount in store is 42, new amount is 43, and effective users online: 1");
        var bought = ServerDashboard.ParseTrade("2026.10.25-13.53.30",
            "[Trade] Tradeable (Ear_Plugs (x1)) purchased by Skynet23rus(76561198104587560) for 104 money from trader B_4_Trader, old amount in store was 48, new amount is 47, and effective users online: 1");

        Assert.NotNull(sold);
        Assert.True(sold!.Sold);
        Assert.Equal(("Skynet23rus", "Tracksuit_Pants_01_01", 38L, "B_4_Trader"), (sold.Player, sold.Item, sold.Price, sold.Trader));
        Assert.NotNull(bought);
        Assert.False(bought!.Sold);
        Assert.Equal(("Ear_Plugs", 104L, "B_4_Trader"), (bought.Item, bought.Price, bought.Trader));

        // The balance lines that follow each deal are not deals.
        Assert.Null(ServerDashboard.ParseTrade("2026.10.25-13.21.35", "[Trade] Before selling tradeables to trader B_4_Trader, player Skynet23rus(76561198104587560) had 0 cash, -250 account balance and 0 gold and trader had 10000 funds."));
    }

    [Fact]
    public void LoginsChatAdminCommandsAndDeathsAreRead()
    {
        var login = ServerDashboard.ParseLogin("'127.0.0.1 76561198104587560:Skynet23rus(1)' logged in at: X=567666.000 Y=-225744.000 Z=356.000");
        Assert.Equal((true, "76561198104587560", "Skynet23rus", 567666.0, -225744.0), (login!.Value.In, login.Value.Id, login.Value.Name, login.Value.X, login.Value.Y));
        Assert.False(ServerDashboard.ParseLogin("'127.0.0.1 76561198104587560:Skynet23rus(1)' logged out at: X=1.000 Y=2.000 Z=3.000")!.Value.In);

        var chat = ServerDashboard.ParseChat("2026.10.25-12.48.37", "'76561198104587560:Skynet23rus(1)' 'Local: !time'");
        Assert.Equal(("Skynet23rus", "Local", "!time"), (chat!.Player, chat.Channel, chat.Text));

        var admin = ServerDashboard.ParseAdmin("2026.10.25-13.48.47", "'76561198104587560:Skynet23rus(1)' Command: 'spawnitem MRE_Stew 2'");
        Assert.Equal(("Skynet23rus", "spawnitem MRE_Stew 2"), (admin!.Player, admin.Command));

        var kill = ServerDashboard.ParseKill("2026.10.26-08.50.51",
            "Died: Skynet23rus (76561198104587560), Killer: BP_Drifter_Lvl_4_C_2147332570 (NPC) Weapon: Weapon_M1911_C [Projectile] S:[KillerLoc : 455427.22, -233810.75, 8393.13 VictimLoc: 455238.59, -233921.53, 8393.13, Distance: 2.19 m]");
        Assert.Equal(("Skynet23rus", "BP_Drifter_Lvl_4_C_2147332570", true, "Weapon_M1911_C", 2.19), (kill!.Victim, kill.Killer, kill.KillerIsNpc, kill.Weapon, kill.Distance));
        Assert.Null(ServerDashboard.ParseKill("2026.10.26-08.50.51", "{\"Killer\":{\"ServerLocation\":{\"X\": 455427.21875}}}"));
    }

    [Fact]
    public void ActiveAndLockedBunkersAreRead()
    {
        var active = ServerDashboard.ParseBunker("2026.10.25-12.27.51", "[LogBunkerLock] A1 Bunker is Active. Activated 00h 33m 53s ago. X=-348529.312 Y=-469201.781 Z=4247.645");
        Assert.True(active!.Active);
        Assert.Equal("A1", active.Name);
        Assert.Equal(new DateTime(2026, 10, 25, 11, 53, 58), active.ChangedAt);

        var locked = ServerDashboard.ParseBunker("2026.10.25-12.27.51", "[LogBunkerLock] D1 Bunker is Locked. Locked 00h 33m 53s ago, next Activation in 23h 26m 06s. X=-537889.562 Y=540004.312 Z=81279.648");
        Assert.False(locked!.Active);
        Assert.Equal(new DateTime(2026, 10, 26, 11, 53, 57), locked.NextActivation);

        Assert.Null(ServerDashboard.ParseBunker("2026.10.25-12.27.40", "[LogBunkerLock] A1 Bunker Activated 00h 33m 53s ago"));
    }

    [AvaloniaFact]
    public void TheDashboardCollectsTheLogsOfTheLastServerStart()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        var logs = Path.Combine(ServerLogs.SavedFolder(server), "SaveFiles", "Logs");
        // An older start and the newest one: the page reads only the newest by default.
        Utf16(Path.Combine(logs, "economy_20261025122554.log"), "2026.10.25-12.25.54: Game version: 1.3.4.2.161892",
            "2026.10.25-13.21.35: [Trade] Tradeable (Old_Item (x1)) purchased by Old(76561198000000001) for 5 money from trader A_1_Trader, old amount in store was 1, new amount is 0, and effective users online: 1");
        Utf16(Path.Combine(logs, "economy_20261027102353.log"), "2026.10.27-10.23.53: Game version: 1.3.4.2.161892",
            "2026.10.27-11.00.00: [Trade] Tradeable (Ear_Plugs (x1)) purchased by Dima(76561198104587560) for 104 money from trader B_4_Trader, old amount in store was 48, new amount is 47, and effective users online: 1",
            "2026.10.27-11.05.00: [Trade] Tradeable (Tracksuit_Pants_01_01 (health: 100.00, uses: 1)) sold by Dima(76561198104587560) for 38 (38 + 0 worth of contained items) to trader B_4_Trader, old amount in store is 42, new amount is 43, and effective users online: 1");
        Utf16(Path.Combine(logs, "login_20261027102353.log"), "2026.10.27-10.23.53: Game version: 1.3.4.2.161892",
            "2026.10.27-10.30.00: '127.0.0.1 76561198104587560:Dima(1)' logged in at: X=100.000 Y=200.000 Z=300.000",
            "2026.10.27-10.31.00: '127.0.0.1 76561198104587561:Anna(2)' logged in at: X=1.000 Y=2.000 Z=3.000",
            "2026.10.27-10.40.00: '127.0.0.1 76561198104587561:Anna(2)' logged out at: X=1.000 Y=2.000 Z=3.000");
        Utf16(Path.Combine(logs, "gameplay_20261027102353.log"), "2026.10.27-10.23.53: Game version: 1.3.4.2.161892",
            "2026.10.27-10.27.51: [LogBunkerLock] A1 Bunker is Active. Activated 00h 33m 53s ago. X=-348529.312 Y=-469201.781 Z=4247.645",
            "2026.10.27-10.27.51: [LogBunkerLock] D1 Bunker is Locked. Locked 00h 33m 53s ago, next Activation in 23h 26m 06s. X=-537889.562 Y=540004.312 Z=81279.648",
            "2026.10.27-11.47.26: [LogTrap] Armed. User: Dima (1, 76561198104587560). Trap name: Anti-personnel mine. Location: X=245735.703 Y=-37787.242 Z=24379.703");
        Utf16(Path.Combine(logs, "chat_20261027102353.log"), "2026.10.27-10.23.53: Game version: 1.3.4.2.161892", "2026.10.27-10.48.37: '76561198104587560:Dima(1)' 'Local: !time'");
        Utf16(Path.Combine(logs, "kill_20261027102353.log"), "2026.10.27-10.23.53: Game version: 1.3.4.2.161892",
            "2026.10.27-12.50.51: Died: Dima (76561198104587560), Killer: BP_Drifter_Lvl_4_C_1 (NPC) Weapon: Weapon_M1911_C [Projectile] S:[KillerLoc : 1, 2, 3 VictimLoc: 4, 5, 6, Distance: 2.19 m]");
        Utf16(Path.Combine(logs, "violations_20261027102353.log"), "2026.10.27-10.23.53: Game version: 1.3.4.2.161892", "2026.10.27-12.00.00: Speed hack suspected: Dima");
        Directory.CreateDirectory(Path.Combine(ServerLogs.SavedFolder(server), "Logs"));
        File.WriteAllText(Path.Combine(ServerLogs.SavedFolder(server), "Logs", "SCUM.log"),
            "[2026.10.27-10.23.52:935][  0]LogConfig: Setting CVar [[scum.MaxPlayers:64]]\r\n"
            + "[2026.10.27-10.29.58:506][ 17]LogSCUM: Global Stats:  32.6ms ( 30.7FPS),  33.1ms ( 30.2FPS),  34.7ms ( 28.8FPS) | C: 329 (326), P:   1 (  1), Z:   7 (  0), R:   0 (  0), S:   0 (  0), A: V: 4950 | LoD3:303\r\n");

        using var page = new DashboardPageViewModel(ctx.Services, live: false, runningProbe: () => true);

        Assert.True(page.HasServer);
        Assert.Equal(["Dima", "Dima"], page.Trades.Select(t => t.Player));
        Assert.DoesNotContain(page.Trades, t => t.Player == "Old");
        Assert.Equal("2", page.TradesText.Split(' ')[0]);
        Assert.Contains("Dima", Assert.Single(page.TradeSummary), StringComparison.Ordinal);
        Assert.Equal(["Dima"], page.Players.Select(p => p.Name)); // Anna logged out again
        Assert.Equal("30.7", page.FpsText);
        Assert.Equal(["A1", "D1"], page.Bunkers.Select(b => b.Name));
        Assert.Equal(1, page.Bunkers.Count(b => b.Active));
        Assert.Equal("1 / 2", page.BunkersText.Replace(Loc.F("Dash.Of", 1, 2), "1 / 2", StringComparison.Ordinal));
        Assert.Single(page.Kills);
        Assert.True(page.Kills[0].Killer.Contains("BP_Drifter", StringComparison.Ordinal));
        Assert.Single(page.Chat);
        Assert.Contains(page.Events, e => e.Kind == "violations" && e.Text.Contains("Speed hack", StringComparison.Ordinal));
        Assert.Contains(page.Events, e => e.Kind == "gameplay" && e.Text.Contains("LogTrap", StringComparison.Ordinal));

        page.Scope = 1; // all logs: the older deal counts too
        Assert.Equal(3, page.Trades.Count);
    }

    [AvaloniaFact]
    public void WithoutAServerTheDashboardSaysSo()
    {
        using var ctx = AppTestContext.Create(inline: false);

        using var page = new DashboardPageViewModel(ctx.Services, live: false);

        Assert.False(page.HasServer);
        Assert.Empty(page.Players);
        Assert.Equal(Loc.T("Dash.NoServer"), page.RunningText);
    }

    [AvaloniaFact]
    public void TheBridgesLivePlayerListWinsOverTheLoginLog()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        Directory.CreateDirectory(ServerBridge.Folder(server));
        File.WriteAllText(ServerBridge.PlayersFile(server), "Dima\t100\t200\t300\nAnna\t5\t6\t7\n");
        File.WriteAllText(ServerBridge.EventsFile(server), "2099.01.01-00.00.00\tjoin\tDima\t100\t200\t300\n");
        // The login log knows Dima's Steam id; the bridge's list has only names.
        var logs = Path.Combine(ServerLogs.SavedFolder(server), "SaveFiles", "Logs");
        Utf16(Path.Combine(logs, "login_20261027102353.log"), "2026.10.27-10.23.53: Game version: 1.3.4.2.161892",
            "2026.10.27-10.30.00: '127.0.0.1 76561198104587560:Dima(1)' logged in at: X=100.000 Y=200.000 Z=300.000");

        using var page = new DashboardPageViewModel(ctx.Services, live: false);

        Assert.Equal(["Anna", "Dima"], page.Players.Select(p => p.Name));
        Assert.Equal("100 200", page.Players.Single(p => p.Name == "Dima").Place);
        Assert.Equal("76561198104587560", page.Players.Single(p => p.Name == "Dima").SteamId);
        Assert.Equal(Loc.T("Dash.Live"), page.Players[0].Source);
        Assert.Contains(page.Events, e => e.Kind == "lua" && e.Text.StartsWith("join Dima", StringComparison.Ordinal));
        Assert.Equal(["Anna", "Dima"], ServerBridge.ReadPlayers(server)); // the name-only reader still works on the new lines
    }

    [AvaloniaFact]
    public void TheViewHasTooltipsOnItsControls()
    {
        using var ctx = AppTestContext.Create(inline: false);
        ServerPageTests.FakeServer(ctx, withUe4ss: true);
        using var page = new DashboardPageViewModel(ctx.Services, live: false);
        var view = new DashboardPageView { DataContext = page };
        var window = new Window { Content = view };
        window.Show();

        foreach (var name in new[] { "ScopePicker", "RefreshButton", "CardOnline", "CardFps", "CardBunkers", "CardTrades", "CardKills", "PlayersTab", "TradeTab", "BunkersTab", "KillsTab", "ChatTab", "EventsTab" })
        {
            var control = view.FindControl<Control>(name);
            Assert.NotNull(control);
            Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(control) as string), name);
        }
    }
}
