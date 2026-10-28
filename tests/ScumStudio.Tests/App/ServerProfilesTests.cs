using Avalonia.Headless.XUnit;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Settings;

namespace ScumStudio.Tests.App;

/// <summary>
/// Several servers (owner, 2026-10-27): a user may have more than one server, local or on a network share. The list lives in the
/// settings; <c>ServerPaksFolder</c> stays the folder of the active one, so every page follows a switch.
/// </summary>
public sealed class ServerProfilesTests
{
    private const string A = @"D:\Servers\Alpha\SCUM Server\SCUM\Content\Paks";
    private const string B = @"E:\Beta\SCUM Server\SCUM\Content\Paks";
    private const string Share = @"\\vps\scum\SCUM Server\SCUM\Content\Paks";

    [Fact]
    public void AFolderSetBeforeTheListExistedBecomesTheFirstServer()
    {
        var old = new AppSettings { ServerPaksFolder = A };

        var now = ServerProfiles.Normalize(old);

        var only = Assert.Single(now.Servers);
        Assert.Equal((A, "SCUM Server"), (only.Folder, only.Name));
        Assert.Equal(only.Id, now.ActiveServerId);
        Assert.Same(now, ServerProfiles.Normalize(now));   // nothing more to do the second time
    }

    [Fact]
    public void NoServerAtAllStaysEmpty()
    {
        var s = new AppSettings();

        Assert.Same(s, ServerProfiles.Normalize(s));
        Assert.Empty(s.Servers);
    }

    [Fact]
    public void ServersAreAddedSwitchedRenamedAndRemoved()
    {
        var s = ServerProfiles.Add(new AppSettings(), A, "Home");
        s = ServerProfiles.Add(s, Share);
        s = ServerProfiles.Add(s, B, "Test", activate: false);

        Assert.Equal(3, s.Servers.Count);
        Assert.Equal(Share, s.ServerPaksFolder);                           // the last one activated
        Assert.True(ServerProfiles.Active(s)!.IsNetwork);
        Assert.StartsWith("vps:", ServerProfiles.Active(s)!.Name, StringComparison.Ordinal);
        Assert.Same(s, ServerProfiles.Normalize(s));

        var home = s.Servers.Single(x => x.Name == "Home");
        s = ServerProfiles.Activate(s, home.Id);
        Assert.Equal((A, home.Id), (s.ServerPaksFolder, s.ActiveServerId));

        s = ServerProfiles.Rename(s, home.Id, "  Main  ");
        Assert.Equal("Main", s.Servers.Single(x => x.Id == home.Id).Name);
        Assert.Same(s, ServerProfiles.Rename(s, home.Id, "   "));         // an empty name changes nothing

        s = ServerProfiles.Remove(s, home.Id);                             // the active one goes: the next takes its place
        Assert.Equal(2, s.Servers.Count);
        Assert.Equal(s.Servers[0].Folder, s.ServerPaksFolder);

        s = ServerProfiles.Remove(s, s.Servers[0].Id);
        s = ServerProfiles.Remove(s, s.Servers[0].Id);
        Assert.Empty(s.Servers);
        Assert.Null(s.ServerPaksFolder);
        Assert.Null(s.ActiveServerId);
    }

