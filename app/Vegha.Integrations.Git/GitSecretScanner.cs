using System.Text.RegularExpressions;

namespace Vegha.Integrations.Git;

/// <summary>Pre-commit check for credentials about to be committed. Scans only the lines a
/// commit would add (HEAD → index) in text files, looking for well-known token formats and
/// <c>key: value</c> assignments of secret-looking names to literal (non-<c>{{variable}}</c>)
/// values. Deliberately conservative — a false positive costs one click ("Commit anyway").</summary>
public static partial class GitSecretScanner
{
    private static readonly (string Name, Regex Rx)[] TokenPatterns =
    {
        ("GitHub token", GitHubTokenRx()),
        ("GitHub fine-grained token", GitHubPatRx()),
        ("AWS access key", AwsKeyRx()),
        ("Slack token", SlackRx()),
        ("Private key", PrivateKeyRx()),
        ("JSON Web Token", JwtRx()),
        ("Google API key", GoogleKeyRx()),
        ("Azure storage key", AzureStorageRx()),
        ("Bearer token", BearerRx()),
    };

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
    public static (string Kind, string Masked)? ScanLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 4000) return null;
        foreach (var (name, rx) in TokenPatterns)
        {
            var m = rx.Match(line);
            if (m.Success && !IsPlaceholder(m.Value)) return (name, Mask(line, m.Value));
        }
        var a = AssignmentRx().Match(line);
        if (a.Success)
        {
            var value = a.Groups["value"].Value.Trim().Trim('"', '\'', ',');
            if (value.Length >= 8 && !IsPlaceholder(value) && LooksSecret(value))
                return ($"Literal {a.Groups["key"].Value.Trim()}", Mask(line, value));
        }
        return null;
    }

    private static bool IsIgnoredPath(string path)
    {
        var p = path.Replace('\\', '/');
        return p.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("package-lock.json", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/node_modules/", StringComparison.Ordinal);
    }

    private static bool IsPlaceholder(string value) =>
        value.Contains("{{", StringComparison.Ordinal)
        || value.Contains("${", StringComparison.Ordinal)
        || value.Contains("<", StringComparison.Ordinal) && value.Contains(">", StringComparison.Ordinal)
        || value.StartsWith("secret://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("process.env", StringComparison.Ordinal)
        || value.All(c => c == '*' || c == 'x' || c == 'X' || c == '•' || c == '.');

    /// <summary>Mixed character classes and no spaces — rules out prose like "password: please change me".</summary>
    private static bool LooksSecret(string value)
    {
        if (value.Contains(' ')) return false;
        var classes = 0;
        if (value.Any(char.IsLower)) classes++;
        if (value.Any(char.IsUpper)) classes++;
        if (value.Any(char.IsDigit)) classes++;
        if (value.Any(c => !char.IsLetterOrDigit(c))) classes++;
        return classes >= 3 || (classes >= 2 && value.Length >= 20);
    }

    private static string Mask(string line, string secret)
    {
        var keep = Math.Min(4, secret.Length / 4);
        var masked = secret[..keep] + new string('•', Math.Min(12, secret.Length - keep));
        var result = line.Replace(secret, masked, StringComparison.Ordinal).Trim();
        return result.Length > 160 ? result[..160] + "…" : result;
    }

    [GeneratedRegex(@"\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,}\b")]
    private static partial Regex GitHubTokenRx();

    [GeneratedRegex(@"\bgithub_pat_[A-Za-z0-9_]{60,}\b")]
    private static partial Regex GitHubPatRx();

    [GeneratedRegex(@"\b(AKIA|ASIA)[A-Z0-9]{16}\b")]
    private static partial Regex AwsKeyRx();

    [GeneratedRegex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}\b")]
    private static partial Regex SlackRx();

    [GeneratedRegex(@"-----BEGIN (RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY")]
    private static partial Regex PrivateKeyRx();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b")]
    private static partial Regex JwtRx();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_-]{35}\b")]
    private static partial Regex GoogleKeyRx();

    [GeneratedRegex(@"AccountKey=[A-Za-z0-9+/=]{40,}")]
    private static partial Regex AzureStorageRx();

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/-]{24,}=*")]
    private static partial Regex BearerRx();

    [GeneratedRegex(@"(?i)(?<key>[A-Za-z0-9_.-]*(api[_-]?key|apikey|secret|token|password|passwd|pwd|client[_-]?secret|access[_-]?key)[A-Za-z0-9_.-]*)\s*[:=]\s*(?<value>.+)$")]
    private static partial Regex AssignmentRx();
}

public sealed record GitSecretFinding(string Path, int Line, string Kind, string MaskedLine);
