using System.Collections.ObjectModel;
using System.Text;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Integrations.Git;

namespace Vegha.App.ViewModels.Tabs;

/// <summary>Per-line role in the side-by-side diff. Drives both background tint
/// (<c>DiffLineColorizer</c>) and the gutter line numbers (<c>DiffLineNumberMargin</c>).</summary>
public enum DiffDecorationKind { Unchanged, Removed, Added, Phantom, HunkHeader }

/// <summary>
/// Backing ViewModel for the side-by-side diff workspace. Loads a single file's diff against
/// its index / HEAD counterpart, builds two AvaloniaEdit <see cref="TextDocument"/>s with
/// phantom-blank padding so corresponding lines stay row-aligned, and exposes per-document
/// decoration arrays + line-number maps so the editor margins/colorizers can paint correctly.
/// </summary>
public sealed partial class GitDiffTabViewModel : RequestTabViewModel
{
    private readonly GitService _git;
    private readonly string _repoPath;
    private readonly string _filePath;
    private readonly DiffMode _mode;

    [ObservableProperty] private string _leftTitle = "";
    [ObservableProperty] private string _rightTitle = "";
    [ObservableProperty] private bool _isBinary;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private int _additionCount;
    [ObservableProperty] private int _deletionCount;

    /// <summary>True when the tab is rendering a merge conflict (single editor with inline
    /// resolve chips), false for normal side-by-side diffs.</summary>
    public bool IsMergeMode => _mode == DiffMode.Merge;

    /// <summary>Working-tree path under the repo root — the diff workspace needs this to
    /// drive the "Save resolved file" action in merge mode.</summary>
    public string FilePath => _filePath;

    /// <summary>Repo root the diff is bound to. Used by the merge-mode save flow.</summary>
    public string RepoPath => _repoPath;

    public TextDocument LeftDocument { get; } = new();
    public TextDocument RightDocument { get; } = new();

    /// <summary>Per-document-line role. Indexed by 1-based document line number.</summary>
    public List<DiffDecorationKind> LeftDecorations { get; } = new();
    public List<DiffDecorationKind> RightDecorations { get; } = new();

    /// <summary>Maps document line → original source line in the corresponding file (or -1
    /// for phantom/hunk-header rows). Used by the line-number gutter so phantom rows are blank
    /// and real rows show the source file's line number.</summary>
    public List<int> LeftLineMap { get; } = new();
    public List<int> RightLineMap { get; } = new();

    /// <summary>Document-line offsets of each hunk's first row — drives Prev/Next change navigation.</summary>
    public ObservableCollection<int> HunkAnchors { get; } = new();

    /// <summary>Per-hunk staging stubs — populated for future hunk-level staging but not yet
    /// wired to commands. Keeps the data model stable.</summary>
    public ObservableCollection<GitDiffHunkVm> Hunks { get; } = new();

    public override object Workspace => this;