    [Fact]
    public void AddingAKnownFolderDoesNotDuplicateIt()
    {
        var s = ServerProfiles.Add(new AppSettings(), A);

        var again = ServerProfiles.Add(s, A.ToLowerInvariant() + @"\", "Other name");

        Assert.Single(again.Servers);
    }

    [Fact]
    public void TypingAFolderOverTheActiveServerMovesThatServerAndTypingAListedOneSwitches()
    {
        var s = ServerProfiles.Add(ServerProfiles.Add(new AppSettings(), A), B, activate: false);
        var alpha = s.Servers[0];

        var moved = ServerProfiles.Normalize(s with { ServerPaksFolder = @"D:\Elsewhere\SCUM\Content\Paks" });
        Assert.Equal(2, moved.Servers.Count);
        Assert.Equal(@"D:\Elsewhere\SCUM\Content\Paks", moved.Servers.Single(x => x.Id == alpha.Id).Folder);

        var switched = ServerProfiles.Normalize(s with { ServerPaksFolder = B });
        Assert.Equal(switched.Servers.Single(x => x.Folder == B).Id, switched.ActiveServerId);
        Assert.Equal(A, switched.Servers.Single(x => x.Id == alpha.Id).Folder);   // the first one kept its folder
    }

    [Fact]
    public void TheListKeepsAtMostTwentyServers()
    {
        var s = new AppSettings();
        for (var i = 0; i < ServerProfiles.MaxServers + 5; i++)
        {
            s = ServerProfiles.Add(s, $@"D:\S{i}\SCUM\Content\Paks");
        }

        Assert.Equal(ServerProfiles.MaxServers, s.Servers.Count);
        Assert.Equal($@"D:\S{ServerProfiles.MaxServers + 4}\SCUM\Content\Paks", s.ServerPaksFolder);
    }

    [Fact]
    public void ThePathKindsAreTold()
    {
        Assert.True(ServerProfiles.IsNetworkPath(@"\\host\share\x"));
        Assert.True(ServerProfiles.IsNetworkPath("//host/share/x"));
        Assert.False(ServerProfiles.IsNetworkPath(@"D:\x"));
        Assert.False(ServerProfiles.IsNetworkPath(null));
        Assert.Throws<ArgumentException>(() => ServerProfiles.Add(new AppSettings(), "  "));
    }

    [Fact]
    public void TheSettingsFileKeepsTheServers()
    {
        var s = ServerProfiles.Add(ServerProfiles.Add(new AppSettings(), A, "Home"), Share);
        var json = System.Text.Json.JsonSerializer.Serialize(s);

        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(s.Servers.Select(x => (x.Name, x.Folder)), back.Servers.Select(x => (x.Name, x.Folder)));
        Assert.Equal(s.ActiveServerId, back.ActiveServerId);
    }

    private static string MakeServer(AppTestContext ctx, string name)
    {
        var win64 = ctx.Combine(name, "SCUM", "Binaries", "Win64");
        Directory.CreateDirectory(win64);
        var paks = ctx.Combine(name, "SCUM", "Content", "Paks");
        Directory.CreateDirectory(paks);
        File.WriteAllText(Path.Combine(win64, "SCUMServer.exe"), "exe");
        return paks;
    }

    [AvaloniaFact]
    public void TheAppSwitchesBetweenServersAndEveryPageFollows()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var alpha = MakeServer(ctx, "Alpha");
        var beta = MakeServer(ctx, "Beta");
        // Alpha has a log; Beta has another.
        Directory.CreateDirectory(Path.Combine(ctx.Combine("Alpha"), "SCUM", "Saved", "Logs"));
        Directory.CreateDirectory(Path.Combine(ctx.Combine("Beta"), "SCUM", "Saved", "Logs"));
        File.WriteAllText(Path.Combine(ctx.Combine("Alpha"), "SCUM", "Saved", "Logs", "SCUM.log"), "from alpha\r\n");
        File.WriteAllText(Path.Combine(ctx.Combine("Beta"), "SCUM", "Saved", "Logs", "SCUM.log"), "from beta\r\n");

        ctx.Services.AddServer(alpha, "Alpha");
        using var console = new ConsolePageViewModel(ctx.Services, live: false);
        using var window = new MainWindowViewModel(ctx.Services);
        Assert.Equal(["from alpha"], console.Lines.Select(l => l.Text));
        Assert.False(window.HasManyServers);                   // one server: no switcher

        ctx.Services.AddServer(beta, "Beta");                  // adding makes it the one in use
        Assert.True(window.HasManyServers);
        Assert.Equal("Beta", window.ActiveServer?.Name);
        Assert.Equal(["from beta"], console.Lines.Select(l => l.Text));   // the Console followed

        // The switcher in the status bar picks Alpha again.
        window.ActiveServer = window.ServerChoices.Single(p => p.Name == "Alpha");
        Assert.Equal(alpha, ctx.Services.Settings.Load().ServerPaksFolder);
        Assert.Equal(["from alpha"], console.Lines.Select(l => l.Text));

        // The Settings page lists both, marks the one in use, and removing one leaves the other.
        var settings = new SettingsPageViewModel(ctx.Services, () => { });
        Assert.Equal(2, settings.Servers.Count);
        Assert.True(settings.Servers.Single(r => r.Name == "Alpha").Active);
        settings.PickedServer = settings.Servers.Single(r => r.Name == "Beta");
        settings.RemoveServerCommand.Execute(null);
        Assert.Equal(["Alpha"], settings.Servers.Select(r => r.Name));
        Assert.False(window.HasManyServers);
    }

