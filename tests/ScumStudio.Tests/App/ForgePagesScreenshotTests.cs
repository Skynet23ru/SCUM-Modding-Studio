using System.Text;
using Avalonia.Headless.XUnit;
using ScumStudio.App.ViewModels;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.Tests.App;

/// <summary>
/// Pictures of the server pages for the README (only written with <c>SCUMSTUDIO_SCREENSHOTS</c>): a made-up server with a few
/// players, deals, deaths and moves, so nothing real is shown.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class ForgePagesScreenshotTests
{
    private static void Utf16(string path, params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new UnicodeEncoding(false, false).GetBytes("\n" + string.Join("\n", lines) + "\n"));
    }

    [AvaloniaFact]
    public void ThePagesOfTheServerToolsAreDrawn()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        var saved = ServerLogs.SavedFolder(server);
        var logs = Path.Combine(saved, "SaveFiles", "Logs");
        Utf16(Path.Combine(logs, "economy_20261027102353.log"), "2026.10.27-10.23.53: Game version: 1.3.4.2.161892",
            "2026.10.27-11.00.00: [Trade] Tradeable (Ear_Plugs (x1)) purchased by Wolf(76561198000000001) for 104 money from trader B_4_Trader, old amount in store was 48, new amount is 47, and effective users online: 2",
            "2026.10.27-11.05.00: [Trade] Tradeable (Tracksuit_Pants_01_01 (health: 100.00, uses: 1)) sold by Raven(76561198000000002) for 38 (38 + 0 worth of contained items) to trader B_4_Trader, old amount in store is 42, new amount is 43, and effective users online: 2",
            "2026.10.27-11.20.00: [Trade] Tradeable (Painkillers_01 (x2)) purchased by Wolf(76561198000000001) for 60 money from trader A_1_Trader, old amount in store was 9, new amount is 7, and effective users online: 2");
        Utf16(Path.Combine(logs, "login_20261027102353.log"), "2026.10.27-10.23.53: Game version: 1.3.4.2.161892",
            "2026.10.27-10.30.00: '127.0.0.1 76561198000000001:Wolf(1)' logged in at: X=-348529.000 Y=-469201.000 Z=300.000",
            "2026.10.27-10.31.00: '127.0.0.1 76561198000000002:Raven(2)' logged in at: X=245735.000 Y=-37787.000 Z=300.000");
        Utf16(Path.Combine(logs, "gameplay_20261027102353.log"), "2026.10.27-10.23.53: Game version: 1.3.4.2.161892",
            "2026.10.27-10.27.51: [LogBunkerLock] A1 Bunker is Active. Activated 00h 33m 53s ago. X=-348529.312 Y=-469201.781 Z=4247.645",
            "2026.10.27-10.27.51: [LogBunkerLock] D1 Bunker is Locked. Locked 00h 33m 53s ago, next Activation in 23h 26m 06s. X=-537889.562 Y=540004.312 Z=81279.648");
        Utf16(Path.Combine(logs, "chat_20261027102353.log"), "2026.10.27-10.23.53: Game version: 1.3.4.2.161892",
            "2026.10.27-10.48.37: '76561198000000001:Wolf(1)' 'Local: anyone near the gas station?'");
        Utf16(Path.Combine(logs, "kill_20261027102353.log"), "2026.10.27-10.23.53: Game version: 1.3.4.2.161892",
            "2026.10.27-12.50.51: Died: Raven (76561198000000002), Killer: BP_Drifter_Lvl_4_C_1 (NPC) Weapon: Weapon_M1911_C [Projectile] S:[KillerLoc : 1, 2, 3 VictimLoc: 4, 5, 6, Distance: 12.4 m]");
        Directory.CreateDirectory(Path.Combine(saved, "Logs"));
        File.WriteAllText(Path.Combine(saved, "Logs", "SCUM.log"),
            "[2026.10.27-10.23.52:935][  0]LogConfig: Setting CVar [[scum.MaxPlayers:64]]\r\n"
            + "[2026.10.27-10.29.58:506][ 17]LogSCUM: Global Stats:  32.6ms ( 30.7FPS),  33.1ms ( 30.2FPS) | C: 329 (326), P:   2 (  2), Z:   7 (  0)\r\n"
            + "[2026.10.27-10.30.00:100][ 18]LogSCUM: Player Wolf joined the server\r\n"
            + "[2026.10.27-10.31.00:100][ 19]LogSCUM: Player Raven joined the server\r\n");
        Directory.CreateDirectory(ServerActivity.Folder(server));
        File.WriteAllLines(ServerActivity.LogFile(server),
        [
            "2026.10.27-18.40.20	move	Wolf	481789	-237087	5136	item Weapon_SKS_C id=2147335116 {health=0/0 weight=0} (was in Level:PersistentLevel) | container VehicleInventoryComponent:Inventory in Rager1_Item_Container_C id=2147336584 of BPC_Rager1_C id=2147336586 @481623 -237235 5072",
            "2026.10.27-18.41.00	pickup	Raven	245735	-37787	300	item Nails_C id=77 {health=100/100 weight=1} (was in Level:PersistentLevel) | container PrisonerInventoryComponent:Inventory in BP_Prisoner_C id=5 of BP_ConZPlayerController_C id=6",
        ]);

        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            foreach (var key in new[] { "console", "dashboard" })
            {
                vm.NavigateTo(key);
                HeadlessUi.Pump();
                HeadlessUi.SaveScreenshot(window, "app-page-" + key);
            }

            var dashboard = (DashboardPageViewModel)vm.CurrentPage!;
            foreach (var (tab, name) in new[] { (1, "deals"), (6, "activity") })
            {
                dashboard.SelectedTab = tab;
                HeadlessUi.Pump();
                HeadlessUi.SaveScreenshot(window, "app-dashboard-" + name);
            }
        }
        finally
        {
            window.Close();
        }
    }
}
