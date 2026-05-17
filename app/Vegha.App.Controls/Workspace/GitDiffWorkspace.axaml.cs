using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using Vegha.App.ViewModels.Tabs;

namespace Vegha.App.Controls.Workspace;

public partial class GitDiffWorkspace : UserControl
{
    private bool _suppressScrollSync;
    private GitDiffTabViewModel? _vm;
    private readonly ObservableCollection<ConflictBlock> _conflictBlocks = new();

    public GitDiffWorkspace()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        ConflictList.ItemsSource = _conflictBlocks;
    }

    private async void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.ConflictsLoaded -= OnConflictsLoaded;
        _vm = DataContext as GitDiffTabViewModel;
        if (_vm is null) return;
        _vm.ConflictsLoaded += OnConflictsLoaded;

        try { await _vm.LoadAsync(); } catch { /* surfaced via vm.ErrorMessage */ }
        WireEditors();
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e) => WireEditors();

    private void OnConflictsLoaded(object? sender, EventArgs e) => RebuildConflictList();

    /// <summary>Walks the working-tree document, surfaces each conflict block as a row in
    /// the <c>ConflictList</c> ItemsControl. Re-run after each Accept so the list shrinks.</summary>
    private void RebuildConflictList()
    {
        _conflictBlocks.Clear();
        if (_vm is null) return;
        foreach (var b in ConflictParser.Parse(_vm.RightDocument))
            _conflictBlocks.Add(b);
    }

    private void OnAcceptCurrent_Click(object? s, RoutedEventArgs e) =>
        ResolveBlock(s, ConflictResolution.Ours);

    private void OnAcceptIncoming_Click(object? s, RoutedEventArgs e) =>
        ResolveBlock(s, ConflictResolution.Theirs);

    private void OnAcceptBoth_Click(object? s, RoutedEventArgs e) =>
        ResolveBlock(s, ConflictResolution.Both);

    private void ResolveBlock(object? s, ConflictResolution choice)
    {
        if (_vm is null) return;
        if (s is not Button b || b.Tag is not ConflictBlock block) return;
        ConflictParser.Resolve(_vm.RightDocument, block, choice);
        RebuildConflictList();
    }

    private void WireEditors()
    {
        if (_vm is null) return;

        var removedBg = ResolveBrush("StatusErrBrush", Color.FromArgb(60, 220, 38, 38));
        var addedBg = ResolveBrush("StatusOkBrush", Color.FromArgb(60, 22, 163, 74));
        var phantomBg = ResolveBrush("Bg3Brush", Color.FromArgb(120, 32, 36, 42));

        if (LeftEditor is not null && RightEditor is not null)
        {
            LeftEditor.TextArea.TextView.LineTransformers.Clear();
            RightEditor.TextArea.TextView.LineTransformers.Clear();
            LeftEditor.TextArea.TextView.LineTransformers.Add(
                new DiffLineColorizer(n => SafeKind(_vm.LeftDecorations, n),
                    AddAlpha(removedBg, 60), AddAlpha(addedBg, 60), AddAlpha(phantomBg, 80)));
            RightEditor.TextArea.TextView.LineTransformers.Add(
                new DiffLineColorizer(n => SafeKind(_vm.RightDecorations, n),
                    AddAlpha(removedBg, 60), AddAlpha(addedBg, 60), AddAlpha(phantomBg, 80)));

            LeftEditor.TextArea.TextView.ScrollOffsetChanged -= OnLeftScrolled;
            RightEditor.TextArea.TextView.ScrollOffsetChanged -= OnRightScrolled;
            LeftEditor.TextArea.TextView.ScrollOffsetChanged += OnLeftScrolled;
            RightEditor.TextArea.TextView.ScrollOffsetChanged += OnRightScrolled;
        }
    }

    private void OnLeftScrolled(object? s, EventArgs e)
    {
        if (_suppressScrollSync || RightEditor is null) return;
        var offset = LeftEditor.TextArea.TextView.ScrollOffset;
        _suppressScrollSync = true;
        try { RightEditor.ScrollToVerticalOffset(offset.Y); }
        finally { _suppressScrollSync = false; }
    }

    private void OnRightScrolled(object? s, EventArgs e)
    {
        if (_suppressScrollSync || LeftEditor is null) return;
        var offset = RightEditor.TextArea.TextView.ScrollOffset;
        _suppressScrollSync = true;
        try { LeftEditor.ScrollToVerticalOffset(offset.Y); }
        finally { _suppressScrollSync = false; }
    }

    private static DiffDecorationKind SafeKind(System.Collections.Generic.List<DiffDecorationKind> list, int oneBasedLine)
    {
        var idx = oneBasedLine - 1;
        if (idx < 0 || idx >= list.Count) return DiffDecorationKind.Unchanged;
        return list[idx];
    }

    private IBrush ResolveBrush(string key, Color fallback)
    {
        var value = this.FindResource(key);
        if (value is ISolidColorBrush b) return new SolidColorBrush(b.Color);
        value = Application.Current?.FindResource(key);
        if (value is ISolidColorBrush b2) return new SolidColorBrush(b2.Color);
        return new SolidColorBrush(fallback);
    }

    private static IBrush AddAlpha(IBrush brush, byte alpha)
    {
        if (brush is ISolidColorBrush scb)
            return new SolidColorBrush(Color.FromArgb(alpha, scb.Color.R, scb.Color.G, scb.Color.B));
        return brush;
    }
}
