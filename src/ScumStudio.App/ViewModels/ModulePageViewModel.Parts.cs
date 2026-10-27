using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.App.ViewModels;

/// <content>
/// Owner (2026-10-09): "more control over vehicles: attachments and parts". A vehicle's spawn presets (one part each:
/// which part every slot spawns with, from the parts that fit it) and a weapon's attachment sockets (which mount types
/// each socket takes) as rows of the Values tab, journaled and exported like any value (see <see cref="VehicleParts"/>,
/// <see cref="WeaponMounts"/>).
/// </content>
public abstract partial class ModulePageViewModel
{
    private (AssetCatalog Catalog, string Clones, IReadOnlyDictionary<string, IReadOnlyList<string>> Index)? _mountIndex;

    private (AssetCatalog Catalog, string Clones, IReadOnlyDictionary<string, IReadOnlyList<string>> Index)? _ammoIndex;

    /// <summary>The attachment package drawn highlighted in the 3D view (a part row's "show on the car"), or empty.</summary>
    [ObservableProperty]
    private string _highlightAttachment = string.Empty;

    /// <summary>Shows a part row's part highlighted on the vehicle in the 3D view (an empty slot: the part that holds it).</summary>
    [RelayCommand]
    private void ShowPartOnCar(TunableRowViewModel? row)
    {
        if (row is null || SelectedItem is null)
        {
            return;
        }

        var path = row.Value.Length > 0 ? row.Value : row.StockValue.Length > 0 ? row.StockValue : row.Tunable.ExportClass;
        HighlightAttachment = path.StartsWith('/') ? VehicleParts.PackageOf(path) : string.Empty;
        ShowPreviewTab = true;
    }

    /// <summary>The "Default parts" entries of a vehicle (one per spawn preset) or the "Attachments" entry of a weapon.</summary>
    private List<ModulePart> SlotParts(AssetCatalog catalog, ModuleItemViewModel item)
    {
        var parts = new List<ModulePart>();
        try
        {
            if (item.Asset.Kind == ModdableKind.Vehicle)
            {
                var clone = item.IsClone ? _services.Projects.Current?.State.FindCloneOf(item.PackagePath) : null;
                var presets = clone is not null
                    ? clone.Packages.Select(p => p.New).Where(p => p.StartsWith(VehicleParts.PresetFolder, StringComparison.OrdinalIgnoreCase))
                    : catalog.PackageFiles.Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName))
                        .Where(p => p.StartsWith(VehicleParts.PresetFolder, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(p => ModdableAssets.ReadImportedPackages(catalog, p).Contains(item.PackagePath, StringComparer.OrdinalIgnoreCase));
                var token = ModdableAssets.VehicleToken(item.Name).Replace("_", string.Empty, StringComparison.Ordinal);
                foreach (var preset in presets.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var leaf = PackageMap.Leaf(preset);
                    var name = leaf.StartsWith(token, StringComparison.OrdinalIgnoreCase) && leaf.Length > token.Length ? leaf[token.Length..] : leaf;
                    parts.Add(new ModulePart(Loc.F("Module.Part.Parts", name), preset, Loc.F("Module.Part.Parts.Tip", leaf)) { IsVehicleParts = true });
                }
            }
            else if (item.Asset.Kind == ModdableKind.Weapon && ReadForEditing(catalog, item.PackagePath).Names.Contains(WeaponMounts.Property))
            {
                parts.Add(new ModulePart(Loc.T("Module.Part.Attachments"), item.PackagePath, Loc.T("Module.Part.Attachments.Tip")) { IsWeaponMounts = true });
            }
            else if (item.Asset.Kind == ModdableKind.Magazine && ReadForEditing(catalog, item.PackagePath).Names.Contains(AmmoTypes.Property))
            {
                parts.Add(new ModulePart(Loc.T("Module.Part.Ammo"), item.PackagePath, Loc.T("Module.Part.Ammo.Tip")) { IsAmmoTypes = true });
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException)
        {
            _services.Logger.LogDebug("Parts of {Item}: {Message}", item.PackagePath, ex.Message);
        }

        return parts;
    }

    /// <summary>The rows of a "Default parts" or "Attachments" entry.</summary>
    private IReadOnlyList<Tunable> ReadSlots(AssetCatalog catalog, ModulePart part)
    {
        var package = ReadForEditing(catalog, part.PackagePath);
        if (part.IsWeaponMounts)
        {
            var kept = MountIndex(catalog);
            return WeaponMounts.Read(package, kept.Index);
        }

        if (part.IsAmmoTypes)
        {
            return AmmoTypes.Read(package, AmmoIndex(catalog));
        }

        return VehicleParts.Read(package, p => catalog.PackageExists(p) || _services.Projects.Current?.State.FindCloneOf(p) is not null ? TryRead(catalog, p) : null);
    }

    /// <summary>
    /// What fits each mount type: the game's items and the project's copies (a copied magazine or attachment), each copy under
    /// the mount type the project gave it. Kept until the copies or their mount types change.
    /// </summary>
    private (AssetCatalog Catalog, string Clones, IReadOnlyDictionary<string, IReadOnlyList<string>> Index) MountIndex(AssetCatalog catalog)
    {
        var state = _services.Projects.Current?.State;
        var clones = state?.AssetClones.SelectMany(c => c.Packages).Select(p => p.New).ToList() ?? [];
        string? OwnMount(string package) =>
            TryRead(catalog, package) is { } read && WeaponMounts.ItemMount(read) is { } item
                ? state?.GetAssetValue(package, item.Export, WeaponMounts.ItemMountProperty)?.Current is { Length: > 0 } own ? own : null
                : null;
        var signature = string.Join('|', clones.Select(c => c + "=" + OwnMount(c)));
        if (_mountIndex is not { } kept || !ReferenceEquals(kept.Catalog, catalog) || kept.Clones != signature)
        {
            _mountIndex = kept = (catalog, signature, WeaponMounts.Index(catalog, clones, p => TryRead(catalog, p), OwnMount));
        }

        return kept;
    }

    /// <summary>What ammunition types the game's and the project's ammunition items are (copies first); kept until the copies change.</summary>
    private IReadOnlyDictionary<string, IReadOnlyList<string>> AmmoIndex(AssetCatalog catalog)
    {
        var clones = _services.Projects.Current?.State.AssetClones.SelectMany(c => c.Packages).Select(p => p.New).ToList() ?? [];
        var signature = string.Join('|', clones);
        if (_ammoIndex is not { } kept || !ReferenceEquals(kept.Catalog, catalog) || kept.Clones != signature)
        {
            _ammoIndex = kept = (catalog, signature, AmmoTypes.Index(catalog, clones, p => TryRead(catalog, p)));
        }

        return kept.Index;
    }

    private CookedPackage? TryRead(AssetCatalog catalog, string packagePath)
    {
        try
        {
            return ReadForEditing(catalog, packagePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
