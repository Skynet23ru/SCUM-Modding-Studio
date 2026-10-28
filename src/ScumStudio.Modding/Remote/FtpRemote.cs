using System.Security.Cryptography;
using FluentFTP;
using FluentFTP.Exceptions;
using ScumStudio.Core.Settings;

namespace ScumStudio.Modding.Remote;

/// <summary>Files of a host over FTP or FTPS (FluentFTP), for game hosting panels that give no SSH.</summary>
public sealed class FtpRemote : IRemoteFiles
{
    private readonly FtpClient _client;
    private string? _seenKey;
    private string? _mismatch;

    /// <summary>Connects. FTPS accepts the certificate at a first connection (no remembered key) and reports it in <see cref="HostKey"/>; plain FTP has no key.</summary>
    /// <exception cref="HostKeyMismatchException">The certificate differs from the remembered one.</exception>
    /// <exception cref="IOException">The host cannot be reached or the login is refused.</exception>
    public FtpRemote(SftpConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _client = new FtpClient(connection.Host, connection.User, connection.Password ?? string.Empty, connection.Port);
        _client.Config.ConnectTimeout = 15000;
        _client.Config.ReadTimeout = 60000;
        _client.Config.DataConnectionConnectTimeout = 15000;
        _client.Config.DataConnectionReadTimeout = 60000;
        if (connection.Protocol == RemoteProtocol.Ftps)
        {
            _client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            _client.Config.DataConnectionEncryption = true;
            _client.ValidateCertificate += (_, e) =>
            {
                _seenKey = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.Certificate.GetRawCertData())).TrimEnd('=');
                var trusted = connection.TrustedHostKey;
                e.Accept = string.IsNullOrEmpty(trusted) || string.Equals(trusted, _seenKey, StringComparison.Ordinal);
                if (!e.Accept)
                {
                    _mismatch = trusted;
                }
            };
        }
        else
        {
            _client.Config.EncryptionMode = FtpEncryptionMode.None;
        }

        try
        {
            _client.Connect();
        }
        catch (Exception ex) when (ex is FtpException or System.Net.Sockets.SocketException or TimeoutException or IOException or System.Security.Authentication.AuthenticationException)
        {
            _client.Dispose();
            if (_mismatch is not null)
            {
                throw new HostKeyMismatchException(_mismatch, _seenKey ?? "?");
            }

            throw new IOException(ex.Message, ex);
        }
    }

    /// <summary>The fingerprint of the host's certificate (FTPS) seen at the connection; null for plain FTP.</summary>
    public string? HostKey => _seenKey;

    private static T Wrap<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is FtpException or TimeoutException or System.Net.Sockets.SocketException)
        {
            throw new IOException(ex.Message, ex);
        }
    }

    private static RemoteEntry Entry(FtpListItem f) =>
        new(f.Name, f.FullName, f.Type == FtpObjectType.Directory, f.Type == FtpObjectType.Directory ? 0 : Math.Max(0, f.Size),
            f.Modified == default ? DateTime.UtcNow : f.Modified.ToUniversalTime());

    /// <inheritdoc />
    public RemoteEntry? Stat(string path) => Wrap(() =>
    {
        if (_client.FileExists(path))
        {
            var size = _client.GetFileSize(path);
            var time = _client.GetModifiedTime(path);
            return new RemoteEntry(path[(path.LastIndexOf('/') + 1)..], path, false, Math.Max(0, size), time.ToUniversalTime());
        }

        return _client.DirectoryExists(path) ? new RemoteEntry(path[(path.LastIndexOf('/') + 1)..], path, true, 0, DateTime.UtcNow) : null;
    });

    /// <inheritdoc />
    public IReadOnlyList<RemoteEntry> List(string folder) => Wrap(() =>
        _client.DirectoryExists(folder)
            ? (IReadOnlyList<RemoteEntry>)[.. _client.GetListing(folder).Where(f => f.Type is FtpObjectType.File or FtpObjectType.Directory).Select(Entry)]
            : []);

    /// <inheritdoc />
    public void Download(string path, Stream into, long from = 0) => Wrap<object?>(() =>
    {
        using var file = _client.OpenRead(path, FtpDataType.Binary, from);
        file.CopyTo(into);
        return null;
    });

    /// <inheritdoc />
    public void Upload(string path, Stream from) => Wrap<object?>(() =>
    {
        _client.UploadStream(from, path, FtpRemoteExists.Overwrite, true);
        return null;
    });

    /// <inheritdoc />
    public void Delete(string path) => Wrap<object?>(() =>
    {
        if (_client.FileExists(path))
        {
            _client.DeleteFile(path);
        }

        return null;
    });

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
