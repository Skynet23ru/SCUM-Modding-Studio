namespace ScumStudio.Core.Settings;

/// <summary>One server the app knows: a name and the folder it is reached at (a local folder or a network share).</summary>
/// <param name="Id">Stable key (a GUID text); never shown.</param>
/// <param name="Name">The name shown in the lists.</param>
/// <param name="Folder">The server's Paks folder, or any folder inside the server install (the app finds <c>SCUMServer.exe</c> from it).</param>
public sealed record ServerProfile(string Id, string Name, string Folder)
{
    /// <summary>How a remote server is reached over SFTP; null for a server in a local folder or a network share. For an SFTP server <see cref="Folder"/> is the Paks folder of its local mirror.</summary>
    public SftpAccess? Sftp { get; init; }

    /// <summary>True for a network share (<c>\\host\share\…</c>): slower, and "is the server running" cannot be asked of its process.</summary>
    public bool IsNetwork => ServerProfiles.IsNetworkPath(Folder);

    /// <summary>True for a server reached over SFTP (its files are mirrored on this PC).</summary>
    public bool IsSftp => Sftp is not null;
}

/// <summary>Where and how a remote server is reached over SFTP.</summary>
/// <param name="Host">Host name or address.</param>
/// <param name="Port">SSH port (22 by default).</param>
/// <param name="User">Login.</param>
/// <param name="RemoteRoot">The server's install folder on the host (the one that holds <c>SCUM</c>), with <c>/</c> separators.</param>
/// <param name="Password">The password, protected for the current Windows user (see <c>SecretText</c>); null when a key file is used.</param>
/// <param name="KeyFile">Path of a private key file, or null.</param>
/// <param name="HostKey">Fingerprint of the host key (for FTPS of the certificate), remembered at the first check and verified at every connection.</param>
/// <param name="AllowWrite">Mods, commands and settings may be written to the host (otherwise the server is only read).</param>
public sealed record SftpAccess(string Host, int Port, string User, string RemoteRoot, string? Password, string? KeyFile, string? HostKey, bool AllowWrite)
{
    /// <summary>The default SSH port.</summary>
    public const int DefaultPort = 22;

    /// <summary>How the host is reached (SFTP unless set).</summary>
    public RemoteProtocol Protocol { get; init; } = RemoteProtocol.Sftp;
}

/// <summary>The kind of connection to a remote server.</summary>
public enum RemoteProtocol
{
    /// <summary>SSH file transfer.</summary>
    Sftp,

    /// <summary>FTP over TLS (explicit); the certificate is remembered at the first check.</summary>
    Ftps,

    /// <summary>Plain FTP: nothing is encrypted (login and files travel in the open).</summary>
    Ftp,
}

/// <summary>
/// The list of servers kept in the settings and the one in use. The rest of the app keeps reading
/// <see cref="AppSettings.ServerPaksFolder"/>: it always holds the folder of the active server, so switching the server changes
/// that one value and every page follows. Pure functions over <see cref="AppSettings"/>.
/// </summary>
public static class ServerProfiles
{
    /// <summary>The most servers kept.</summary>
    public const int MaxServers = 20;

