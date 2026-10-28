using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (2026-10-27): "collect who picked up what, from which vehicle, an extensive log of everything that happens, by
/// scripts; the script is always added to any server the app connects to". The activity mod logs it inside the server; the
/// Studio puts its mods on every server it knows and shows the log on the Dashboard.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class ActivityTests
{
    private const string Line = "2026.10.27-18.20.01\tpickup\tDima\t566659\t-224706\t392\tBP_Item_MRE_C:MRE_Stew_C_2147 | BPC_Rager_C:Trunk_C_17 || by BP_Prisoner_C:BP_Prisoner_C_21";

    [Fact]
    public void ALogLineIsReadWithItsPlayerPlaceAndDetail()
    {
        var e = ServerActivity.Parse(Line);

        Assert.NotNull(e);
        Assert.Equal(("pickup", "Dima", 566659.0, -224706.0, 392.0), (e!.Kind, e.Player, e.X, e.Y, e.Z));
        Assert.Contains("BPC_Rager_C:Trunk_C_17", e.Detail, StringComparison.Ordinal);
        Assert.Null(ServerActivity.Parse("not a log line"));
        Assert.Null(ServerActivity.Parse("2026.10.27-18.20.01\tpickup\tDima")); // too short
    }

    [Fact]
    public void TheActivityModPassesTheLuaCheckAndHooksTheFunctionsTheGameHas()
    {
        Assert.DoesNotContain(LuaCheck.Run(ServerActivity.Code), f => f.Level == LuaFindingLevel.Error);
        foreach (var function in new[] { "Server_CharacterInventoryComponent_PickupItem", "Server_InventoryComponent_AddOrMoveEntry", "PickupItemOnServer", "Server_PlayerSellTradeables", "Server_DropItemFromHands" })
        {
            Assert.Contains(function, ServerActivity.Code, StringComparison.Ordinal);
        }

        // A hook must never read a player itself: the work is deferred to the game thread.
        Assert.Contains("ExecuteWithDelay", ServerActivity.Code, StringComparison.Ordinal);
        Assert.Contains("ExecuteInGameThread", ServerActivity.Code, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TheStudiosModsGoToEveryServerWithUe4ssAndStayUpToDate()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);

        var written = ServerAutoMods.Ensure(server);

        Assert.Equal([ServerBridge.ModName, ServerActivity.ModName], written);
        var store = new ServerModStore(server);
        Assert.Equal(ServerActivity.Code, store.Read(ServerActivity.ModName));
        Assert.Empty(ServerAutoMods.Ensure(server)); // nothing to do the second time

        // A new version of the Studio replaces the old script.
        store.Save(ServerBridge.ModName, "-- old version");
        Assert.Equal([ServerBridge.ModName], ServerAutoMods.Ensure(server));
        Assert.Equal(ServerBridge.Code, store.Read(ServerBridge.ModName));

        // A mod the owner switched off stays off.
        store.SetEnabled(ServerActivity.ModName, false);
        store.Save(ServerBridge.ModName, "-- old version");
        Assert.Equal([ServerBridge.ModName], ServerAutoMods.Ensure(server));
        Assert.False(store.List().Single(m => m.Name == ServerActivity.ModName).Enabled);
    }

    [AvaloniaFact]
    public void WithoutUe4ssNothingIsWritten()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: false);

        Assert.Empty(ServerAutoMods.Ensure(server));
        Assert.False(Directory.Exists(ServerActivity.Folder(server)));
    }

    [AvaloniaFact]
    public void TheMainWindowPutsTheModsOnTheConfiguredServerAtStartAndWhenSettingsChange()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);

        using var vm = new MainWindowViewModel(ctx.Services);

        Assert.True(File.Exists(Path.Combine(ServerActivity.Folder(server), "Scripts", "main.lua")));
        Assert.True(File.Exists(Path.Combine(ServerBridge.Folder(server), "Scripts", "main.lua")));
    }

    [Fact]
    public void TheDetailOfARealLineIsTakenApartIntoItsColumns()
    {
        // Lines of the owner's server (2026-10-27): an item put into a vehicle from the player's pockets, one picked up from the world.
        var moved = ServerActivity.Describe("item Weapon_SKS_C id=2147335116 {health=0/0 weight=0} (was in Level:PersistentLevel) | container VehicleInventoryComponent:Inventory in Rager1_Item_Container_C id=2147336584 of BPC_Rager1_C id=2147336586 @481623 -237235 5072");
        Assert.Equal(("Weapon_SKS_C", "2147335116", "health=0/0 weight=0", "Vehicle BPC_Rager1_C #2147336586", "World"), (moved.Item, moved.ItemId, moved.Stats, moved.To, moved.From));

        var fromTrunk = ServerActivity.Describe("item Magazine_Clip_SKS_C id=2147335127 {health=100/100 weight=13} (was in Rager1_Item_Container_C id=2147336584 of BPC_Rager1_C id=2147336586 @481623 -237235 5072) | container PrisonerInventoryComponent:Inventory in BP_Prisoner_C id=2147339367 of BP_ConZPlayerController_C id=2147344063");
        Assert.Equal(("Magazine_Clip_SKS_C", "Vehicle BPC_Rager1_C #2147336586", "Player BP_ConZPlayerController_C #2147344063"), (fromTrunk.Item, fromTrunk.From, fromTrunk.To));

        // A line of the first version (class names only) and a line without an item give what they have.
        Assert.Equal(string.Empty, ServerActivity.Describe("VehicleInventoryComponent:Inventory | 4 || by BP_ConZPlayerController_C:BP_ConZPlayerController_C_2147344063").Item);
        var none = ServerActivity.Describe(string.Empty);
        Assert.Equal(("", "", "", "", ""), (none.Item, none.ItemId, none.Stats, none.To, none.From));
    }

    [Fact]
    public void TheCurrentLineFormatWithAnExplicitFromAndToIsTakenApart()
    {
        // Ammunition moved from the player's pockets into the trunk (the item remembered where it was last seen).
        var moved = ServerActivity.Describe("item Cal_7_62x39mm_TR_C id=2147334790 {health=100.00/100 weight=326.00} (was in PrisonerInventoryComponent:Inventory in BP_Prisoner_C id=2147339367 of BP_ConZPlayerController_C id=2147344063) | to VehicleInventoryComponent:Inventory in Rager1_Item_Container_C id=2147336584 of BPC_Rager1_C id=2147336586 @481623 -237235 5072");
        Assert.Equal(("Cal_7_62x39mm_TR_C", "Player BP_ConZPlayerController_C #2147344063", "Vehicle BPC_Rager1_C #2147336586"), (moved.Item, moved.From, moved.To));

        var dropped = ServerActivity.Describe("item WeaponSights_ImprovisedScope_C id=2147334834 {health=100.00/100 weight=150} (was in PrisonerInventoryComponent:Inventory in BP_Prisoner_C id=2147339367 of BP_ConZPlayerController_C id=2147344063) | to Level:PersistentLevel");
        Assert.Equal("World", dropped.To);
        Assert.Equal("?", ServerActivity.Describe("item X_C id=1 {health=1/1} (was in ?)").From); // an unknown place stays a plain question mark
    }

    [AvaloniaFact]
    public void TheActivityTableHasItsColumnsAndSearchesThem()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        Directory.CreateDirectory(ServerActivity.Folder(server));
        File.WriteAllLines(ServerActivity.LogFile(server),
        [
            "2026.10.27-18.40.20	move	Dima	481789	-237087	5136	item Weapon_SKS_C id=2147335116 {health=0/0 weight=0} (was in Level:PersistentLevel) | container VehicleInventoryComponent:Inventory in Rager1_Item_Container_C id=2147336584 of BPC_Rager1_C id=2147336586 @481623 -237235 5072",
            "2026.10.27-18.41.00	pickup	Anna	1	2	3	item Nails_C id=77 {health=100/100 weight=1} (was in Level:PersistentLevel) | container PrisonerInventoryComponent:Inventory in BP_Prisoner_C id=5 of BP_ConZPlayerController_C id=6",
        ]);

        using var page = new DashboardPageViewModel(ctx.Services, live: false, runningProbe: () => true);

        var row = page.Activity.Single(r => r.Player == "Dima");
        Assert.Equal(("move", "Weapon_SKS_C", "2147335116", "Vehicle BPC_Rager1_C #2147336586", "World"), (row.Action, row.Item, row.ItemId, row.To, row.From));
        page.ActivityFilter = "2147336586";   // search by the vehicle's id
        Assert.Equal(["Dima"], page.Activity.Select(r => r.Player));
        page.ActivityFilter = "nails anna";
        Assert.Equal(["Anna"], page.Activity.Select(r => r.Player));
    }

    [Fact]
    public void ThePlaceOfTheVehicleIsTakenFromTheLine()
    {
        var f = ServerActivity.Describe("item Weapon_SKS_C id=1 {health=0/0 weight=0} (was in ?) | to VehicleInventoryComponent:Inventory in Rager1_Item_Container_C id=2 of BPC_Rager1_C id=3 @481623 -237235 5072");

        Assert.True(f.HasToPlace);
        Assert.Equal((481623.0, -237235.0), (f.ToX, f.ToY));
        Assert.False(ServerActivity.Describe("item X_C id=1 {health=1/1} (was in ?) | to Level:PersistentLevel").HasToPlace);
    }

    [AvaloniaFact]
    public void TheCardOfALineHasItsDataItsMapPointsAndTheHistoryOfTheItem()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        Directory.CreateDirectory(ServerActivity.Folder(server));
        File.WriteAllLines(ServerActivity.LogFile(server),
        [
            "2026.10.27-18.40.00\tpickup\tDima\t481700\t-237000\t5100\titem Weapon_SKS_C id=2147335116 {health=0/0 weight=0} (was in Level:PersistentLevel) | to PrisonerInventoryComponent:Inventory in BP_Prisoner_C id=5 of BP_ConZPlayerController_C id=6",
            "2026.10.27-18.40.20\tmove\tDima\t481789\t-237087\t5136\titem Weapon_SKS_C id=2147335116 {health=0/0 weight=0} (was in PrisonerInventoryComponent:Inventory in BP_Prisoner_C id=5 of BP_ConZPlayerController_C id=6) | to VehicleInventoryComponent:Inventory in Rager1_Item_Container_C id=2 of BPC_Rager1_C id=3 @481623 -237235 5072",
            "2026.10.27-18.41.00\tpickup\tAnna\t1\t2\t3\titem Nails_C id=77 {health=100/100 weight=1} (was in Level:PersistentLevel)",
        ]);
        using var page = new DashboardPageViewModel(ctx.Services, live: false, runningProbe: () => true);
        var row = page.Activity.Single(r => r.Action == "move");

        using var card = page.CreateCard(row);

        Assert.Equal("Dima", card.Row.Player);
        Assert.True(card.HasPoint);
        Assert.Equal("481789 -237087 5136", card.Coordinates);
        Assert.Equal(2, card.Markers.Count);                      // the player and the vehicle
        Assert.Equal(["pickup", "move"], card.History.Select(h => h.Action)); // the history of the same item, oldest first
        Assert.Contains("Vehicle BPC_Rager1_C #3", card.History[1].Route, StringComparison.Ordinal);
        Assert.Contains("Dima", card.Title, StringComparison.Ordinal);

        // A line of an item nobody else touched has no history to show beyond itself.
        using var other = page.CreateCard(page.Activity.Single(r => r.Player == "Anna"));
        Assert.Single(other.History);
        Assert.Single(other.Markers);
    }

    [AvaloniaFact]
    public void ClickingALineOpensItsCardWithTheMapAndTooltips()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        Directory.CreateDirectory(ServerActivity.Folder(server));
        File.WriteAllLines(ServerActivity.LogFile(server),
        [
            "2026.10.27-18.40.20\tmove\tDima\t481789\t-237087\t5136\titem Weapon_SKS_C id=2147335116 {health=0/0 weight=0} (was in Level:PersistentLevel) | to Level:PersistentLevel",
        ]);
        using var page = new DashboardPageViewModel(ctx.Services, live: false, runningProbe: () => true);
        using var card = page.CreateCard(page.Activity.Single());
        var window = new ScumStudio.App.Views.ActivityCardWindow { DataContext = card };
        window.Show();

        foreach (var name in new[] { "CopyCoordsButton", "CenterButton", "CardMap", "HistoryList", "RawBox" })
        {
            var control = window.FindControl<Control>(name);
            Assert.NotNull(control);
            Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(control) as string), name);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void TheDashboardShowsTheActivityNewestFirstAndFiltersIt()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        Directory.CreateDirectory(ServerActivity.Folder(server));
        File.WriteAllLines(ServerActivity.LogFile(server),
        [
            "2026.10.27-18.10.00\tstart\t\t0\t0\t0\tStudioActivity on, 33 hooks",
            "2026.10.27-18.20.01\tpickup\tDima\t566659\t-224706\t392\tBP_Item_MRE_C:MRE_Stew_C_2147 | BPC_Rager_C:Trunk_C_17",
            "2026.10.27-18.21.30\tmove\tAnna\t10\t20\t30\tBP_Item_Nails_C:Nails_1 | BP_Chest_C:Chest_4",
        ]);

        using var page = new DashboardPageViewModel(ctx.Services, live: false, runningProbe: () => true);

        Assert.Equal(["move", "pickup", "start"], page.Activity.Select(a => a.Action));
        page.ActivityFilter = "rager dima";
        var only = Assert.Single(page.Activity);
        Assert.Equal("Dima", only.Player);
        Assert.Equal("566659 -224706", only.Place);
    }
}
