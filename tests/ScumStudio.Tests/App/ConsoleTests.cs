using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.App.Views.Pages;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (2026-10-27): "a place for the server logs and everything that happens in the game in real time, and to send admin
/// commands and a message to one player". The logs are followed as the server writes them; sending goes through the studio
/// bridge (a Lua mod in the server) by files.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class ConsoleTests
{
    private static string Saved(ServerInstall server) => ServerLogs.SavedFolder(server);

    private static void AppendUtf16(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = new UnicodeEncoding(false, false).GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    [Fact]
    public void ATailReturnsOnlyWhatWasAddedAndWaitsForTheEndOfALine()
    {
        var path = Path.Combine(Path.GetTempPath(), "scumstudio-tail-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllText(path, "one\r\ntwo\r\n", new UTF8Encoding(true));
            var tail = new LogTail(path);
            Assert.Equal(["one", "two"], tail.ReadNew());
            Assert.Empty(tail.ReadNew());

            File.AppendAllText(path, "three\r\nfou");
            Assert.Equal(["three"], tail.ReadNew()); // "fou" is not a whole line yet
            File.AppendAllText(path, "r\r\n");
            Assert.Equal(["four"], tail.ReadNew());

            // A new server start writes the file again from the top.
            File.WriteAllText(path, "new\r\n");
            Assert.Equal(["new"], tail.ReadNew());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ATailReadsTheGamesUtf16LogsAndStartsNearTheEndOfABigOne()
    {
        var path = Path.Combine(Path.GetTempPath(), "scumstudio-tail16-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            AppendUtf16(path, "\n2026.10.27-10.23.53: Game version: 1.3.4\n2026.10.27-10.24.01: Привет\n");
            Assert.Equal(["2026.10.27-10.23.53: Game version: 1.3.4", "2026.10.27-10.24.01: Привет"], new LogTail(path).ReadNew());

            File.WriteAllText(path, string.Join("\r\n", Enumerable.Range(1, 5000).Select(i => "line " + i)) + "\r\n");
            var lines = new LogTail(path, initialBytes: 2000).ReadNew();
            Assert.Equal("line 5000", lines[^1]);
            Assert.True(lines.Count is > 100 and < 400);
            Assert.StartsWith("line ", lines[0]); // the cut line at the start is dropped
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LevelsAndFiltersWork()
    {
        Assert.Equal("error", LogTail.LevelOf("LogNet: Error: lost connection"));
        Assert.Equal("warning", LogTail.LevelOf("LogX: Warning: slow"));
        Assert.Equal(string.Empty, LogTail.LevelOf("LogX: fine"));
        Assert.True(LogTail.Matches("Player Dima KILLED a zombie", "dima killed"));
        Assert.False(LogTail.Matches("Player Dima joined", "dima killed"));
        Assert.True(LogTail.Matches("anything", "  "));
    }

    [AvaloniaFact]
    public void TheLogsThatExistAreListedWithTheNewestFileOfEachKind()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        var saved = Saved(server);
        Directory.CreateDirectory(Path.Combine(saved, "Logs"));
        File.WriteAllText(Path.Combine(saved, "Logs", "SCUM.log"), "Log file open\r\n");
        var game = Path.Combine(saved, "SaveFiles", "Logs");
        AppendUtf16(Path.Combine(game, "chat_20261027102353.log"), "\nold\n");
        AppendUtf16(Path.Combine(game, "chat_20261027133000.log"), "\nnew chat\n");
        AppendUtf16(Path.Combine(game, "kill_20261027133000.log"), "\nkill\n");
        AppendUtf16(Path.Combine(game, "zzz_20261027133000.log"), "\nz\n");

        var sources = ServerLogs.Sources(server);

        Assert.Equal(["server", "chat", "kill", "zzz"], sources.Select(s => s.Id));
        Assert.EndsWith("chat_20261027133000.log", sources.Single(s => s.Id == "chat").Path, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ThePageFollowsALogAndFiltersAndPauses()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        Directory.CreateDirectory(Path.Combine(Saved(server), "Logs"));
        var log = Path.Combine(Saved(server), "Logs", "SCUM.log");
        File.WriteAllText(log, "LogA: start\r\nLogB: Error: broke\r\n");

        using var page = new ConsolePageViewModel(ctx.Services, live: false);

        Assert.True(page.HasServer);
        Assert.Equal("server", page.SelectedSource?.Id);
        Assert.Equal(["LogA: start", "LogB: Error: broke"], page.Lines.Select(l => l.Text));
        Assert.True(page.Lines[1].IsError);

        File.AppendAllText(log, "LogC: hello world\r\n");
        page.Tick();
        Assert.Equal(3, page.Lines.Count);

        page.Filter = "hello";
        Assert.Equal(["LogC: hello world"], page.Lines.Select(l => l.Text));
        page.Filter = string.Empty;

        page.IsPaused = true;
        File.AppendAllText(log, "LogD: while paused\r\n");
        page.Tick();
        Assert.Equal(3, page.Lines.Count);
        page.IsPaused = false;
        Assert.Equal(4, page.Lines.Count);
        Assert.Contains("while paused", page.ShownText(), StringComparison.Ordinal);

        page.ClearCommand.Execute(null);
        Assert.Empty(page.Lines);
    }

    [AvaloniaFact]
    public void WithoutAServerThePageOffersNothingToSend()
    {
        using var ctx = AppTestContext.Create(inline: false);

        using var page = new ConsolePageViewModel(ctx.Services, live: false);

        Assert.False(page.HasServer);
        Assert.False(page.CanSend);
        Assert.Empty(page.Sources);
    }

    [AvaloniaFact]
    public void TheBridgeIsInstalledAndAMessageAndACommandLandInItsQueue()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        using var page = new ConsolePageViewModel(ctx.Services, live: false);
        Assert.False(page.BridgeInstalled);
        Assert.False(page.CanSend);

        page.InstallBridgeCommand.Execute(null);
        Assert.True(page.BridgeInstalled);
        Assert.True(page.CanSend);
        Assert.True(File.Exists(Path.Combine(ServerBridge.Folder(server), "Scripts", "main.lua")));

        // The mod writes who is online; the page offers them.
        File.WriteAllText(ServerBridge.PlayersFile(server), "Dima\nAnna\n");
        for (var i = 0; i < 9; i++)
        {
            page.Tick();
        }

        Assert.Equal(["Anna", "Dima"], page.Players);

        page.Mode = 1;
        page.SelectedPlayer = "Dima";
        page.SendText = "Hello, Dima";
        page.SendCommand.Execute(null);
        page.Mode = 0;
        page.SendText = "#Announce Restart in 5 minutes";
        page.SendCommand.Execute(null);
        page.Mode = 2;
        page.SendText = "Everyone: hi";
        page.SendCommand.Execute(null);

        var lines = File.ReadAllLines(ServerBridge.QueueFile(server)).Select(l => l.Split('\t')).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal(["message", "Dima", "Hello, Dima"], lines[0][1..]);
        Assert.Equal(["admin", string.Empty, "Announce Restart in 5 minutes"], lines[1][1..]); // the # is dropped
        Assert.Equal(["say", string.Empty, "Everyone: hi"], lines[2][1..]);
        Assert.Equal(string.Empty, page.SendText);
    }

    [AvaloniaFact]
    public void AnItemIsGivenToAPlayerAtTheirPlaceAndTheyAreToldSo()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        using var page = new ConsolePageViewModel(ctx.Services, live: false);
        page.InstallBridgeCommand.Execute(null);
        // The bridge wrote where Dima stands.
        File.WriteAllText(ServerBridge.PlayersFile(server), "Dima\t360657\t-162369\t9120\n");
        for (var i = 0; i < 9; i++)
        {
            page.Tick();
        }

        page.Mode = 3;
        page.SelectedPlayer = "Dima";
        page.GiveItem = "MRE_Stew";
        page.GiveCount = "2";
        Assert.True(page.IsGive);
        page.SendCommand.Execute(null);

        var lines = File.ReadAllLines(ServerBridge.QueueFile(server)).Select(l => l.Split('\t')).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(["admin", string.Empty, "SpawnItem MRE_Stew 2 Location \"X=360657 Y=-162369 Z=9120\""], lines[0][1..]);
        Assert.Equal(["message", "Dima"], lines[1][1..3]);
        Assert.Contains("MRE_Stew", lines[1][3], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void GivingNeedsAValidItemAndCountAndAKnownPlace()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        using var page = new ConsolePageViewModel(ctx.Services, live: false);
        page.InstallBridgeCommand.Execute(null);
        File.WriteAllText(ServerBridge.PlayersFile(server), "Dima\t1\t2\t3\nGhost\t0\t0\t0\n");
        for (var i = 0; i < 9; i++)
        {
            page.Tick();
        }

        page.Mode = 3;
        page.SelectedPlayer = "Dima";
        page.GiveCount = "1";

        page.GiveItem = "MRE_Stew\" Location \"X=0";            // a command smuggled in the item id
        page.SendCommand.Execute(null);
        Assert.Equal(Loc.T("Console.Give.BadItem"), page.Status);

        page.GiveItem = "MRE_Stew";
        page.GiveCount = "0";
        page.SendCommand.Execute(null);
        Assert.Equal(Loc.T("Console.Give.BadCount"), page.Status);

        page.GiveCount = "1";
        page.SelectedPlayer = "Ghost";                              // a player whose place is not known
        page.SendCommand.Execute(null);
        Assert.Equal(Loc.F("Console.Give.NoPlace", "Ghost"), page.Status);

        Assert.False(File.Exists(ServerBridge.QueueFile(server)));  // nothing was sent
    }

    [AvaloniaFact]
    public void ABridgeCommandNeedsTextAndAMessageNeedsAPlayer()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);

        Assert.Throws<ArgumentException>(() => ServerBridge.Enqueue(server, BridgeKind.Say, null, "   "));
        Assert.Throws<ArgumentException>(() => ServerBridge.Enqueue(server, BridgeKind.Message, "", "hi"));
        Assert.Throws<ArgumentException>(() => ServerBridge.Enqueue(server, BridgeKind.Admin, null, "#"));
        // A tab or a line break in the text must not split the line of the file.
        ServerBridge.Enqueue(server, BridgeKind.Say, null, "a\tb\nc");
        Assert.Equal(["say", string.Empty, "a b c"], File.ReadAllLines(ServerBridge.QueueFile(server)).Single().Split('\t')[1..]);
    }

    [Fact]
    public void TheBridgeModPassesTheLuaCheckAndKeepsToTheTestedFunctions()
    {
        var findings = LuaCheck.Run(ServerBridge.Code);

        Assert.DoesNotContain(findings, f => f.Level == LuaFindingLevel.Error);
        Assert.Contains("SendHUDMessageToClient", ServerBridge.Code, StringComparison.Ordinal);
        Assert.Contains("SendHUDMessageToAll", ServerBridge.Code, StringComparison.Ordinal);
        Assert.Contains("Chat_Server_ProcessAdminCommand", ServerBridge.Code, StringComparison.Ordinal);
        Assert.Contains("ExecuteInGameThread", ServerBridge.Code, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TheViewShowsTheControlsWithTooltips()
    {
        using var ctx = AppTestContext.Create(inline: false);
        ServerPageTests.FakeServer(ctx, withUe4ss: true);
        using var page = new ConsolePageViewModel(ctx.Services, live: false);
        var view = new ConsolePageView { DataContext = page };
        var window = new Window { Content = view };
        window.Show();

        foreach (var name in new[] { "SourcePicker", "FilterBox", "PauseButton", "FollowButton", "CopyButton", "ClearButton", "ModePicker", "SendBox", "SendButton", "LogList", "GiveItemBox", "GiveCountBox" })
        {
            var control = view.FindControl<Control>(name);
            Assert.NotNull(control);
            Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(control) as string), name);
        }
    }
}
