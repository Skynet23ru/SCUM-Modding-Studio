using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ScumStudio.App.ViewModels;

namespace ScumStudio.App.Views.Pages;

/// <summary>The Console page.</summary>
public partial class ConsolePageView : UserControl
{
    private ConsolePageViewModel? _shown;

    /// <summary>Creates the view.</summary>
    public ConsolePageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ConsolePageViewModel);
    }

    private void Attach(ConsolePageViewModel? model)
    {
        if (_shown is not null)
        {
            _shown.Lines.CollectionChanged -= OnLinesChanged;
        }

        _shown = model;
        if (model is not null)
        {
            model.Lines.CollectionChanged += OnLinesChanged;
        }
    }

    // The view follows the newest line while "Follow" is on.
    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_shown is not { Follow: true, IsPaused: false } || _shown.Lines.Count == 0 || e.Action == NotifyCollectionChangedAction.Remove)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_shown is { Lines.Count: > 0 } model)
            {
                LogList.ScrollIntoView(model.Lines[^1]);
            }
        }, DispatcherPriority.Background);
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_shown is not null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(_shown.ShownText());
        }
    }

    private void OnSendKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _shown is { CanSend: true } model)
        {
            model.SendCommand.Execute(null);
            e.Handled = true;
        }
    }
}
