using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Core.Security;
using ScumStudio.Core.Settings;
using ScumStudio.Modding.Remote;

namespace ScumStudio.App.ViewModels;

public sealed partial class SettingsPageViewModel
{
    private string? _sftpHostKey;
    private string? _sftpCheckedFor;

    /// <summary>Name of the remote server to add (empty: the host).</summary>
    [ObservableProperty]
    private string _sftpName = string.Empty;

    /// <summary>Host name or address.</summary>
    [ObservableProperty]
    private string _sftpHost = string.Empty;

    /// <summary>How to connect: 0 SFTP, 1 FTPS (FTP over TLS), 2 plain FTP.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SftpIsPlainFtp))]
    private int _sftpProtocolIndex;

    /// <summary>Plain FTP is picked: nothing is encrypted, so a warning is shown.</summary>
    public bool SftpIsPlainFtp => SftpProtocolIndex == 2;

    partial void OnSftpProtocolIndexChanged(int value)
    {
        // The usual port follows the kind, unless the user typed another one.
        if (SftpPort is "22" or "21")
        {
            SftpPort = value == 0 ? "22" : "21";
        }
    }

    private RemoteProtocol Protocol => SftpProtocolIndex switch { 1 => RemoteProtocol.Ftps, 2 => RemoteProtocol.Ftp, _ => RemoteProtocol.Sftp };

    /// <summary>The kinds of connection offered.</summary>
    public string[] SftpProtocols { get; } = ["SFTP (SSH)", "FTPS (FTP + TLS)", "FTP"];

    /// <summary>SSH port.</summary>
    [ObservableProperty]
    private string _sftpPort = SftpAccess.DefaultPort.ToString(CultureInfo.InvariantCulture);

    /// <summary>Login.</summary>
    [ObservableProperty]
    private string _sftpUser = string.Empty;

    /// <summary>Password (or the passphrase of the key file); stored protected for this Windows user.</summary>
    [ObservableProperty]
    private string _sftpPassword = string.Empty;

    /// <summary>Private key file, when the host uses a key instead of a password.</summary>
    [ObservableProperty]
    private string _sftpKeyFile = string.Empty;

    /// <summary>The server's install folder on the host (the one that holds <c>SCUM</c>).</summary>
    [ObservableProperty]
    private string _sftpRoot = string.Empty;

    /// <summary>Mods, commands and settings may be written to the host.</summary>
    [ObservableProperty]
    private bool _sftpAllowWrite;

    /// <summary>What the last check found.</summary>
    [ObservableProperty]
    private string _sftpStatus = string.Empty;

    /// <summary>A check or an add is running.</summary>
    [ObservableProperty]
    private bool _sftpBusy;

    /// <summary>Status of the mirror of the server in use.</summary>
    public string MirrorStatus => _services.Mirror.StatusText;

    private string CheckKey() => string.Join('|', Protocol, SftpHost.Trim(), SftpPort.Trim(), SftpUser.Trim(), SftpRoot.Trim());

    private SftpConnection? Connection(string? trusted)
    {
        if (SftpHost.Trim().Length == 0 || SftpUser.Trim().Length == 0 || SftpRoot.Trim().Length == 0 ||
            !int.TryParse(SftpPort.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            SftpStatus = Loc.T("Settings.Sftp.Incomplete");
            return null;
        }

        return new SftpConnection(SftpHost.Trim(), port, SftpUser.Trim(), SftpPassword.Length == 0 ? null : SftpPassword,
            SftpKeyFile.Trim().Length == 0 || Protocol != RemoteProtocol.Sftp ? null : SftpKeyFile.Trim().Trim('"'), trusted) { Protocol = Protocol };
    }

    /// <summary>Connects once: shows the host's key and whether the folder is a SCUM server; remembers the key for the add.</summary>
    [RelayCommand]
    private async Task CheckSftpAsync() => await CheckAsync();

    private async Task<bool> CheckAsync()
    {
        if (Connection(null) is not { } connection)
        {
            return false;
        }

        SftpBusy = true;
        SftpStatus = Loc.T("Settings.Sftp.Checking");
        try
        {
            var root = SftpRoot.Trim().Replace('\\', '/');
            var (key, probe) = await Task.Run(() => MirrorService.Check(connection, root));
            _sftpHostKey = key;
            _sftpCheckedFor = CheckKey();
            var shown = key ?? Loc.T("Settings.Sftp.NoKey");
            SftpStatus = !probe.RootFound
                ? Loc.F("Settings.Sftp.NoServer", shown)
                : Loc.F("Settings.Sftp.Found", shown, YesNo(probe.HasUe4ss), YesNo(probe.HasDatabase));
            return probe.RootFound;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SftpStatus = Loc.F("Settings.Sftp.Failed", ex.Message);
            return false;
        }
        finally
        {
            SftpBusy = false;
        }
    }

    private static string YesNo(bool value) => Loc.T(value ? "Settings.Sftp.Yes" : "Settings.Sftp.No");

    /// <summary>Checks the connection if needed, then adds the remote server and uses it.</summary>
    [RelayCommand]
    private async Task AddSftpServerAsync()
    {
        if (_sftpCheckedFor != CheckKey())
        {
            if (!await CheckAsync())
            {
                return;
            }
        }

        if (!SecretText.IsSupported && SftpPassword.Length > 0)
        {
            _services.Notifications.Warning(Loc.T("Settings.Sftp.NoProtect"), Loc.T("Settings.Sftp.NoProtectDetail"));
            return;
        }

        var access = new SftpAccess(SftpHost, int.Parse(SftpPort.Trim(), CultureInfo.InvariantCulture), SftpUser, SftpRoot,
            SecretText.Protect(SftpPassword), SftpKeyFile.Trim().Length == 0 || Protocol != RemoteProtocol.Sftp ? null : SftpKeyFile.Trim().Trim('"'), _sftpHostKey, SftpAllowWrite) { Protocol = Protocol };
        _services.AddSftpServer(SftpName, access);
        _services.Notifications.Success(Loc.T("Settings.Sftp.Added"), SftpHost.Trim());
        SftpPassword = string.Empty;
        SftpStatus = string.Empty;
        _sftpHostKey = null;
    }
}