    public GitDiffTabViewModel(GitService git, string repoPath, string filePath, DiffMode mode, string? collectionPath = null)
    {
        _git = git;
        _repoPath = repoPath;
        _filePath = filePath;
        _mode = mode;

        Id = $"diff:{mode}:{filePath}";
        Name = Path.GetFileName(filePath);
        Method = mode switch
        {
            DiffMode.WorkingTreeVsHead => "DIFF",
            DiffMode.IndexVsHead => "STAGED",
            DiffMode.WorkingTreeVsIndex => "DIFF",
            DiffMode.Merge => "MERGE",
            _ => "DIFF",
        };
        Kind = Vegha.Core.Domain.RequestKind.Http; // tab kind ≠ request kind; reuse Http for default icon
        SourcePath = Path.Combine(repoPath, filePath);
        CollectionPath = collectionPath;

        (LeftTitle, RightTitle) = mode switch
        {
            DiffMode.WorkingTreeVsHead => ("HEAD", "Working Tree"),
            DiffMode.IndexVsHead => ("HEAD", "Index"),
            DiffMode.WorkingTreeVsIndex => ("Index", "Working Tree"),
            DiffMode.Merge => ("Conflict", "Working Tree"),
            _ => ("Base", "Compare"),
        };
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            if (_mode == DiffMode.Merge)
            {
                await LoadMergeAsync().ConfigureAwait(true);
                return;
            }

            var diffs = await Task.Run(() => _git.FileDiffs(_repoPath, BaseSource(), HeadSource(), _filePath))
                .ConfigureAwait(true);
            var diff = diffs.FirstOrDefault();
            if (diff is null)
            {
                LeftDocument.Text = string.Empty;
                RightDocument.Text = string.Empty;
                return;
            }
            AdditionCount = diff.AdditionCount;
            DeletionCount = diff.DeletionCount;
            IsBinary = diff.IsBinary;

            if (diff.IsBinary)
            {
                var msg = $"Binary file. {diff.AdditionCount + diff.DeletionCount} bytes changed.";
                LeftDocument.Text = msg;
                RightDocument.Text = msg;
                LeftDecorations.Clear();
                RightDecorations.Clear();
                LeftLineMap.Clear();
                RightLineMap.Clear();
                LeftDecorations.Add(DiffDecorationKind.Unchanged);
                RightDecorations.Add(DiffDecorationKind.Unchanged);
                LeftLineMap.Add(-1);
                RightLineMap.Add(-1);
                return;
            }

            BuildSideBySide(diff);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private DiffSource BaseSource() => _mode switch
    {
        DiffMode.WorkingTreeVsHead => DiffSource.Head,
        DiffMode.IndexVsHead => DiffSource.Head,
        DiffMode.WorkingTreeVsIndex => DiffSource.Index,
        DiffMode.Merge => DiffSource.Head,
        _ => DiffSource.Head,
    };

    private DiffSource HeadSource() => _mode switch
    {
        DiffMode.WorkingTreeVsHead => DiffSource.WorkingTree,
        DiffMode.IndexVsHead => DiffSource.Index,
        DiffMode.WorkingTreeVsIndex => DiffSource.WorkingTree,
        DiffMode.Merge => DiffSource.WorkingTree,
        _ => DiffSource.WorkingTree,
    };

    /// <summary>Walks the hunks, emitting context / removed / added lines into the two documents
    /// while inserting phantom blanks so each row aligns visually. Maintains per-side line maps
    /// and decoration arrays in lockstep with the documents.</summary>
    private void BuildSideBySide(GitFileDiff diff)
    {
        var leftSb = new StringBuilder();
        var rightSb = new StringBuilder();
        LeftDecorations.Clear();
        RightDecorations.Clear();
        LeftLineMap.Clear();
        RightLineMap.Clear();
        HunkAnchors.Clear();
        Hunks.Clear();

        bool first = true;
        foreach (var hunk in diff.Hunks)
        {
            // Insert a blank separator row between hunks so the boundary is visible.
            if (!first)
            {
                Emit(leftSb, rightSb, "", "", DiffDecorationKind.HunkHeader, DiffDecorationKind.HunkHeader, -1, -1);
            }
            first = false;
            HunkAnchors.Add(LeftDecorations.Count);

            // Walk the lines and pair up Removed/Added blocks. Inside one hunk, a run of
            // removed lines is followed by a run of added lines; we pad whichever side is
            // shorter with phantom rows so the next context row lines up.
            var pendingRemoved = new List<string>();
            var pendingAdded = new List<string>();
            int oldLine = hunk.OldStartLine;
            int newLine = hunk.NewStartLine;

            void FlushBlock()
            {
                int n = Math.Max(pendingRemoved.Count, pendingAdded.Count);
                for (int i = 0; i < n; i++)
                {
                    var leftText = i < pendingRemoved.Count ? pendingRemoved[i] : "";
                    var rightText = i < pendingAdded.Count ? pendingAdded[i] : "";
                    var leftKind = i < pendingRemoved.Count ? DiffDecorationKind.Removed : DiffDecorationKind.Phantom;
                    var rightKind = i < pendingAdded.Count ? DiffDecorationKind.Added : DiffDecorationKind.Phantom;
                    int leftLn = i < pendingRemoved.Count ? oldLine++ : -1;
                    int rightLn = i < pendingAdded.Count ? newLine++ : -1;
                    Emit(leftSb, rightSb, leftText, rightText, leftKind, rightKind, leftLn, rightLn);
                }
                pendingRemoved.Clear();
                pendingAdded.Clear();
            }

            foreach (var line in hunk.Lines)
            {
                switch (line.Kind)
                {
                    case DiffLineKind.Removed:
                        pendingRemoved.Add(line.Text);
                        break;
                    case DiffLineKind.Added:
                        pendingAdded.Add(line.Text);
                        break;
                    case DiffLineKind.Context:
                        FlushBlock();
                        Emit(leftSb, rightSb, line.Text, line.Text,
                             DiffDecorationKind.Unchanged, DiffDecorationKind.Unchanged,
                             oldLine++, newLine++);
                        break;
                }
            }
            FlushBlock();

            Hunks.Add(new GitDiffHunkVm(hunk.OldStartLine, hunk.NewStartLine));
        }

        // AvaloniaEdit's TextDocument needs the trailing newlines trimmed or it adds a phantom
        // last row to each pane. Strip one trailing \n if the buffer ends with one.
        LeftDocument.Text = TrimTrailingNewline(leftSb.ToString());
        RightDocument.Text = TrimTrailingNewline(rightSb.ToString());
    }

    private void Emit(StringBuilder left, StringBuilder right, string leftText, string rightText,
                      DiffDecorationKind leftKind, DiffDecorationKind rightKind, int leftLn, int rightLn)
    {
        left.Append(leftText).Append('\n');
        right.Append(rightText).Append('\n');
        LeftDecorations.Add(leftKind);
        RightDecorations.Add(rightKind);
        LeftLineMap.Add(leftLn);
        RightLineMap.Add(rightLn);
    }

    private static string TrimTrailingNewline(string s) =>
        s.EndsWith('\n') ? s[..^1] : s;

    // -----------------------------------------------------------------------
    // Merge mode — loads the working-tree file (with conflict markers in place) into a
    // single document. The view-control listens for ConflictsParsed and renders inline
    // accept buttons over each block; saving writes the document back to disk and stages it.
    // -----------------------------------------------------------------------

    /// <summary>Fires once after <see cref="LoadAsync"/> finishes for a merge tab. The view
    /// (re-)builds inline conflict chips when this fires.</summary>
    public event EventHandler? ConflictsLoaded;

    /// <summary>Fires when the user picks Stage in the merge banner. The host writes the
    /// document to disk and stages it via <see cref="GitService"/>.</summary>
    public event EventHandler? StageRequested;

    private async Task LoadMergeAsync()
    {
        var abs = Path.Combine(_repoPath, _filePath);
        if (!File.Exists(abs))
        {
            ErrorMessage = "Conflict file is not present on disk.";
            return;
        }
        var content = await File.ReadAllTextAsync(abs).ConfigureAwait(true);
        RightDocument.Text = content;
        LeftDocument.Text = string.Empty;
        IsBinary = false;
        ConflictsLoaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes the current document text back to the working-tree file and stages
    /// it — completes the merge once all conflict blocks are resolved.</summary>
    [RelayCommand]
    public async Task SaveAndStageAsync()
    {
        try
        {
            var abs = Path.Combine(_repoPath, _filePath);
            await File.WriteAllTextAsync(abs, RightDocument.Text).ConfigureAwait(true);
            await Task.Run(() => _git.Stage(_repoPath, _filePath)).ConfigureAwait(true);
            StageRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { ErrorMessage = $"Stage failed: {ex.Message}"; }
    }
}

public enum DiffMode
{
    /// <summary>HEAD → working tree. The default for unstaged changes.</summary>
    WorkingTreeVsHead,
    /// <summary>HEAD → index. The default for staged changes.</summary>
    IndexVsHead,
    /// <summary>Index → working tree. The "pure unstaged" view.</summary>
    WorkingTreeVsIndex,
    /// <summary>Single-pane view of the merge-conflict working file.</summary>
    Merge,
}

/// <summary>Placeholder per-hunk model. Phase A.6 plan reserves <see cref="IsStaged"/> +
/// stage / unstage commands here for future hunk-level staging without reshaping the model.</summary>
public sealed partial class GitDiffHunkVm : ObservableObject
{
    public int OldStartLine { get; }
    public int NewStartLine { get; }

    [ObservableProperty] private bool _isStaged;

    public GitDiffHunkVm(int oldStart, int newStart)
    {
        OldStartLine = oldStart;
        NewStartLine = newStart;
    }

    // Stubs — not wired yet. Plan reserves the commands so the data model is stable.
    [RelayCommand]
    private void StageHunk() { /* TODO: hunk-level staging */ }

    [RelayCommand]
    private void UnstageHunk() { /* TODO: hunk-level unstaging */ }
}
