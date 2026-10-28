namespace ScumStudio.Modding.Remote;

/// <summary>A "remote host" that is a folder on this PC: lets the mirror be tested without a network.</summary>
public sealed class FolderRemote : IRemoteFiles
{
    private readonly string _root;

    /// <summary>Remote paths <c>/a/b</c> are read from <paramref name="root"/>\a\b.</summary>
    public FolderRemote(string root) => _root = Path.GetFullPath(root);

    /// <summary>True: the host can be made to fail, to test a lost connection.</summary>
    public bool Offline { get; set; }

    private string Local(string path) => Path.Combine(_root, path.Trim('/').Replace('/', Path.DirectorySeparatorChar));

    private void Check()
    {
        if (Offline)
        {
            throw new IOException("The connection is lost.");
        }
    }

    private static RemoteEntry Entry(FileSystemInfo info, string path) =>
        new(info.Name, path, info is DirectoryInfo, info is FileInfo f ? f.Length : 0, info.LastWriteTimeUtc);

    /// <inheritdoc />
    public RemoteEntry? Stat(string path)
    {
        Check();
        var local = Local(path);
        if (File.Exists(local))
        {
            return Entry(new FileInfo(local), path);
        }

        return Directory.Exists(local) ? Entry(new DirectoryInfo(local), path) : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<RemoteEntry> List(string folder)
    {
        Check();
        var local = Local(folder);
        if (!Directory.Exists(local))
        {
            return [];
        }

        return [.. new DirectoryInfo(local).GetFileSystemInfos().Select(i => Entry(i, folder.TrimEnd('/') + "/" + i.Name))];
    }

    /// <inheritdoc />
    public void Download(string path, Stream into, long from = 0)
    {
        Check();
        using var file = new FileStream(Local(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        file.Seek(from, SeekOrigin.Begin);
        file.CopyTo(into);
    }

    /// <inheritdoc />
    public void Upload(string path, Stream from)
    {
        Check();
        var local = Local(path);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        using var file = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        from.CopyTo(file);
    }

    /// <inheritdoc />
    public void Delete(string path)
    {
        Check();
        var local = Local(path);
        if (File.Exists(local))
        {
            File.Delete(local);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
