using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.Level.Editing;
using ScumStudio.Modding.Catalog;

namespace ScumStudio.App.ViewModels;

/// <summary>A magazine copy of the project the weapon copy can be limited to.</summary>
/// <param name="Package">The magazine copy's package path.</param>
/// <param name="Name">Its name as the list shows it.</param>
public sealed record MagazineChoice(string Package, string Name);

/// <content>
/// Owner (2026-10-27): "on a copy of a weapon I want only the copy of its magazine". The Attachments part of a weapon copy
/// offers the project's magazine copies; one press gives the magazine a mount type of its own and lets the weapon copy's
/// magazine socket take that type only (see <see cref="ExclusiveMagazine"/>).
/// </content>
public abstract partial class ModulePageViewModel
{
    /// <summary>The project's magazine copies.</summary>
    [ObservableProperty]
    private IReadOnlyList<MagazineChoice> _magazineChoices = [];

    /// <summary>The magazine copy picked.</summary>
    [ObservableProperty]
    private MagazineChoice? _selectedMagazine;

    /// <summary>True when the Attachments part of a weapon copy is shown and the project has a magazine copy.</summary>
    public bool ShowExclusiveMagazine => SelectedItem is { IsClone: true } && SelectedPart is { IsWeaponMounts: true } && MagazineChoices.Count > 0;

    /// <summary>Reads the project's magazine copies (after the part, the item or the project changed).</summary>
    private void RefreshMagazineChoices()
    {
        var picked = SelectedMagazine?.Package;
        MagazineChoices = [.. _services.Projects.Current?.State.AssetClones
            .Where(c => string.Equals(c.Kind, "magazine", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.NewPrimary, StringComparer.OrdinalIgnoreCase)
            .Select(c => new MagazineChoice(c.NewPrimary, ModuleItemViewModel.ShortName(c.NewPrimary))) ?? []];
        SelectedMagazine = MagazineChoices.FirstOrDefault(m => string.Equals(m.Package, picked, StringComparison.OrdinalIgnoreCase)) ?? MagazineChoices.FirstOrDefault();
        OnPropertyChanged(nameof(ShowExclusiveMagazine));
    }

    partial void OnMagazineChoicesChanged(IReadOnlyList<MagazineChoice> value) => OnPropertyChanged(nameof(ShowExclusiveMagazine));

    /// <summary>Limits the selected weapon copy to the picked magazine copy (journal: one step, undone with Ctrl+Z).</summary>
    [RelayCommand]
    private async Task MakeMagazineExclusiveAsync()
    {
        var weapon = SelectedItem;
        var magazine = SelectedMagazine;
        var part = SelectedPart;
        var catalog = _catalog;
        var project = _services.Projects.Current;
        if (weapon is not { IsClone: true } || magazine is null || part is not { IsWeaponMounts: true } || catalog is null || project is null)
        {
            return;
        }

        try
        {
            var index = MountIndex(catalog).Index;
            var step = ExclusiveMagazine.Plan(project.State, weapon.PackagePath, magazine.Package, p => TryRead(catalog, p), p => catalog.PackageExists(p), index);
            _services.Projects.Apply(step);
        }
        catch (InvalidOperationException ex)
        {
            _services.Notifications.Error(Loc.T("Module.ExclusiveMag.Failed"), ex.Message);
            return;
        }

        _services.Notifications.Success(Loc.T("Module.ExclusiveMag.Done"), Loc.F("Module.ExclusiveMag.DoneDetail", weapon.Name, magazine.Name));
        _io.Wait();
        try
        {
            _packageCache.Clear();
        }
        finally
        {
            _io.Release();
        }

        _partTask = LoadValuesAsync(catalog, part); // the sockets show the new mount type
        await _partTask.ConfigureAwait(true);
    }
}
