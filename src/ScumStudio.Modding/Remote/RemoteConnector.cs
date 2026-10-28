using ScumStudio.Core.Settings;

namespace ScumStudio.Modding.Remote;

/// <summary>Opens the connection a profile asks for: SFTP, or FTP/FTPS for hosts without SSH.</summary>
public static class RemoteConnector
{
    /// <summary>Connects and returns the files of the host; <paramref name="hostKey"/> is the key or certificate fingerprint it showed (null for plain FTP).</summary>
    /// <exception cref="HostKeyMismatchException">The key differs from the remembered one.</exception>
    /// <exception cref="IOException">The host cannot be reached or the login is refused.</exception>
    public static IRemoteFiles Open(SftpConnection connection, out string? hostKey)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Protocol == RemoteProtocol.Sftp)
        {
            var sftp = new SftpRemote(connection);
            hostKey = sftp.HostKey;
            return sftp;
        }

        var ftp = new FtpRemote(connection);
        hostKey = ftp.HostKey;
        return ftp;
    }
}
