using Avalonia.Controls;
using ScumStudio.App.ViewModels;

namespace ScumStudio.App.Views.Pages;

/// <summary>The Dashboard page.</summary>
public partial class DashboardPageView : UserControl
{
    /// <summary>Creates the view.</summary>
    public DashboardPageView()
    {
        InitializeComponent();
    }

    // A click on a line of the Activity tab opens its card (a window with the map); the line is unpicked so it can be clicked again.
    private void OnActivityPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem is DashActivityRow row && DataContext is DashboardPageViewModel page)
        {
            list.SelectedItem = null;
            var window = new Views.ActivityCardWindow { DataContext = page.CreateCard(row) };
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }
        }
    }
}
