using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Controls;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;

namespace ScumStudio.App.ViewModels;

/// <summary>One line of the item's history on the card.</summary>
/// <param name="Time">When.</param>
/// <param name="Player">Who.</param>
/// <param name="Action">What.</param>
/// <param name="Route">From where to where.</param>
public sealed record CardHistoryRow(string Time, string Player, string Action, string Route);

/// <summary>
/// The card of one activity line (opened from the Dashboard's Activity tab): every detail of the action, the island map with the
/// point where it happened (and the vehicle the item went to, when the line has its place), and the history of the same item.
/// </summary>
public sealed partial class ActivityCardViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices _services;

    /// <summary>Creates the card of <paramref name="row"/>; <paramref name="history"/> are the lines of the same item.</summary>
    public ActivityCardViewModel(AppServices services, DashActivityRow row, IReadOnlyList<DashActivityRow> history)
    {
        _services = services;
        Row = row;
        History = [.. history.Select(h => new CardHistoryRow(h.Time, h.Player, h.Action, Route(h)))];
        var markers = new List<SpawnMapMarker>();
        if (HasPoint)
        {
            markers.Add(new SpawnMapMarker(1, (float)row.X, (float)row.Y, SpawnMapLayer.Players));
        }

        if (row.HasToPlace)
        {
            markers.Add(new SpawnMapMarker(2, (float)row.ToX, (float)row.ToY, SpawnMapLayer.Vehicles));
        }

        Markers = markers;
        Title = Loc.F("Card.Title", row.Action, row.Player);
    }

    /// <summary>The line.</summary>
    public DashActivityRow Row { get; }

    /// <summary>The window title.</summary>
    public string Title { get; }

    /// <summary>The same item's lines, oldest first.</summary>
    public IReadOnlyList<CardHistoryRow> History { get; }

    /// <summary>The markers of the map: the player, and the container's owner when its place is known.</summary>
    public IReadOnlyList<SpawnMapMarker> Markers { get; }

    /// <summary>The line has a place on the map.</summary>
    public bool HasPoint => Row.X != 0 || Row.Y != 0;

    /// <summary>The item has a history to show.</summary>
    public bool HasHistory => History.Count > 0;

    /// <summary>The place as "X Y Z" (cm), for copying.</summary>
    public string Coordinates => string.Create(CultureInfo.InvariantCulture, $"{Row.X:0} {Row.Y:0} {Row.Z:0}");

    /// <summary>The island picture under the map (null while it loads, or when there is none).</summary>
    [ObservableProperty]
    private IImage? _picture;

    /// <summary>Where the picture came from, or that there is none.</summary>
    [ObservableProperty]
    private string _pictureText = string.Empty;

    private static string Route(DashActivityRow r) =>
        r.From.Length > 0 || r.To.Length > 0 ? (r.From.Length > 0 ? r.From : "?") + " → " + (r.To.Length > 0 ? r.To : "?") : string.Empty;

    /// <summary>Loads the island picture (imported, the game's or the terrain) on a worker.</summary>
    public async Task LoadPictureAsync()
    {
        try
        {
            var pictures = new IslandMapPicture(Path.Combine(_services.DataDirectory, "cache", "islandmap"), _services.Logger);
            var catalog = _services.Workspace.Catalog;
            var (_, bitmap) = await Task.Run(async () =>
            {
                var found = await pictures.GetAsync(catalog).ConfigureAwait(false);
                return (found, found.PngPath is { } png ? new Bitmap(png) : null);
            }).ConfigureAwait(true);
            Picture = bitmap;
            PictureText = bitmap is null ? Loc.T("Card.NoPicture") : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            _services.Logger.LogWarning("Activity card: no island picture: {Message}", ex.Message);
            PictureText = Loc.T("Card.NoPicture");
        }
    }

    /// <inheritdoc />
    public void Dispose() => (Picture as IDisposable)?.Dispose();
}
