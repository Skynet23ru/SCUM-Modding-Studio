using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ScumStudio.App.Controls;
using ScumStudio.App.ViewModels;

namespace ScumStudio.App.Views;

/// <summary>The card of one activity line: its data, the island map with the point, the history of the item.</summary>
public partial class ActivityCardWindow : Window
{
    /// <summary>Creates the window.</summary>
    public ActivityCardWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not ActivityCardViewModel model)
        {
            return;
        }

        // The map is laid out now: zoom to the point at once, then again when the picture is in.
        Dispatcher.UIThread.Post(CenterOnPoint, DispatcherPriority.Loaded);
        await model.LoadPictureAsync();
        Dispatcher.UIThread.Post(CenterOnPoint, DispatcherPriority.Background);
    }

    private void CenterOnPoint()
    {
        if (DataContext is ActivityCardViewModel { HasPoint: true } model && this.FindControl<SpawnMap>("CardMap") is { } map)
        {
            map.CenterOn((float)model.Row.X, (float)model.Row.Y, 2.0);
        }
    }

    private void OnCenterClick(object? sender, RoutedEventArgs e) => CenterOnPoint();

    private async void OnCopyCoords(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ActivityCardViewModel model && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(model.Coordinates);
        }
    }
}
