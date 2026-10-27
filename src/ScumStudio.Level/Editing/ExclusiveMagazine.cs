using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Level.Editing;

/// <summary>
/// "On a copy of a weapon only the copy of its magazine": a SCUM weapon takes a magazine when the magazine's mount type
/// (<c>BP_MountType…_C</c>, a small Blueprint class) is one of its socket's. A copied magazine keeps the mount type of the
/// stock one, so the copy of a weapon takes the stock magazines too. This plans the steps that give the magazine copy a
/// mount type of its own and let the weapon copy's magazine socket take that type only:
/// a copy of the stock mount type (<see cref="CloneAssetOp"/> of kind <see cref="Kind"/>), the magazine copy re-pointed to
/// it, and the weapon copy's socket rows (<see cref="WeaponMounts"/>) switched from the stock type to the new one.
/// </summary>
public static class ExclusiveMagazine
{
    /// <summary>The <see cref="CloneAssetOp.Kind"/> of a mount type copy.</summary>
    public const string Kind = "mounttype";

    private const string Marker = ".Items#";

    /// <summary>
    /// The steps for <paramref name="weapon"/> (a clone's package) to take <paramref name="magazine"/> (a clone's package) only.
    /// </summary>
    /// <param name="state">The project's edits (what the weapon's sockets and the magazine's mount type are now).</param>
    /// <param name="weapon">The weapon copy's package path.</param>
    /// <param name="magazine">The magazine copy's package path.</param>
    /// <param name="read">Reads a package of the game or the project (null when there is none).</param>
    /// <param name="exists">True for a package path the game or the project already has.</param>
    /// <param name="index">Mount type → the items that fit it (<see cref="WeaponMounts.Index"/>).</param>
    /// <exception cref="InvalidOperationException">A package cannot be read, the magazine has no mount type, or the weapon's sockets do not take it.</exception>
    public static BatchOp Plan(EditState state, string weapon, string magazine, Func<string, CookedPackage?> read, Func<string, bool> exists,
        IReadOnlyDictionary<string, IReadOnlyList<string>> index)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(index);
        var weaponPackage = read(weapon) ?? throw new InvalidOperationException($"{weapon} cannot be read.");
        var magazinePackage = read(magazine) ?? throw new InvalidOperationException($"{magazine} cannot be read.");
        var (export, stored) = WeaponMounts.ItemMount(magazinePackage) ?? throw new InvalidOperationException($"{magazine} has no mount type.");
        var current = state.GetAssetValue(magazine, export, WeaponMounts.ItemMountProperty)?.Current;
        var oldMount = string.IsNullOrEmpty(current) ? stored : current;

        // The sockets of the weapon copy that take the magazine's present mount type.
        var sockets = WeaponMounts.Read(weaponPackage, index)
            .Where(t => t.Kind == TunableKind.Mount && MountOf(t.Path) is { } m && string.Equals(m, oldMount, StringComparison.OrdinalIgnoreCase))
            .Where(t => (state.GetAssetValue(weapon, t.Export, t.Path)?.Current ?? t.Value) == "true")
            .ToList();
        if (sockets.Count == 0)
        {
            throw new InvalidOperationException($"{weapon} has no socket that takes the mount type of {magazine}; allow it in the Attachments part first.");
        }

        var ops = new List<EditOp>();
        var newMount = oldMount;
        var own = state.AssetClones.Any(c => c.Kind == Kind && string.Equals(ClassPath(c.NewPrimary), oldMount, StringComparison.OrdinalIgnoreCase));
        if (!own)
        {
            // A copy of the stock mount type beside it; the magazine copy is re-pointed to it.
            var template = PackageMap.Normalize(VehicleParts.PackageOf(oldMount));
            var taken = state.AssetClones.SelectMany(c => c.Packages.Select(p => p.New)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stem = "BP_MountTypeMagazine_" + CloneFamilyPlanner.SafeName(PackageMap.Leaf(magazine));
            var package = PackageMap.Folder(template) + "/" + stem;
            for (var n = 2; taken.Contains(package) || exists(package); n++)
            {
                package = PackageMap.Folder(template) + "/" + CloneFamilyPlanner.NumberedName(stem, n);
            }

            newMount = ClassPath(package);
            ops.Add(new CloneAssetOp(Kind, template, package, [new PackagePair(template, package)]));
            ops.Add(new SetAssetValueOp(magazine, export, WeaponMounts.ItemMountProperty, nameof(TunableKind.Text), oldMount, newMount));
        }

        foreach (var socket in sockets)
        {
            var prefix = socket.Path[..socket.Path.IndexOf(Marker, StringComparison.Ordinal)];
            ops.Add(new SetAssetValueOp(weapon, socket.Export, socket.Path, nameof(TunableKind.Mount), "true", "false"));
            if (!own)
            {
                ops.Add(new SetAssetValueOp(weapon, socket.Export, prefix + Marker + newMount, nameof(TunableKind.Mount), "false", "true"));
            }
        }

        return new BatchOp($"Only {PackageMap.Leaf(magazine)} on {PackageMap.Leaf(weapon)}", ops);
    }

    /// <summary>The class path of a Blueprint package: <c>/Game/…/BP_X</c> → <c>/Game/…/BP_X.BP_X_C</c>.</summary>
    public static string ClassPath(string package) => package + "." + PackageMap.Leaf(package) + "_C";

    private static string? MountOf(string path)
    {
        var at = path.IndexOf(Marker, StringComparison.Ordinal);
        return at < 0 ? null : path[(at + Marker.Length)..];
    }
}
