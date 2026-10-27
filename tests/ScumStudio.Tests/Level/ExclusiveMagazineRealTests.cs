using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Modding;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner (2026-10-27): "on a copy of a weapon only the copy of its magazine". The magazine copy gets a mount type copy of its
/// own and the weapon copy's magazine socket takes that type only. Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class ExclusiveMagazineRealTests(ITestOutputHelper output)
{
    private const string Ak47 = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_AK47";
    private const string Mag = "/Game/ConZ_Files/Items/Weapons/Weapon_Clips/Magazine_AK47";
    private const string Folder = "/Game/ConZ_Files/Items/Weapons/";
    private const string StockMount = "/Game/ConZ_Files/Items/Weapons/AttachmentSockets/BP_MountTypeWeaponMagazine76239.BP_MountTypeWeaponMagazine76239_C";

    [Fact]
    public void TheWeaponCopyTakesOnlyTheMagazineCopy()
    {
        if (Open() is not { } catalog)
        {
            return;
        }

        using (catalog)
        {
            var state = new EditState();
            var weaponPlan = CloneFamilyPlanner.PlanItem(catalog, Ak47, "Weapon_AK47_Mine");
            var magPlan = CloneFamilyPlanner.PlanItem(catalog, Mag, "Magazine_AK47_Mine");
            foreach (var plan in new[] { weaponPlan, magPlan })
            {
                state.Apply(new CloneAssetOp("item", plan.Template, plan.NewPrimary, plan.Packages.Select(p => new PackagePair(p.Key, p.Value)).ToList()));
            }

            CookedPackage? Read(string p) => AssetEditing.ReadForEditing(catalog, state, p);
            var index = WeaponMounts.Index(catalog, state.AssetClones.SelectMany(c => c.Packages).Select(p => p.New), Read,
                p => state.GetAssetValue(p, WeaponMounts.ItemMount(Read(p)!)!.Value.Export, WeaponMounts.ItemMountProperty)?.Current);

            var batch = ExclusiveMagazine.Plan(state, weaponPlan.NewPrimary, magPlan.NewPrimary, Read, p => catalog.PackageExists(p), index);
            foreach (var op in batch.Ops)
            {
                output.WriteLine(op.Describe());
            }

            Assert.Null(state.Validate(batch));
            state.Apply(batch);
            var mountClone = Assert.Single(state.AssetClones, c => c.Kind == ExclusiveMagazine.Kind);
            Assert.StartsWith(Folder + "AttachmentSockets/BP_MountTypeMagazine_Magazine_AK47_Mine", mountClone.NewPrimary, StringComparison.Ordinal);

            // The mod: the mount type copy, the magazine re-pointed to it and the weapon's magazine socket changed.
            var result = AssetModBuilder.Build(catalog, ProjectExporter.BuildAssetRequest(state));
            foreach (var w in result.Warnings)
            {
                output.WriteLine("WARN " + w);
            }

            Assert.Contains(result.Packages, p => string.Equals(p.PackagePath, mountClone.NewPrimary, StringComparison.OrdinalIgnoreCase) && p.IsClone);
            var newMount = ExclusiveMagazine.ClassPath(mountClone.NewPrimary);
            var magazine = Parsed(Assert.Single(result.Packages, p => string.Equals(p.PackagePath, magPlan.NewPrimary, StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(newMount, WeaponMounts.ItemMount(magazine)?.Mount, ignoreCase: true);

            var weapon = Parsed(Assert.Single(result.Packages, p => string.Equals(p.PackagePath, weaponPlan.NewPrimary, StringComparison.OrdinalIgnoreCase)));
            var rows = WeaponMounts.Read(weapon, index).ToDictionary(r => r.Path[(r.Path.IndexOf(".Items#", StringComparison.Ordinal) + 7)..], r => r.Value, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("true", rows[newMount]);
            Assert.Equal("false", rows.GetValueOrDefault(StockMount, "false"));

            // The game's own reader (CUE4Parse) loads the three packages and the magazine's mount type is the new class.
            var dir = Path.Combine(Path.GetTempPath(), "scumstudio-exclusive-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var built in result.Packages.Where(p => p.IsClone))
                {
                    built.Bytes.WriteAsync(Path.Combine(dir, built.HeaderFilePath())[..^".uasset".Length]).GetAwaiter().GetResult();
                }

                using var loose = AssetCatalog.OpenLoose(dir);
                var mountPackage = loose.LoadPackage(mountClone.NewPrimary);
                Assert.Contains(mountPackage.GetExports(), e => e.Name == PackageMap.Leaf(mountClone.NewPrimary) + "_C");
                var cdo = loose.LoadPackage(magPlan.NewPrimary).GetExports().Single(e => e.Name.StartsWith("Default__", StringComparison.Ordinal));
                Assert.Equal(PackageMap.Leaf(mountClone.NewPrimary) + "_C", cdo.GetOrDefault<FPackageIndex>(WeaponMounts.ItemMountProperty).Name);
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

    private static CookedPackage Parsed(BuiltAssetPackage built) => CookedPackage.Parse(built.Bytes.UAsset, built.Bytes.UExp, built.UBulk, built.PackagePath);

    private static AssetCatalog? Open() =>
        Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks)
            ? AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() })
            : null;
}
