namespace ScumStudio.Modding.Remote;

/// <summary>A file or folder on the remote host.</summary>
/// <param name="Name">The name without the folder.</param>
/// <param name="Path">The full remote path (with <c>/</c> separators).</param>
/// <param name="IsDirectory">True for a folder.</param>
/// <param name="Size">Size in bytes (0 for a folder).</param>
/// <param name="ModifiedUtc">Last write time.</param>
public sealed record RemoteEntry(string Name, string Path, bool IsDirectory, long Size, DateTime ModifiedUtc);

/// <summary>
/// The few file operations the app needs on a server's host. SFTP implements it for real servers; a plain folder implements it for tests.
/// Paths use <c>/</c>. Every method may throw <see cref="IOException"/> when the connection is lost.
/// </summary>
public interface IRemoteFiles : IDisposable
{
    /// <summary>The entry at <paramref name="path"/>, or null when there is none.</summary>
    RemoteEntry? Stat(string path);

    /// <summary>The entries of a folder (empty when it does not exist).</summary>
    IReadOnlyList<RemoteEntry> List(string folder);

    /// <summary>Copies the file from byte <paramref name="from"/> to its end into <paramref name="into"/>.</summary>
    void Download(string path, Stream into, long from = 0);

    /// <summary>Writes the file (creating its folders, replacing what is there).</summary>
    void Upload(string path, Stream from);

    /// <summary>Deletes a file (nothing happens when there is none).</summary>
    void Delete(string path);
}
