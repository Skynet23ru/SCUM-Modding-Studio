using System.Buffers.Binary;
using System.Globalization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Modding.Tuning;

/// <summary>
/// Which ammunition a SCUM magazine takes. A magazine's CDO stores <c>AmmunitionTags</c>, a <c>GameplayTagContainer</c> of
/// ammunition types (<c>AmmunitionType.Cal762x39mm</c>), and every ammunition item stores its own <c>AmmunitionTag</c>: the
/// magazine takes the ammunition whose type is in its container. One <see cref="TunableKind.Mount"/> tunable per ammunition
/// type: path <c>AmmunitionTags#&lt;tag&gt;</c>, value <c>true</c> when the magazine takes it. A copy of an ammunition item
/// keeps the type of the stock one, so it loads wherever the stock one does.
/// </summary>
public static class AmmoTypes
{
    /// <summary>The magazine property (a gameplay tag container).</summary>
    public const string Property = "AmmunitionTags";

    /// <summary>The ammunition item property (one gameplay tag).</summary>
    public const string ItemProperty = "AmmunitionTag";

    /// <summary>Folder searched for ammunition items.</summary>
    public const string ItemFolder = ModdableAssets.ConZ + "Items/Ammunition/";

    private const string Marker = "#";

    /// <summary>True for an edit this class writes.</summary>
    public static bool IsEdit(string path) => path.StartsWith(Property + Marker, StringComparison.Ordinal);

    /// <summary>The ammunition type of an ammunition item (<c>AmmunitionType.Cal762x39mm</c>), or null when it is not one.</summary>
    public static string? TypeOf(CookedPackage item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var cdo = Cdo(item);
        if (cdo < 0 || !item.Names.Contains(ItemProperty))
        {
            return null;
        }

        return item.ReadProperties(cdo).Find(ItemProperty)?.Value switch
        {
            StructValue s => s.Find("TagName")?.Value is NameValue n ? n.Value : null,
            NameValue n => n.Value,
            _ => null,
        };
    }

