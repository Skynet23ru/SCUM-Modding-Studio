namespace ScumStudio.Modding.ServerMods;

/// <summary>
/// The Studio's own server mods (the bridge and the activity logger) that every server the app connects to gets: written
/// when they are missing and brought up to date when the Studio's version changed. A mod the owner switched off stays off,
/// and nothing is done where UE4SS is not installed (the Studio never installs UE4SS by itself).
/// </summary>
public static class ServerAutoMods
{
    /// <summary>The mods, by name and code.</summary>
    public static IReadOnlyList<(string Name, string Code)> Mods { get; } =
    [
        (ServerBridge.ModName, ServerBridge.Code),
        (ServerActivity.ModName, ServerActivity.Code),
    ];

    /// <summary>
    /// Makes sure the Studio's mods are on <paramref name="server"/>. Returns the names of the mods written or updated (empty
    /// when there was nothing to do). A new mod is loaded by the server at its next start; an updated one reloads at once.
    /// </summary>
    /// <exception cref="IOException">A mod file cannot be written.</exception>
    public static IReadOnlyList<string> Ensure(ServerInstall server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!server.HasUe4ss)
        {
            return [];
        }

        var store = new ServerModStore(server);
        var known = store.List();
        var changed = new List<string>();
        foreach (var (name, code) in Mods)
        {
            var existing = known.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is { Enabled: false })
            {
                continue; // switched off by the owner
            }

            if (existing is null || !string.Equals(Normalize(store.Read(name)), Normalize(code), StringComparison.Ordinal))
            {
                store.Save(name, code);
                changed.Add(name);
            }
        }

        return changed;
    }

    private static string Normalize(string? text) => (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
}
