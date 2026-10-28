using ScumStudio.Level.Saves;
using ScumStudio.Modding.ServerMods;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Modding;

/// <summary>The dashboard on the logs of a real dedicated server (<c>SCUM_SERVER</c>: the folder of the server; skipped without it).</summary>
public sealed class ServerDashboardRealTests(ITestOutputHelper output)
{
    [Fact]
    public void TheLogsOfARealServerAreReadWithoutLosingTheirLines()
    {
        if (ServerInstall.Find(Environment.GetEnvironmentVariable("SCUM_SERVER")) is not { } server)
        {
            return;
        }

        var all = new ServerDashboard(server, () => false).Read(DashboardScope.AllLogs);
        var now = new ServerDashboard(server, () => true).Read(DashboardScope.CurrentStart);
        output.WriteLine($"all: trades {all.Trades.Count}, kills {all.Kills.Count}, chat {all.Chat.Count}, admin {all.Admin.Count}, events {all.Events.Count}, bunkers {all.Bunkers.Count}, logins {all.Logins}");
        output.WriteLine($"now: players {now.Players.Count}, health {now.Health}, max {now.MaxPlayers}");
        foreach (var b in all.Bunkers.Take(4))
        {
            output.WriteLine($"{b.Name} active={b.Active} since={b.ChangedAt} next={b.NextActivation}");
        }

        foreach (var t in all.Trades.Take(3))
        {
            output.WriteLine($"{t.Time} {t.Player} sold={t.Sold} {t.Item} x{t.Quantity} {t.Price} {t.Trader}");
        }

        Assert.True(all.Trades.Count > 0 || all.Chat.Count > 0 || all.Events.Count > 0);
        Assert.All(all.Trades, t => Assert.True(t.Player.Length > 0 && t.Item.Length > 0));
        Assert.True(all.Bunkers.Count > 0);
        Assert.NotNull(now.Health);
    }

    [Fact]
    public void TheVehiclesOfARealSaveAreRead()
    {
        if (ServerInstall.Find(Environment.GetEnvironmentVariable("SCUM_SERVER")) is not { } server)
        {
            return;
        }

        var (vehicles, players) = ServerVehicles.Read(server);
        output.WriteLine($"vehicles {vehicles.Count}, locked {vehicles.Count(v => v.LockOwner.Length > 0)}, players {players.Count}");
        foreach (var v in vehicles.OrderByDescending(v => v.LastUsed).Take(3))
        {
            output.WriteLine($"{v.Id} {v.Type} @{v.X:0} {v.Y:0} used {v.LastUsed} works={v.Works} owner='{v.LockOwner}' lock='{v.Lock}'");
        }

        Assert.True(vehicles.Count > 0);
        Assert.All(vehicles, v => Assert.True(v.Type.Length > 0 && !v.Type.Contains(':', StringComparison.Ordinal)));
        Assert.Contains(players, p => p.SteamId.Length >= 15);
    }
}