    /// <summary>True for a UNC path (<c>\\host\share</c> or <c>//host/share</c>).</summary>
    public static bool IsNetworkPath(string? folder) =>
        folder is { Length: > 2 } && (folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal));

    /// <summary>A name made from a folder: the folder that holds <c>SCUM</c> (<c>SCUM Server</c>), else the last folder name.</summary>
    public static string DefaultName(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var parts = folder.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var at = Array.FindIndex(parts, p => string.Equals(p, "SCUM", StringComparison.OrdinalIgnoreCase));
        var name = at > 0 ? parts[at - 1] : parts.Length > 0 ? parts[^1] : "Server";
        return IsNetworkPath(folder) && parts.Length > 0 ? parts[0] + ": " + name : name;
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim().TrimEnd('\\', '/'), b?.Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The server in use, or null.</summary>
    public static ServerProfile? Active(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Servers.FirstOrDefault(s => s.Id == settings.ActiveServerId);
    }

    /// <summary>
    /// Keeps the list and <see cref="AppSettings.ServerPaksFolder"/> in step: a folder set without a list becomes the first
    /// server (the update of an older version); a folder typed over the active server's changes that server's folder; a folder of
    /// another listed server makes it the active one. Returns <paramref name="settings"/> itself when nothing changes.
    /// </summary>
    public static AppSettings Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var folder = settings.ServerPaksFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            return settings.ActiveServerId is not null && Active(settings) is null ? settings with { ActiveServerId = null } : settings;
        }

        var active = Active(settings);
        if (active is not null && Same(active.Folder, folder))
        {
            return settings;
        }

        if (settings.Servers.FirstOrDefault(s => Same(s.Folder, folder)) is { } listed)
        {
            return settings with { ActiveServerId = listed.Id };
        }

        if (active is not null)
        {
            // The box of the folder was edited: the active server now lives there.
            return settings with { Servers = [.. settings.Servers.Select(s => s.Id == active.Id ? s with { Folder = folder } : s)] };
        }

        var added = new ServerProfile(Guid.NewGuid().ToString("N"), DefaultName(folder), folder);
        return settings with { Servers = [.. settings.Servers, added], ActiveServerId = added.Id };
    }

    /// <summary>Adds a server (and makes it the active one when <paramref name="activate"/>).</summary>
    /// <exception cref="ArgumentException">The folder is empty.</exception>
    public static AppSettings Add(AppSettings settings, string folder, string? name = null, bool activate = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var clean = folder?.Trim().Trim('"') ?? string.Empty;
        if (clean.Length == 0)
        {
            throw new ArgumentException("The server folder is empty.", nameof(folder));
        }

        if (settings.Servers.FirstOrDefault(s => Same(s.Folder, clean)) is { } known)
        {
            return activate ? Activate(settings, known.Id) : settings;
        }

        var profile = new ServerProfile(Guid.NewGuid().ToString("N"), string.IsNullOrWhiteSpace(name) ? DefaultName(clean) : name.Trim(), clean);
        var servers = settings.Servers.Count >= MaxServers ? settings.Servers.Skip(1).Append(profile).ToList() : [.. settings.Servers, profile];
        var next = settings with { Servers = servers };
        return activate ? Activate(next, profile.Id) : next;
    }

    /// <summary>The folder on this PC that mirrors the install of the SFTP server <paramref name="id"/>.</summary>
    public static string MirrorRoot(string id, string? dataDirectory = null) => Path.Combine(dataDirectory ?? StudioHome.GetDirectory(), "mirrors", id);

    /// <summary>Adds a server reached over SFTP and makes it the one in use. Its <see cref="ServerProfile.Folder"/> is the Paks folder of its mirror.</summary>
    /// <exception cref="ArgumentException">The host, user or folder is empty.</exception>
    public static AppSettings AddSftp(AppSettings settings, string name, SftpAccess access, bool activate = true, string? dataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(access);
        if (string.IsNullOrWhiteSpace(access.Host) || string.IsNullOrWhiteSpace(access.User) || string.IsNullOrWhiteSpace(access.RemoteRoot))
        {
            throw new ArgumentException("The host, the user and the server folder are all needed.", nameof(access));
        }

        var id = Guid.NewGuid().ToString("N");
        var paks = Path.Combine(MirrorRoot(id, dataDirectory), "SCUM", "Content", "Paks");
        var label = string.IsNullOrWhiteSpace(name) ? access.Host.Trim() : name.Trim();
        var clean = access with { Host = access.Host.Trim(), User = access.User.Trim(), RemoteRoot = access.RemoteRoot.Trim().Replace('\\', '/') };
        var profile = new ServerProfile(id, label, paks) { Sftp = clean };
        var servers = settings.Servers.Count >= MaxServers ? settings.Servers.Skip(1).Append(profile).ToList() : [.. settings.Servers, profile];
        var next = settings with { Servers = servers };
        return activate ? Activate(next, id) : next;
    }

    /// <summary>Changes how an SFTP server is reached (a new password, the remembered host key).</summary>
    public static AppSettings UpdateSftp(AppSettings settings, string id, Func<SftpAccess, SftpAccess> change)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(change);
        return settings with { Servers = [.. settings.Servers.Select(s => s.Id == id && s.Sftp is { } a ? s with { Sftp = change(a) } : s)] };
    }

    /// <summary>Makes a server the active one: its folder becomes <see cref="AppSettings.ServerPaksFolder"/>.</summary>
    public static AppSettings Activate(AppSettings settings, string id)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Servers.FirstOrDefault(s => s.Id == id) is { } profile
            ? settings with { ActiveServerId = profile.Id, ServerPaksFolder = profile.Folder }
            : settings;
    }

    /// <summary>Renames a server.</summary>
    public static AppSettings Rename(AppSettings settings, string id, string name)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var clean = name?.Trim() ?? string.Empty;
        return clean.Length == 0 ? settings : settings with { Servers = [.. settings.Servers.Select(s => s.Id == id ? s with { Name = clean } : s)] };
    }

    /// <summary>Takes a server off the list; when it was the active one, the next one (or none) takes its place.</summary>
    public static AppSettings Remove(AppSettings settings, string id)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Servers.All(s => s.Id != id))
        {
            return settings;
        }

        var left = settings.Servers.Where(s => s.Id != id).ToList();
        var next = settings with { Servers = left };
        if (settings.ActiveServerId != id)
        {
            return next;
        }

        return left.Count > 0 ? Activate(next with { ActiveServerId = null }, left[0].Id) : next with { ActiveServerId = null, ServerPaksFolder = null };
    }
}
