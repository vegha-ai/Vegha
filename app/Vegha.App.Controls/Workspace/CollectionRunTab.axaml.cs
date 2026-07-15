using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Vegha.App.ViewModels.Tabs;

namespace Vegha.App.Controls.Workspace;

public partial class CollectionRunTab : UserControl
{
    // Remembered width of the detail pane so a drag persists across open/close of the pane.
    private double _lastDetailWidth = 480;
    private CollectionRunTabViewModel? _hookedVm;

    public CollectionRunTab()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Delay (ms) is a plain text input — reject non-digit keystrokes at the tunnel stage
        // so only whole numbers ever land in the box (the int binding handles the rest).
        DelayInput.AddHandler(TextInputEvent, (_, e) =>
        {
            if (e.Text is { Length: > 0 } t && !t.All(char.IsAsciiDigit)) e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_hookedVm is not null) _hookedVm.PropertyChanged -= OnVmPropertyChanged;
        _hookedVm = DataContext as CollectionRunTabViewModel;
        if (_hookedVm is not null) _hookedVm.PropertyChanged += OnVmPropertyChanged;
        ApplyDetailWidth();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CollectionRunTabViewModel.ShowDetail))
            ApplyDetailWidth();
    }

    /// <summary>Expands the detail column to its remembered pixel width when a row is selected,
    /// and collapses it to zero (no reserved gap) when nothing is selected. The GridSplitter
    /// writes the dragged size back into the column, which we capture before collapsing.</summary>
    private void ApplyDetailWidth()
    {
        if (_hookedVm is null || ResultsSplitGrid.ColumnDefinitions.Count < 3) return;
        var col = ResultsSplitGrid.ColumnDefinitions[2];
        if (_hookedVm.ShowDetail)
        {
            col.MinWidth = 320;
            col.Width = new GridLength(_lastDetailWidth, GridUnitType.Pixel);
        }
        else
        {
            if (col.Width.IsAbsolute && col.Width.Value > 0)
                _lastDetailWidth = System.Math.Clamp(col.Width.Value, 320, 1200);
            col.MinWidth = 0;
            col.Width = new GridLength(0);
        }
    }

    private async void OnPickDataFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CollectionRunTabViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select iteration data file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("CSV / JSON")
                {
                    Patterns = new[] { "*.csv", "*.json" },
                },
                new FilePickerFileType("All files") { Patterns = new[] { "*.*" } },
            },
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        await vm.PickDataFileAsync(path);
    }

    private async void OnCopyCli_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CollectionRunTabViewModel vm) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(vm.CliCommand);
    }

    // ---- Manual resize of the config Run-Sequence panel ----
    // A GridSplitter didn't drag reliably under this app's theme, so the divider simply follows
    // the pointer's X position within the grid — inherently bidirectional, no delta bookkeeping.

    private bool _splitDrag;

    private void OnConfigSplitPressed(object? sender, PointerPressedEventArgs e)
    {
        _splitDrag = true;
        e.Pointer.Capture(ConfigSplitter);
        e.Handled = true;
    }

    private void OnConfigSplitMove(object? sender, PointerEventArgs e)
    {
        if (!_splitDrag) return;
        var gw = ConfigSplitGrid.Bounds.Width;
        if (gw <= 0) return;
        // Left column follows the cursor; keep 320px for the right panel and 220px for the left.
        var max = Math.Max(220, gw - ConfigSplitGrid.ColumnDefinitions[1].ActualWidth - 320);
        var w = Math.Clamp(e.GetPosition(ConfigSplitGrid).X, 220, max);
        ConfigSplitGrid.ColumnDefinitions[0].Width = new GridLength(w, GridUnitType.Pixel);
    }

    private void OnConfigSplitReleased(object? sender, PointerReleasedEventArgs e)
    {
        _splitDrag = false;
        e.Pointer.Capture(null);
    }

    // ---- Manual resize of the results detail pane ----

    private bool _detailDrag;

    private void OnDetailSplitPressed(object? sender, PointerPressedEventArgs e)
    {
        _detailDrag = true;
        e.Pointer.Capture(DetailSplitter);
        e.Handled = true;
    }

    private void OnDetailSplitMove(object? sender, PointerEventArgs e)
    {
        if (!_detailDrag) return;
        var gw = ResultsSplitGrid.Bounds.Width;
        if (gw <= 0) return;
        // Detail pane spans from the cursor to the right edge; keep 240px for the results list.
        var max = Math.Max(320, gw - 240);
        var w = Math.Clamp(gw - e.GetPosition(ResultsSplitGrid).X, 320, max);
        _lastDetailWidth = w;
        ResultsSplitGrid.ColumnDefinitions[2].Width = new GridLength(w, GridUnitType.Pixel);
    }

    private void OnDetailSplitReleased(object? sender, PointerReleasedEventArgs e)
    {
        _detailDrag = false;
        e.Pointer.Capture(null);
    }

    // ---- Drag-to-reorder the run sequence ----
    // The collection is reordered ONCE on drop. During the drag we only move a translucent ghost
    // and a drop-position line, so there's no per-move container churn (that's what felt choppy).

    private RunRequestRow? _dragRow;
    private bool _dragging;
    private Point _dragStart;
    private int _dropIndex = -1;

    private void OnRowDragStart(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not RunRequestRow row) return;
        if (DataContext is not CollectionRunTabViewModel) return;
        _dragRow = row;
        _dragging = false;
        _dropIndex = -1;
        _dragStart = e.GetPosition(DragOverlay);

        GhostMethod.Text = row.IsGraphQL ? "GQL" : row.Method;
        GhostName.Text = row.Name;

        e.Pointer.Capture(c);
        e.Handled = true;
    }

    private void OnRowDragMove(object? sender, PointerEventArgs e)
    {
        if (_dragRow is null) return;
        var p = e.GetPosition(DragOverlay);
        if (!_dragging)
        {
            if (Math.Abs(p.Y - _dragStart.Y) < 4) return;   // threshold — a plain click shouldn't drag
            _dragging = true;
            DragGhost.IsVisible = true;
            DropIndicator.IsVisible = true;
        }

        // Ghost follows the cursor.
        Canvas.SetLeft(DragGhost, p.X + 12);
        Canvas.SetTop(DragGhost, p.Y - 14);

        var drop = ComputeDrop(p);
        if (drop is { } d)
        {
            _dropIndex = d.insertIndex;
            var listLeft = RunSequenceList.TranslatePoint(new Point(0, 0), DragOverlay)?.X ?? 0;
            Canvas.SetLeft(DropIndicator, listLeft + 6);
            Canvas.SetTop(DropIndicator, d.boundaryY - 1);
            DropIndicator.Width = Math.Max(0, RunSequenceList.Bounds.Width - 12);
            DropIndicator.IsVisible = true;
        }
        else
        {
            DropIndicator.IsVisible = false;
            _dropIndex = -1;
        }
    }

    private void OnRowDragEnd(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        DragGhost.IsVisible = false;
        DropIndicator.IsVisible = false;

        if (_dragging && _dragRow is not null && _dropIndex >= 0
            && DataContext is CollectionRunTabViewModel vm)
        {
            var from = vm.RequestRows.IndexOf(_dragRow);
            var to = _dropIndex;
            if (to > from) to--;                 // removal shifts everything after `from` down one
            to = Math.Clamp(to, 0, vm.RequestRows.Count - 1);
            if (from >= 0 && to != from) vm.RequestRows.Move(from, to);
        }

        _dragRow = null;
        _dragging = false;
        _dropIndex = -1;
    }

    /// <summary>Given a pointer position (in DragOverlay coords), returns the insertion index in
    /// the run sequence and the Y at which to draw the drop line. Above a row's midpoint inserts
    /// before it; below the last row appends. Null when there are no realized rows.</summary>
    private (int insertIndex, double boundaryY)? ComputeDrop(Point p)
    {
        var rows = new List<(int idx, double y0, double y1)>();
        foreach (var container in RunSequenceList.GetRealizedContainers())
        {
            var top = container.TranslatePoint(new Point(0, 0), DragOverlay);
            if (top is null) continue;
            var y0 = top.Value.Y;
            rows.Add((RunSequenceList.IndexFromContainer(container), y0, y0 + container.Bounds.Height));
        }
        if (rows.Count == 0) return null;
        rows.Sort((a, b) => a.y0.CompareTo(b.y0));

        foreach (var r in rows)
            if (p.Y < (r.y0 + r.y1) / 2)
                return (r.idx, r.y0);

        var last = rows[^1];
        return (last.idx + 1, last.y1);
    }
}