    [AvaloniaFact]
    public void AnOlderSettingsFileWithOnlyAFolderGetsItsFirstServerAtStart()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var only = MakeServer(ctx, "Solo");
        ctx.Services.UpdateSettings(s => s with { ServerPaksFolder = only, Servers = [], ActiveServerId = null });

        using var window = new MainWindowViewModel(ctx.Services);

        var settings = ctx.Services.Settings.Load();
        Assert.Single(settings.Servers);
        Assert.Equal(only, settings.Servers[0].Folder);
        Assert.Equal(settings.Servers[0].Id, settings.ActiveServerId);
    }
}

/// <summary>A remote (SFTP) server: its files are mirrored on this PC in the background and every page reads the mirror.</summary>
public sealed class MirrorServiceTests
{
    [Fact]
    public void TheActiveSftpServerIsMirroredAndTheConsoleReadsIt()
    {
        using var ctx = AppTestContext.Create();
        var host = ctx.Combine("host");
        Directory.CreateDirectory(Path.Combine(host, "srv", "SCUM", "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(host, "srv", "SCUM", "Saved", "Logs"));
        File.WriteAllText(Path.Combine(host, "srv", "SCUM", "Saved", "Logs", "SCUM.log"), "from the host\r\n");

        using var service = new ScumStudio.App.Services.MirrorService(ctx.Services, _ => new ScumStudio.Modding.Remote.FolderRemote(host), TimeSpan.FromMilliseconds(50));
        Assert.Equal(ScumStudio.App.Services.MirrorState.None, service.State);
        Assert.Null(service.RunningProbeForActive());

        ctx.Services.AddSftpServer("Remote", new SftpAccess("h", 22, "u", "/srv", null, null, "SHA256:x", false));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (service.State != ScumStudio.App.Services.MirrorState.Online && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        Assert.Equal(ScumStudio.App.Services.MirrorState.Online, service.State);
        Assert.NotNull(service.RunningProbeForActive());
        var active = ServerProfiles.Active(ctx.Services.Settings.Load())!;
        Assert.Equal("from the host\r\n", File.ReadAllText(Path.Combine(active.Folder, "..", "..", "Saved", "Logs", "SCUM.log")));
        Assert.StartsWith(ctx.Services.DataDirectory, active.Folder);

        // Removing the server stops the mirror and deletes the copy.
        ctx.Services.RemoveServer(active.Id);
        Assert.False(Directory.Exists(ServerProfiles.MirrorRoot(active.Id, ctx.Services.DataDirectory)));
    }

    [Fact]
    public void ARemoteServerWithoutAConfirmedHostKeyDoesNotConnect()
    {
        using var ctx = AppTestContext.Create();
        using var service = new ScumStudio.App.Services.MirrorService(ctx.Services, interval: TimeSpan.FromMilliseconds(50));
        ctx.Services.AddSftpServer("Remote", new SftpAccess("127.0.0.1", 1, "u", "/srv", null, null, null, false));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (service.State == ScumStudio.App.Services.MirrorState.Syncing && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        Assert.Equal(ScumStudio.App.Services.MirrorState.Offline, service.State);
        Assert.Contains("host key", service.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlainFtpNeedsNoHostKeyAndFtpsKeepsItsProtocol()
    {
        using var ctx = AppTestContext.Create();
        using var service = new ScumStudio.App.Services.MirrorService(ctx.Services, interval: TimeSpan.FromMilliseconds(50));
        ctx.Services.AddSftpServer("Host", new SftpAccess("127.0.0.1", 1, "u", "/srv", null, null, null, false) { Protocol = RemoteProtocol.Ftp });
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (service.State == ScumStudio.App.Services.MirrorState.Syncing && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        // It tried to connect (port 1 refuses): not stopped for a missing key as an SFTP server would be.
        Assert.Equal(ScumStudio.App.Services.MirrorState.Offline, service.State);
        Assert.DoesNotContain("host key", service.StatusText, StringComparison.OrdinalIgnoreCase);

        var added = ServerProfiles.AddSftp(new AppSettings(), "x", new SftpAccess("h", 21, "u", "/", null, null, "SHA256:a", false) { Protocol = RemoteProtocol.Ftps });
        Assert.Equal(RemoteProtocol.Ftps, added.Servers[0].Sftp!.Protocol);
    }
}
