using System.Text.RegularExpressions;

namespace Vegha.Core.Domain.Security;

/// <summary>
/// Recognizes credentials in a line of text: well-known token formats and <c>key: value</c>
/// assignments of secret-looking names to literal (non-<c>{{variable}}</c>) values. Shared by
/// the desktop app's pre-commit check (<c>GitSecretScanner</c>) and the VS Code extension's
/// diagnostics, so both flag the same things. Deliberately conservative — a false positive
/// costs one click.
/// </summary>
public static partial class SecretPatterns
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

    /// <summary>Returns the first secret-looking match in <paramref name="line"/> — its kind,
    /// the line with the secret masked, and the raw secret's position in the line.</summary>
    public static SecretMatch? ScanLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 4000) return null;
        foreach (var (name, rx) in TokenPatterns)
        {
            var m = rx.Match(line);
            if (m.Success && !IsPlaceholder(m.Value)) return new SecretMatch(name, Mask(line, m.Value), m.Index, m.Length);
        }
        var a = AssignmentRx().Match(line);
        if (a.Success)
        {
            var group = a.Groups["value"];
            var raw = group.Value;
            var value = raw.Trim().Trim('"', '\'', ',');
            if (value.Length >= 8 && !IsPlaceholder(value) && LooksSecret(value))
            {
                var start = group.Index + raw.IndexOf(value, StringComparison.Ordinal);
                return new SecretMatch($"Literal {a.Groups["key"].Value.Trim()}", Mask(line, value), start, value.Length);
            }
        }
        return null;
    }

    private static bool IsPlaceholder(string value) =>
        value.Contains("{{", StringComparison.Ordinal)
        || value.Contains("${", StringComparison.Ordinal)
        || value.Contains('<', StringComparison.Ordinal) && value.Contains('>', StringComparison.Ordinal)
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

/// <summary>A secret found in a line: what it looks like, the masked line, and where it is.</summary>
public sealed record SecretMatch(string Kind, string MaskedLine, int Start, int Length);
