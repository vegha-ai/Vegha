using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Shell;

public partial class HistoryPanel : UserControl
{
    /// <summary>Distance from the bottom (in scroll units) at which to pre-fetch the next
    /// page. Roughly four item-rows; large enough that the new rows usually land before the
    /// user's thumb reaches the floor.</summary>
    private const double LoadMoreThresholdPx = 120;

    private ScrollViewer? _listScrollViewer;

    public HistoryPanel()
    {
        InitializeComponent();
        // The ListBox's inner ScrollViewer is created during template application — wait for
        // the panel's Loaded event before reaching into the visual tree.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<ListBox>("HistoryList") is { } list)
        {
            _listScrollViewer = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (_listScrollViewer is not null)
                _listScrollViewer.ScrollChanged += OnScrollChanged;
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_listScrollViewer is not null)
            _listScrollViewer.ScrollChanged -= OnScrollChanged;
        _listScrollViewer = null;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not HistoryViewModel vm) return;
        if (sender is not ScrollViewer sv) return;
        // Fire only when the user is scrolling downward, has crossed the near-bottom threshold,
        // and the VM isn't already mid-fetch. The VM's CanExecute also guards against duplicate
        // requests if a re-layout fires multiple ScrollChanged events for the same offset.
        if (e.OffsetDelta.Y <= 0) return;
        var distanceFromBottom = sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height;
        if (distanceFromBottom > LoadMoreThresholdPx) return;
        if (vm.LoadMoreCommand.CanExecute(null))
            vm.LoadMoreCommand.Execute(null);
    }

    private void OnRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not HistoryViewModel vm) return;
        if (sender is Control { Tag: HistoryRow row })
        {
            // Re-trigger an open even when the user double-clicks the same row that's already
            // selected — clear the selection first so OnSelectedRowChanged fires anew.
            vm.SelectedRow = null;
            vm.SelectedRow = row;
            e.Handled = true;
        }
    }

    // Context-menu handlers reach the row via MenuItem.Tag rather than a binding to
    // $parent — bindings break inside Avalonia popups because the popup is hosted outside
    // the visual tree where the ItemsControl ancestor lookup would resolve.

    private void OnDelete_Click(object? sender, RoutedEventArgs e) =>
        InvokeOnRow(sender, vm => vm.DeleteCommand);

    private void InvokeOnRow(
        object? sender,
        Func<HistoryViewModel, System.Windows.Input.ICommand> commandSelector)
    {
        if (DataContext is not HistoryViewModel vm) return;
        if (sender is Control { Tag: HistoryRow row })
            commandSelector(vm).Execute(row);
    }
}