    /// <summary>Ammunition type → the ammunition items (package paths) of it; <paramref name="clones"/> (the project's copies) come first.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Index(AssetCatalog catalog, IEnumerable<string>? clones = null, Func<string, CookedPackage?>? read = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        bool Wanted(string p) => p.StartsWith(ItemFolder, StringComparison.OrdinalIgnoreCase) && !p.EndsWith("_ES", StringComparison.OrdinalIgnoreCase);
        var own = (clones ?? []).Where(p => read is not null && Wanted(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var packages = own.Concat(catalog.PackageFiles.Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName)).Where(Wanted))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in packages)
        {
            try
            {
                var package = own.Contains(path, StringComparer.OrdinalIgnoreCase) ? read!(path) ?? throw new FileNotFoundException(path) : ModdableAssets.ReadPackage(catalog, path);
                if (TypeOf(package) is { } type)
                {
                    (index.TryGetValue(type, out var list) ? list : index[type] = []).Add(path);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException or ArgumentOutOfRangeException or EndOfStreamException)
            {
                // Not a cooked item package (textures, meshes, curves …).
            }
        }

        return index.ToDictionary(e => e.Key, e => (IReadOnlyList<string>)e.Value
            .OrderBy(p => own.Contains(p, StringComparer.OrdinalIgnoreCase) ? 0 : 1).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One row per ammunition type: the types the magazine takes first, then the others (every type of the game's ammunition).
    /// Empty when the magazine does not store its own <see cref="Property"/> (a variant inheriting its parent's).
    /// </summary>
    public static IReadOnlyList<Tunable> Read(CookedPackage magazine, IReadOnlyDictionary<string, IReadOnlyList<string>> index)
    {
        ArgumentNullException.ThrowIfNull(magazine);
        ArgumentNullException.ThrowIfNull(index);
        var cdo = Cdo(magazine);
        if (cdo < 0 || magazine.ReadProperties(cdo).Find(Property)?.Value is not ArrayValue tags)
        {
            return [];
        }

        var key = TunableReader.ExportKeys(magazine)[cdo];
        var present = tags.Items.OfType<NameValue>().Select(n => n.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return [.. present.Concat(index.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => present.Contains(t, StringComparer.OrdinalIgnoreCase) ? 0 : 1).ThenBy(Label, StringComparer.OrdinalIgnoreCase)
            .Select(type =>
            {
                var items = index.GetValueOrDefault(type) ?? [];
                return new Tunable(key, Property + Marker + type, TunableKind.Mount, present.Contains(type, StringComparer.OrdinalIgnoreCase) ? "true" : "false")
                {
                    Name = Label(type) + (items.Count > 0 ? ": " + string.Join(", ", items.Take(4).Select(PackageMap.Leaf)) + (items.Count > 4 ? " …" : string.Empty) : string.Empty),
                    ExportClass = magazine.GetExportClassName(cdo),
                    Group = "Ammunition",
                };
            })];
    }

    /// <summary>Readable type: <c>AmmunitionType.Cal762x39mm</c> → <c>Cal762x39mm</c>.</summary>
    public static string Label(string type) => type[(type.LastIndexOf('.') + 1)..];

    /// <summary>
    /// Writes the edits: a type set to <c>false</c> leaves the container, one set to <c>true</c> joins it (its name is added
    /// to the name table when the package lacks it). The container's size is fixed up.
    /// </summary>
    /// <exception cref="InvalidOperationException">The magazine stores no <see cref="Property"/>, or an edit did not read back.</exception>
    public static PackageBytes Apply(CookedPackage magazine, IEnumerable<TunableEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(magazine);
        ArgumentNullException.ThrowIfNull(edits);
        var list = edits.ToList();
        var cdo = Cdo(magazine);
        var payload = cdo >= 0 ? magazine.GetExportData(cdo).ToArray() : [];
        var block = cdo >= 0 ? PropertyReader.ReadPayload(magazine, payload, cdo) : null;
        if (block?.Find(Property) is not { Value: ArrayValue current } tag)
        {
            throw new InvalidOperationException($"{magazine.BasePath}: no {Property} stored.");
        }

        var tables = PackageTables.Of(magazine, cdo);
        var want = list.ToDictionary(e => e.Path[(Property.Length + Marker.Length)..], e => TunableValue.ParseBool(e.Value), StringComparer.OrdinalIgnoreCase);
        var written = new List<byte>();
        var kept = 0;
        var stored = current.Items.OfType<NameValue>().ToList();
        for (var i = 0; i < stored.Count; i++)
        {
            if (!want.TryGetValue(stored[i].Value, out var on) || on)
            {
                written.AddRange(payload.AsSpan(tag.ValueOffset + 4 + i * 8, 8).ToArray());
                kept++;
            }
        }

        foreach (var add in want.Where(w => w.Value && !stored.Any(n => string.Equals(n.Value, w.Key, StringComparison.OrdinalIgnoreCase))).Select(w => w.Key))
        {
            var name = tables.Name(add);
            var entry = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(entry, name.Index);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(4), name.Number);
            written.AddRange(entry);
            kept++;
        }

        var count = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(count, kept);
        var delta = 4 + written.Count - (tag.EndOffset - tag.ValueOffset);
        payload = [.. payload.AsSpan(0, tag.ValueOffset), .. count, .. written, .. payload.AsSpan(tag.EndOffset)];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(tag.SizeFieldOffset), BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(tag.SizeFieldOffset)) + delta);
        var data = Enumerable.Range(0, magazine.Exports.Count).Select(i => i == cdo ? (ReadOnlyMemory<byte>)payload : magazine.GetExportData(i)).ToArray();
        var bytes = PackageWriter.Build(PackageWriter.ToBuildInput(magazine) with
        {
            Names = tables.Names,
            NameIsWide = tables.Wide,
            Imports = tables.Imports,
            Exports = tables.Exports,
            PreloadDependencies = tables.Preload,
            ExportData = data,
        });

        var check = CookedPackage.Parse(bytes.UAsset, bytes.UExp, magazine.UBulk, magazine.BasePath);
        var now = check.ReadProperties(Cdo(check)).Find(Property)?.Value is ArrayValue a ? a.Items.OfType<NameValue>().Select(n => n.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        foreach (var edit in list)
        {
            var type = edit.Path[(Property.Length + Marker.Length)..];
            if (now.Contains(type) != TunableValue.ParseBool(edit.Value))
            {
                throw new InvalidOperationException($"{magazine.BasePath}: '{edit.Path}' did not read back as {edit.Value}.");
            }
        }

        return bytes;
    }

    private static int Cdo(CookedPackage package) =>
        Enumerable.Range(0, package.Exports.Count).FirstOrDefault(i => package.ResolveName(package.Exports[i].ObjectName).StartsWith("Default__", StringComparison.Ordinal), -1);
}
