using CUE4Parse.UE4.Assets.Exports;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Owner (2026-10-27): "choose which ammunition is loaded into a magazine". A magazine's <c>AmmunitionTags</c> container
/// lists the ammunition types it takes; the Ammunition part switches types on and off. Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class AmmoTypesRealTests(ITestOutputHelper output)
{
    private const string Mag = "/Game/ConZ_Files/Items/Weapons/Weapon_Clips/Magazine_AK47";
    private const string Own = "AmmunitionType.Cal762x39mm";

    [Fact]
    public void AMagazineTakesTheAmmunitionTypesSwitchedOn()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return;
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var index = AmmoTypes.Index(catalog);
        foreach (var e in index)
        {
            output.WriteLine($"{e.Key}: {string.Join(", ", e.Value.Select(v => v[(v.LastIndexOf('/') + 1)..]))}");
        }

        Assert.Contains(index[Own], p => p.EndsWith("/Cal_7_62x39mm", StringComparison.Ordinal));
        var magazine = ModdableAssets.ReadPackage(catalog, Mag);
        var rows = AmmoTypes.Read(magazine, index);
        Assert.Equal("true", Assert.Single(rows, r => r.Path.EndsWith("#" + Own, StringComparison.Ordinal)).Value);
        var other = rows.First(r => r.Value == "false");
        output.WriteLine("adds " + other.Path);

        var result = AssetModBuilder.Build(catalog, new AssetModRequest([], new Dictionary<string, IReadOnlyList<TunableEdit>>
        {
            [Mag] = [new(other.Export, other.Path, "true")],
        }));
        Assert.Empty(result.Warnings);
        var built = Assert.Single(result.Packages, p => p.PackagePath == Mag);
        var after = CookedPackage.Parse(built.Bytes.UAsset, built.Bytes.UExp, built.UBulk, built.PackagePath);
        var now = AmmoTypes.Read(after, index).ToDictionary(r => r.Path, r => r.Value);
        Assert.Equal("true", now[other.Path]);
        Assert.Equal("true", now["AmmunitionTags#" + Own]);

        // Every other stored value is untouched.
        var stock = TunableReader.Read(magazine).ToDictionary(t => t.Key, t => t.Value);
        var edited = TunableReader.Read(after).ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal(stock["Default__Magazine_AK47_C|_capacity"], edited["Default__Magazine_AK47_C|_capacity"]);

        // The game's own reader (CUE4Parse) agrees.
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-ammo-" + Guid.NewGuid().ToString("N"));
        try
        {
            built.Bytes.WriteAsync(Path.Combine(dir, built.HeaderFilePath())[..^".uasset".Length]).GetAwaiter().GetResult();
            using var loose = AssetCatalog.OpenLoose(dir);
            var cdo = loose.LoadPackage(Mag).GetExports().Single(e => e.Name == "Default__Magazine_AK47_C");
            var tags = cdo.GetOrDefault<CUE4Parse.UE4.Objects.GameplayTags.FGameplayTagContainer>(AmmoTypes.Property).GameplayTags.Select(t => t.TagName.Text).ToList();
            output.WriteLine(string.Join(", ", tags));
            Assert.Contains(Own, tags);
            Assert.Contains(AmmoTypes.Label(other.Path[(other.Path.IndexOf('#', StringComparison.Ordinal) + 1)..]), string.Join('|', tags));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
