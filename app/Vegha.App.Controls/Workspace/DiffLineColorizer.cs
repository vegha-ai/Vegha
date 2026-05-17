using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Vegha.App.ViewModels.Tabs;

namespace Vegha.App.Controls.Workspace;

/// <summary>Paints per-line background tints (red for removed, green for added, dim grey for
/// phantom rows). Reads decoration roles from the bound <see cref="GitDiffTabViewModel"/>'s
/// per-side decoration array, indexed by 1-based document line number.</summary>
public sealed class DiffLineColorizer : DocumentColorizingTransformer
{
    private readonly Func<int, DiffDecorationKind> _kindAt;
    private readonly IBrush _removedBg;
    private readonly IBrush _addedBg;
    private readonly IBrush _phantomBg;

    public DiffLineColorizer(Func<int, DiffDecorationKind> kindAt, IBrush removedBg, IBrush addedBg, IBrush phantomBg)
    {
        _kindAt = kindAt;
        _removedBg = removedBg;
        _addedBg = addedBg;
        _phantomBg = phantomBg;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var kind = _kindAt(line.LineNumber);
        IBrush? bg = kind switch
        {
            DiffDecorationKind.Removed => _removedBg,
            DiffDecorationKind.Added => _addedBg,
            DiffDecorationKind.Phantom => _phantomBg,
            DiffDecorationKind.HunkHeader => _phantomBg,
            _ => null,
        };
        if (bg is null) return;

        ChangeLinePart(line.Offset, line.EndOffset, element =>
        {
            element.BackgroundBrush = bg;
        });
    }
}
