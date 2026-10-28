using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Core.Security;
using ScumStudio.Core.Settings;
using ScumStudio.Modding.Remote;

namespace ScumStudio.App.Services;

/// <summary>Where the mirror of the active remote server stands.</summary>
public enum MirrorState
{
    /// <summary>The server in use is not a remote one.</summary>
    None,

    /// <summary>Connecting or copying.</summary>
    Syncing,

    /// <summary>In step with the host.</summary>
    Online,

    /// <summary>The host cannot be reached; tries again.</summary>
    Offline,

    /// <summary>The host refused or its key changed; the user has to act.</summary>
    Failed,
}

/// <summary>
/// Keeps the local mirror of the active SFTP server in step with the host, in the background: new log lines every few seconds,
/// the save, mods and settings every minute, and what was changed here sent on (when the server allows writing).
/// </summary>
public sealed class MirrorService : IDisposable
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);

    private readonly AppServices _services;
    private readonly Func<ServerProfile, IRemoteFiles> _connect;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _fullEvery;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private string? _runningFor;
    private DateTime? _serverLogUtc;

    /// <summary>Starts watching the settings; <paramref name="connect"/> and the intervals are replaced in tests.</summary>
    public MirrorService(AppServices services, Func<ServerProfile, IRemoteFiles>? connect = null, TimeSpan? interval = null, TimeSpan? fullEvery = null)
    {
        _services = services;
        _connect = connect ?? Connect;
        _interval = interval ?? TimeSpan.FromSeconds(3);
        _fullEvery = fullEvery ?? TimeSpan.FromSeconds(60);
        _services.SettingsChanged += OnSettingsChanged;
        Restart();
    }

    /// <summary>Raised (on a background thread) when <see cref="State"/> or <see cref="StatusText"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Where the mirror stands.</summary>
    public MirrorState State { get; private set; }

    /// <summary>What to tell the user about it (empty when there is nothing remote).</summary>
    public string StatusText { get; private set; } = string.Empty;

    /// <summary>When the last pass finished (local time), or null.</summary>
    public DateTime? LastSync { get; private set; }

    /// <summary>True when the host wrote its log a moment ago, so the server is running.</summary>
    public bool ServerLooksRunning => _serverLogUtc is { } t && DateTime.UtcNow - t < Fresh;

    /// <summary>The "is the server running" answer for the active server when it is a remote one; null otherwise.</summary>
    public Func<bool>? RunningProbeForActive() =>
        ServerProfiles.Active(_services.Settings.Load()) is { IsSftp: true } ? () => ServerLooksRunning : null;

    /// <summary>Tries the connection once and reports the host's key and what the folder holds. Does not touch the settings.</summary>
    /// <param name="connection">How to connect; a null <c>TrustedHostKey</c> accepts the key the host shows.</param>
    /// <param name="remoteRoot">The server's install folder on the host.</param>
    public static (string? HostKey, MirrorProbe Probe) Check(SftpConnection connection, string remoteRoot)
    {
        using var remote = RemoteConnector.Open(connection, out var hostKey);
        return (hostKey, ServerMirror.Probe(remote, remoteRoot));
    }

    private static IRemoteFiles Connect(ServerProfile profile)
    {
        var a = profile.Sftp!;
        if (a.Protocol != RemoteProtocol.Ftp && string.IsNullOrEmpty(a.HostKey))
        {
            throw new IOException("The host key is not confirmed yet: check the connection in Settings first.");
        }

        return RemoteConnector.Open(new SftpConnection(a.Host, a.Port, a.User, SecretText.Reveal(a.Password), a.KeyFile, a.HostKey) { Protocol = a.Protocol }, out _);
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Restart();

    private void Restart()
    {
        var profile = ServerProfiles.Active(_services.Settings.Load());
        var key = profile is { IsSftp: true } ? profile.Id + "|" + profile.Sftp : null;
        lock (_gate)
        {
            if (key == _runningFor)
            {
                return;
            }

            _cts?.Cancel();
            _cts = null;
            _runningFor = key;
            _serverLogUtc = null;
            if (key is null)
            {
                Set(MirrorState.None, string.Empty);
                return;
            }

            var cts = _cts = new CancellationTokenSource();
            Set(MirrorState.Syncing, string.Empty);
            _ = Task.Run(() => Loop(profile!, cts.Token));
        }
    }

    private void Set(MirrorState state, string text)
    {
        State = state;
        StatusText = text;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task Loop(ServerProfile profile, CancellationToken token)
    {
        var access = profile.Sftp!;
        var root = ServerProfiles.MirrorRoot(profile.Id, _services.DataDirectory);
        var retry = TimeSpan.FromSeconds(5);
        var lastFull = DateTime.MinValue;
        IRemoteFiles? remote = null;
        ServerMirror? mirror = null;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (remote is null)
                {
                    remote = _connect(profile);
                    mirror = new ServerMirror(remote, access.RemoteRoot, root, access.AllowWrite);
                    lastFull = DateTime.MinValue;
                }

                var full = DateTime.UtcNow - lastFull >= _fullEvery;
                var report = mirror!.Sync(full);
                if (full)
                {
                    lastFull = DateTime.UtcNow;
                }

                _serverLogUtc = report.ServerLogUtc;
                LastSync = DateTime.Now;
                retry = TimeSpan.FromSeconds(5);
                Report(token, MirrorState.Online, Loc.F("Mirror.Online", access.Host));
                await Task.Delay(_interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HostKeyMismatchException ex)
            {
                _services.Logger.LogWarning("SFTP host key changed for {Host}", access.Host);
                Report(token, MirrorState.Failed, Loc.F("Mirror.KeyChanged", access.Host, ex.Seen));
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _services.Logger.LogInformation("SFTP {Host}: {Message}", access.Host, ex.Message);
                remote?.Dispose();
                remote = null;
                Report(token, MirrorState.Offline, Loc.F("Mirror.Offline", access.Host, ex.Message));
                try
                {
                    await Task.Delay(retry, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                retry = TimeSpan.FromSeconds(Math.Min(60, retry.TotalSeconds * 2));
            }
        }

        remote?.Dispose();
    }

    private void Report(CancellationToken token, MirrorState state, string text)
    {
        if (!token.IsCancellationRequested)
        {
            Set(state, text);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _services.SettingsChanged -= OnSettingsChanged;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }
}
