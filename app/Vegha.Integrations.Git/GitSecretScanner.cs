using Vegha.Core.Domain.Security;

namespace Vegha.Integrations.Git;

/// <summary>Pre-commit check for credentials about to be committed. Scans only the lines a
/// commit would add (HEAD → index) in text files. What counts as a secret lives in
/// <see cref="SecretPatterns"/>, shared with the VS Code extension's diagnostics.</summary>
public static class GitSecretScanner
{
    public static IReadOnlyList<GitSecretFinding> ScanStaged(GitService git, string repoRoot, int maxFindings = 50)
    {
        var findings = new List<GitSecretFinding>();
        IReadOnlyList<GitFileDiff> diffs;
        try { diffs = git.FileDiffs(repoRoot, DiffSource.Head, DiffSource.Index); }
        catch { return findings; }

        foreach (var file in diffs)
        {
            if (file.IsBinary || file.Kind == GitChangeKind.Deleted) continue;
            if (IsIgnoredPath(file.Path)) continue;
            foreach (var hunk in file.Hunks)
            {
                var newLine = hunk.NewStartLine;
                foreach (var row in hunk.Lines)
                {
                    if (row.Kind == DiffLineKind.Removed) continue;
                    var lineNo = newLine++;
                    if (row.Kind != DiffLineKind.Added) continue;
                    var hit = ScanLine(row.Text);
                    if (hit is null) continue;
                    findings.Add(new GitSecretFinding(file.Path, lineNo, hit.Value.Kind, hit.Value.Masked));
                    if (findings.Count >= maxFindings) return findings;
                }
            }
        }
        return findings;
    }

    /// <summary>Returns the first secret-looking match in <paramref name="line"/>, masked.</summary>
    public static (string Kind, string Masked)? ScanLine(string line) =>
        SecretPatterns.ScanLine(line) is { } m ? (m.Kind, m.MaskedLine) : null;

    private static bool IsIgnoredPath(string path)
    {
        var p = path.Replace('\\', '/');
        return p.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("package-lock.json", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/node_modules/", StringComparison.Ordinal);
    }
}

public sealed record GitSecretFinding(string Path, int Line, string Kind, string MaskedLine);
