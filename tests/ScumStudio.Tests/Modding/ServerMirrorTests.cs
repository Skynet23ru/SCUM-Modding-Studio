using System.Text;
using ScumStudio.Core.Security;
using ScumStudio.Core.Settings;
using ScumStudio.Modding.Remote;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.Tests.Modding;

public sealed class ServerMirrorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mirror-" + Guid.NewGuid().ToString("N"));
    private readonly string _host;
    private readonly string _mine;

    public ServerMirrorTests()
    {
        _host = Path.Combine(_dir, "host");
        _mine = Path.Combine(_dir, "mine");
        Directory.CreateDirectory(Path.Combine(_host, "srv", "SCUM", "Binaries", "Win64", "ue4ss", "Mods"));
        Directory.CreateDirectory(Path.Combine(_host, "srv", "SCUM", "Saved", "Logs"));
        Directory.CreateDirectory(Path.Combine(_host, "srv", "SCUM", "Saved", "SaveFiles", "Logs"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string H(params string[] parts) => Path.Combine([_host, "srv", .. parts]);

    private string M(params string[] parts) => Path.Combine([_mine, .. parts]);

    private ServerMirror Make(bool write, out FolderRemote remote)
    {
        remote = new FolderRemote(_host);
        return new ServerMirror(remote, "/srv", _mine, write);
    }

    [Fact]
    public void LogsAreAppendedNotFetchedAgain()
    {
        var mirror = Make(false, out _);
        File.WriteAllText(H("SCUM", "Saved", "Logs", "SCUM.log"), "one\r\n");
        mirror.Sync(false);
        Assert.Equal("one\r\n", File.ReadAllText(M("SCUM", "Saved", "Logs", "SCUM.log")));

        File.AppendAllText(H("SCUM", "Saved", "Logs", "SCUM.log"), "two\r\n");
        var report = mirror.Sync(false);
        Assert.Equal("one\r\ntwo\r\n", File.ReadAllText(M("SCUM", "Saved", "Logs", "SCUM.log")));
        Assert.Equal(1, report.Downloaded);
        Assert.Equal(0, make().Downloaded);

        // The host started the file again.
        File.WriteAllText(H("SCUM", "Saved", "Logs", "SCUM.log"), "x\r\n");
        mirror.Sync(false);
        Assert.Equal("x\r\n", File.ReadAllText(M("SCUM", "Saved", "Logs", "SCUM.log")));

        MirrorReport make() => mirror.Sync(false);
    }

    [Fact]
    public void ABigLogOnlyBringsItsLastPart()
    {
        var mirror = Make(false, out _);
        var text = string.Concat(Enumerable.Range(0, 200_000).Select(i => "line " + i + "\n"));
        File.WriteAllText(H("SCUM", "Saved", "Logs", "SCUM.log"), text);
        mirror.Sync(false);
        var local = File.ReadAllText(M("SCUM", "Saved", "Logs", "SCUM.log"));
        Assert.True(local.Length < text.Length / 2);
        Assert.EndsWith("line 199999\n", local);
    }

    [Fact]
    public void AGameLogKeepsItsUtf16TextAfterTheHeadIsSkipped()
    {
        var mirror = Make(false, out _);
        var body = string.Concat(Enumerable.Range(0, 150_000).Select(i => "row " + i + "\r\n"));
        File.WriteAllText(H("SCUM", "Saved", "SaveFiles", "Logs", "chat_20260101000000.log"), body, Encoding.Unicode);
        mirror.Sync(false);
        var bytes = File.ReadAllBytes(M("SCUM", "Saved", "SaveFiles", "Logs", "chat_20260101000000.log"));
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
        Assert.EndsWith("row 149999\r\n", Encoding.Unicode.GetString(bytes));
    }

    [Fact]
    public void AFullPassMakesTheFolderLookLikeAServer()
    {
        File.WriteAllBytes(H("SCUM", "Binaries", "Win64", "ue4ss", "UE4SS.dll"), [1]);
        File.WriteAllBytes(H("SCUM", "Binaries", "Win64", "dwmapi.dll"), [1]);
        File.WriteAllText(H("SCUM", "Saved", "SaveFiles", "SCUM.db"), "db");
        var mirror = Make(false, out _);
        mirror.Sync(true);

        var install = ServerInstall.Find(ServerMirror.PaksOf(_mine));
        Assert.NotNull(install);
        Assert.True(install!.IsUe4ssActive);
        Assert.Equal("db", File.ReadAllText(M("SCUM", "Saved", "SaveFiles", "SCUM.db")));
    }

    [Fact]
    public void ReadOnlyNeverWritesToTheHost()
    {
        var mirror = Make(false, out _);
        mirror.Sync(true);
        Directory.CreateDirectory(M("SCUM", "Binaries", "Win64", "ue4ss", "Mods", "Mine", "Scripts"));
        File.WriteAllText(M("SCUM", "Binaries", "Win64", "ue4ss", "Mods", "Mine", "Scripts", "main.lua"), "print(1)");
        Directory.CreateDirectory(M("SCUM", "Binaries", "Win64", "ue4ss", "Mods", "StudioBridge"));
        File.WriteAllText(M("SCUM", "Binaries", "Win64", "ue4ss", "Mods", "StudioBridge", "commands.txt"), "1\tadmin\t\tx\n");
        mirror.Sync(true);

        Assert.False(Directory.Exists(H("SCUM", "Binaries", "Win64", "ue4ss", "Mods", "Mine")));
        Assert.False(File.Exists(H("SCUM", "Binaries", "Win64", "ue4ss", "Mods", "StudioBridge", "commands.txt")));
    }

    [Fact]
    public void ModsAndTheQueueGoToTheHostWhenWritingIsAllowed()
    {
        var mirror = Make(true, out _);
        mirror.Sync(true);
        var mods = new[] { "SCUM", "Binaries", "Win64", "ue4ss", "Mods" };
        Directory.CreateDirectory(M([.. mods, "Mine", "Scripts"]));
        File.WriteAllText(M([.. mods, "Mine", "Scripts", "main.lua"]), "print(1)");
        Directory.CreateDirectory(M([.. mods, "StudioBridge"]));
        File.WriteAllText(M([.. mods, "StudioBridge", "commands.txt"]), "1\tadmin\t\tfirst\n");
        mirror.Sync(false);

        Assert.Equal("print(1)", File.ReadAllText(H([.. mods, "Mine", "Scripts", "main.lua"])));
        Assert.Equal("1\tadmin\t\tfirst\n", File.ReadAllText(H([.. mods, "StudioBridge", "commands.txt"])));
        Assert.False(File.Exists(M([.. mods, "StudioBridge", "commands.txt"])));   // sent: not sent twice

        // A second command is added to what the host has not taken yet.
        File.WriteAllText(M([.. mods, "StudioBridge", "commands.txt"]), "2\tadmin\t\tsecond\n");
        mirror.Sync(false);
        Assert.Equal("1\tadmin\t\tfirst\n2\tadmin\t\tsecond\n", File.ReadAllText(H([.. mods, "StudioBridge", "commands.txt"])));

        // A mod removed here is removed there.
        Directory.Delete(M([.. mods, "Mine"]), true);
        mirror.Sync(false);
        Assert.False(File.Exists(H([.. mods, "Mine", "Scripts", "main.lua"])));
    }

    [Fact]
    public void WhatWasChangedHereIsNotOverwrittenByTheHost()
    {
        var mods = new[] { "SCUM", "Binaries", "Win64", "ue4ss", "Mods" };
        Directory.CreateDirectory(H([.. mods, "Mod"]));
        File.WriteAllText(H([.. mods, "Mod", "a.txt"]), "host v1");
        var mirror = Make(false, out _);
        mirror.Sync(true);
        Assert.Equal("host v1", File.ReadAllText(M([.. mods, "Mod", "a.txt"])));

        File.WriteAllText(M([.. mods, "Mod", "a.txt"]), "mine");
        File.WriteAllText(H([.. mods, "Mod", "a.txt"]), "host v2 longer");
        mirror.Sync(true);
        Assert.Equal("mine", File.ReadAllText(M([.. mods, "Mod", "a.txt"])));
    }

    [Fact]
    public void AFileTheHostRemovedGoesFromTheMirrorToo()
    {
        var mods = new[] { "SCUM", "Binaries", "Win64", "ue4ss", "Mods" };
        Directory.CreateDirectory(H([.. mods, "Mod"]));
        File.WriteAllText(H([.. mods, "Mod", "a.txt"]), "x");
        var mirror = Make(false, out _);
        mirror.Sync(true);
        File.Delete(H([.. mods, "Mod", "a.txt"]));
        mirror.Sync(true);
        Assert.False(File.Exists(M([.. mods, "Mod", "a.txt"])));
    }

    [Fact]
    public void ALostConnectionIsAnIoErrorAndTheNextPassCarriesOn()
    {
        var mirror = Make(false, out var remote);
        File.WriteAllText(H("SCUM", "Saved", "Logs", "SCUM.log"), "a\n");
        mirror.Sync(false);
        remote.Offline = true;
        Assert.Throws<IOException>(() => mirror.Sync(false));
        remote.Offline = false;
        File.AppendAllText(H("SCUM", "Saved", "Logs", "SCUM.log"), "b\n");
        mirror.Sync(false);
        Assert.Equal("a\nb\n", File.ReadAllText(M("SCUM", "Saved", "Logs", "SCUM.log")));
    }

    [Fact]
    public void AMirrorRemembersWhereItWasAfterARestart()
    {
        File.WriteAllText(H("SCUM", "Saved", "Logs", "SCUM.log"), "a\n");
        Make(false, out _).Sync(false);
        File.AppendAllText(H("SCUM", "Saved", "Logs", "SCUM.log"), "b\n");
        var again = Make(false, out _);
        again.Sync(false);
        Assert.Equal("a\nb\n", File.ReadAllText(M("SCUM", "Saved", "Logs", "SCUM.log")));
    }

    [Fact]
    public void AProbeSaysWhetherTheFolderIsAServer()
    {
        using var remote = new FolderRemote(_host);
        Assert.True(ServerMirror.Probe(remote, "/srv").RootFound);
        Assert.False(ServerMirror.Probe(remote, "/nowhere").RootFound);
        Assert.False(ServerMirror.Probe(remote, "/srv").HasUe4ss);
    }

    [Fact]
    public void ASftpServerIsAddedWithItsMirrorFolderAndProtectedPassword()
    {
        var access = new SftpAccess(" host.example ", 22, " root ", @"\srv\scum", SecretText.Protect("pw"), null, "SHA256:abc", false);
        var settings = ServerProfiles.AddSftp(new AppSettings(), "", access);
        var profile = Assert.Single(settings.Servers);
        Assert.True(profile.IsSftp);
        Assert.Equal("host.example", profile.Name);
        Assert.Equal("/srv/scum", profile.Sftp!.RemoteRoot);
        Assert.Equal(profile.Folder, settings.ServerPaksFolder);
        Assert.EndsWith(Path.Combine("SCUM", "Content", "Paks"), profile.Folder);
        if (SecretText.IsSupported)
        {
            Assert.NotEqual("pw", profile.Sftp.Password);
            Assert.Equal("pw", SecretText.Reveal(profile.Sftp.Password));
        }

        Assert.Throws<ArgumentException>(() => ServerProfiles.AddSftp(new AppSettings(), "x", access with { Host = " " }));
    }
}
