using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Level.Saves;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (2026-10-27): "a tab with the vehicles and their owners: the vehicle id, the owner, the place, the owner's Steam id".
/// The owner is whoever's lock is on the vehicle (the save's <c>item_containers</c>); who used it last comes from the activity log.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class VehiclesTabTests
{
    private static void MakeSave(ServerInstall server, bool withLockTables = true)
    {
        var folder = Path.Combine(ServerLogs.SavedFolder(server), "SaveFiles");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "SCUM.db");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        void Run(string sql)
        {
            using var c = db.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }

        Run("CREATE TABLE entity (id INTEGER PRIMARY KEY, class TEXT, location_x REAL, location_y REAL, location_z REAL)");
        Run("CREATE TABLE vehicle_entity (entity_id INTEGER PRIMARY KEY, item_container_entity_id INTEGER, data BLOB)");
        Run("CREATE TABLE vehicle_spawner (vehicle_entity_id INTEGER PRIMARY KEY, vehicle_asset_id TEXT, vehicle_alias TEXT, vehicle_last_access_time INTEGER, is_vehicle_automatically_created INTEGER, time_spent_in_forbidden_zone REAL, is_vehicle_functional INTEGER)");
        Run("CREATE TABLE user_profile (id INTEGER PRIMARY KEY, user_id TEXT, name TEXT)");
        Run("INSERT INTO user_profile VALUES (1, '76561198104587560', 'Dima'), (2, '76561198000000002', 'Anna')");
        // A Rager that Dima locked and a WolfsWagen nobody owns.
        Run("INSERT INTO entity VALUES (640006, 'Rager1_ES', 481623, -237235, 5072), (640007, 'Rager1_Item_Container_ES', 0, 0, 0), (700001, 'WolfsWagen_ES', -100000, 200000, 4000)");
        Run("INSERT INTO vehicle_entity VALUES (640006, 640007, NULL), (700001, NULL, NULL)");
        Run("INSERT INTO vehicle_spawner VALUES (640006, 'Vehicle:BPC_Rager1', 'Dima''s car', 1793117289, 1, 0, 1), (700001, 'Vehicle:BPC_WolfsWagen', '', 1792779485, 1, 0, 0)");
        if (withLockTables)
        {
            Run("CREATE TABLE item_containers (id INTEGER PRIMARY KEY, map_id INTEGER, user_profile_id INTEGER, runtime_id TEXT, lock_reset_time INTEGER)");
            Run("CREATE TABLE item_container_upgrades (id INTEGER PRIMARY KEY, item_container_id INTEGER, asset TEXT, num_tries INTEGER)");
            Run("INSERT INTO item_containers VALUES (1, 1, 1, '640007', 0)");
            Run("INSERT INTO item_container_upgrades VALUES (1, 1, 'Padlock', 0)");
        }
    }

    [AvaloniaFact]
    public void TheSavesVehiclesAreReadWithTheOwnerWhoseLockIsOnThem()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        MakeSave(server);

        var (vehicles, players) = ServerVehicles.Read(server);

        var rager = vehicles.Single(v => v.Id == 640006);
        Assert.Equal(("BPC_Rager1", "Dima", "76561198104587560", "Padlock", true), (rager.Type, rager.LockOwner, rager.LockOwnerSteamId, rager.Lock, rager.Works));
        Assert.Equal("Dima's car", rager.Alias);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793117289).UtcDateTime, rager.LastUsed);
        var wolfs = vehicles.Single(v => v.Id == 700001);
        Assert.Equal((string.Empty, false), (wolfs.LockOwner, wolfs.Works));   // nobody locked it: no owner
        Assert.Equal(["Anna", "Dima"], players.Select(p => p.Name).Order());
    }

    [AvaloniaFact]
    public void ASaveWithoutTheLockTablesStillListsTheVehicles()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        MakeSave(server, withLockTables: false);

        var (vehicles, _) = ServerVehicles.Read(server);

        Assert.Equal(2, vehicles.Count);
        Assert.All(vehicles, v => Assert.Equal(string.Empty, v.LockOwner));
    }

    [Fact]
    public void TheLastUserOfAVehicleIsTheNewestPlayerSeenWithOneOfItsTypeNearby()
    {
        ActivityEvent Ev(string time, string player, string kind, string detail) => new(time, kind, player, 0, 0, 0, detail);
        var events = new[]
        {
            Ev("2026.10.27-19.00.00", "Anna", "move", "item X_C id=1 {health=1/1} (was in ?) | to VehicleInventoryComponent:Inventory in Rager1_Item_Container_C id=9 of BPC_Rager1_C id=8 @900000 900000 1"),
            Ev("2026.10.27-18.50.00", "Dima", "move", "item Y_C id=2 {health=1/1} (was in ?) | to VehicleInventoryComponent:Inventory in Rager1_Item_Container_C id=9 of BPC_Rager1_C id=8 @481700 -237200 5072"),
            Ev("2026.10.27-18.40.00", "Dima", "drive", "BPC_WolfsWagen_C id=5 @-100100 200050 4000"),
        };
        var mentions = VehicleUsers.Mentions(events);

        var rager = VehicleUsers.UserOf("BPC_Rager1", 481623, -237235, mentions);
        Assert.Equal("Dima", rager?.Player);                                   // Anna's line names a Rager 6 km away: not this one
        Assert.Equal("Dima", VehicleUsers.UserOf("BPC_WolfsWagen", -100000, 200000, mentions)?.Player);
        Assert.Null(VehicleUsers.UserOf("BPC_Laika", 481623, -237235, mentions));
    }

    [AvaloniaFact]
    public void TheVehiclesTabListsThemWithOwnerSteamIdSectorAndFilters()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        MakeSave(server);
        Directory.CreateDirectory(ServerActivity.Folder(server));
        File.WriteAllLines(ServerActivity.LogFile(server),
        [
            "2026.10.27-18.50.00\tmove\tAnna\t481700\t-237200\t5072\titem Y_C id=2 {health=1/1} (was in ?) | to VehicleInventoryComponent:Inventory in Rager1_Item_Container_C id=9 of BPC_Rager1_C id=8 @481700 -237200 5072",
        ]);
        using var page = new DashboardPageViewModel(ctx.Services, live: false, runningProbe: () => true);

        page.SelectedTab = DashboardPageViewModel.VehiclesTab;   // the save is read when the tab is shown

        Assert.Equal(2, page.Vehicles.Count);
        var rager = page.Vehicles.Single(v => v.Id == "640006");
        Assert.Equal(("Rager1", "Dima", "76561198104587560", "Padlock"), (rager.Type, rager.Owner, rager.SteamId, rager.Lock));
        Assert.StartsWith("Anna", rager.LastUser, StringComparison.Ordinal);   // the activity log saw Anna with it
        Assert.Equal("481623 -237235", rager.Place);
        Assert.False(string.IsNullOrEmpty(rager.Sector));
        Assert.Equal(Loc.T("Dash.No"), page.Vehicles.Single(v => v.Id == "700001").Works);

        page.VehicleFilter = "76561198104587560";                // search by the owner's Steam id
        Assert.Equal(["640006"], page.Vehicles.Select(v => v.Id));
        page.VehicleFilter = "wolfs";
        Assert.Equal(["700001"], page.Vehicles.Select(v => v.Id));
    }
}
