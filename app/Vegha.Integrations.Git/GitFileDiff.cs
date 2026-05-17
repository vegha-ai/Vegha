namespace Vegha.Integrations.Git;

/// <summary>Per-file diff with hunks, used by the side-by-side diff tab to drive
/// phantom-line alignment, line decorations, and gutter line numbers without
/// re-parsing libgit2sharp's text patch output.</summary>
public sealed record GitFileDiff(
    string Path,
    string? OldPath,
    GitChangeKind Kind,
    bool IsBinary,
    int AdditionCount,
    int DeletionCount,
    IReadOnlyList<GitDiffHunk> Hunks)
{
    public bool IsRename => OldPath is not null && !string.Equals(OldPath, Path, StringComparison.Ordinal);
}

/// <summary>One contiguous hunk inside <see cref="GitFileDiff"/>. <see cref="OldStartLine"/>
/// and <see cref="NewStartLine"/> are 1-based source-file line numbers (matching the
/// <c>@@ -a,b +c,d @@</c> header in unified diff output).</summary>
public sealed record GitDiffHunk(
    int OldStartLine,
    int NewStartLine,
    IReadOnlyList<GitDiffLine> Lines);

public sealed record GitDiffLine(DiffLineKind Kind, string Text);

public enum DiffLineKind
{
    Context = 0,
    Removed,
    Added,
}
