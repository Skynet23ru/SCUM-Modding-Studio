using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace ScumStudio.Modding.Remote;

/// <summary>How to connect to the host.</summary>
/// <param name="Host">Host name or address.</param>
/// <param name="Port">SSH port.</param>
/// <param name="User">Login.</param>
/// <param name="Password">Plain password, or null when a key file is used.</param>
/// <param name="KeyFile">Private key file, or null.</param>
/// <param name="TrustedHostKey">The remembered host key fingerprint; null on the first connection (the one seen is then reported, not trusted silently by later calls).</param>
public sealed record SftpConnection(string Host, int Port, string User, string? Password, string? KeyFile, string? TrustedHostKey)
{
    /// <summary>SFTP unless set; FTP and FTPS are served by <see cref="FtpRemote"/>.</summary>
    public ScumStudio.Core.Settings.RemoteProtocol Protocol { get; init; } = ScumStudio.Core.Settings.RemoteProtocol.Sftp;
}

/// <summary>The host's key does not match the remembered one: someone else may be answering.</summary>
public sealed class HostKeyMismatchException(string expected, string seen)
    : IOException($"The host key changed. Expected {expected}, the host showed {seen}.")
{
    /// <summary>The remembered fingerprint.</summary>
    public string Expected { get; } = expected;

    /// <summary>The fingerprint the host showed.</summary>
    public string Seen { get; } = seen;
}

/// <summary>Files of a host over SFTP (SSH.NET).</summary>
public sealed class SftpRemote : IRemoteFiles
{
    private readonly SftpClient _client;
    private string? _seenKey;
    private string? _mismatch;

    /// <summary>Connects to the host. A first connection (no remembered key) accepts the key and reports it in <see cref="HostKey"/>.</summary>
    /// <exception cref="HostKeyMismatchException">The key differs from the remembered one.</exception>
    /// <exception cref="IOException">The host cannot be reached or the login is refused.</exception>
    public SftpRemote(SftpConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(connection.KeyFile))
        {
            try
            {
                var key = string.IsNullOrEmpty(connection.Password) ? new PrivateKeyFile(connection.KeyFile) : new PrivateKeyFile(connection.KeyFile, connection.Password);
                methods.Add(new PrivateKeyAuthenticationMethod(connection.User, key));
            }
            catch (Exception ex) when (ex is SshException or IOException or ArgumentException)
            {
                throw new IOException("The key file cannot be read: " + ex.Message, ex);
            }
        }
        else
        {
            methods.Add(new PasswordAuthenticationMethod(connection.User, connection.Password ?? string.Empty));
        }

        var info = new ConnectionInfo(connection.Host, connection.Port, connection.User, [.. methods]) { Timeout = TimeSpan.FromSeconds(15) };
        _client = new SftpClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30), OperationTimeout = TimeSpan.FromSeconds(60) };
        _client.HostKeyReceived += (_, e) =>
        {
            _seenKey = "SHA256:" + e.FingerPrintSHA256.TrimEnd('=');
            if (connection.TrustedHostKey is { Length: > 0 } trusted && !string.Equals(trusted, _seenKey, StringComparison.Ordinal))
            {
                _mismatch = trusted;
                e.CanTrust = false;
            }
        };
        try
        {
            _client.Connect();
        }
        catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or TimeoutException or IOException)
        {
            _client.Dispose();
            if (_mismatch is not null)
            {
                throw new HostKeyMismatchException(_mismatch, _seenKey ?? "?");
            }

            throw new IOException(ex.Message, ex);
        }
    }

    /// <summary>The fingerprint of the host key seen at the connection (<c>SHA256:…</c>).</summary>
    public string? HostKey => _seenKey;

    private static RemoteEntry Entry(ISftpFile f) => new(f.Name, f.FullName, f.IsDirectory, f.IsDirectory ? 0 : f.Length, f.LastWriteTimeUtc);

    private static T Wrap<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is SshException or TimeoutException)
        {
            throw new IOException(ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public RemoteEntry? Stat(string path)
    {
        try
        {
            return Wrap(() => _client.Exists(path) ? Entry(_client.Get(path)) : null);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RemoteEntry> List(string folder)
    {
        try
        {
            return Wrap(() => (IReadOnlyList<RemoteEntry>)[.. _client.ListDirectory(folder).Where(f => f.Name is not ("." or "..")).Select(Entry)]);
        }
        catch (SftpPathNotFoundException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public void Download(string path, Stream into, long from = 0)
    {
        Wrap<object?>(() =>
        {
            using var file = _client.OpenRead(path);
            file.Seek(from, SeekOrigin.Begin);
            file.CopyTo(into);
            return null;
        });
    }

    /// <inheritdoc />
    public void Upload(string path, Stream from)
    {
        Wrap<object?>(() =>
        {
            var folder = path[..Math.Max(0, path.LastIndexOf('/'))];
            var built = path.StartsWith('/') ? string.Empty : null;
            foreach (var part in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                built = built is null ? part : built + "/" + part;
                if (!_client.Exists(built))
                {
                    _client.CreateDirectory(built);
                }
            }

            _client.UploadFile(from, path, true);
            return null;
        });
    }

    /// <inheritdoc />
    public void Delete(string path) => Wrap<object?>(() =>
    {
        if (_client.Exists(path))
        {
            _client.DeleteFile(path);
        }

        return null;
    });

    /// <inheritdoc />
    public void Dispose()
    {
        if (_client.IsConnected)
        {
            _client.Disconnect();
        }

        _client.Dispose();
    }
}
