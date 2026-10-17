namespace Vegha.App.ViewModels;

/// <summary>Compact, human-friendly folder paths for space-constrained UI (collection picker,
/// Source Control repo switcher).</summary>
public static class PathDisplay
{
    /// <summary>Paths inside <paramref name="workspaceFolder"/> become "&lt;workspace&gt;\rest";
    /// paths under the user profile start with "~"; anything longer than
    /// <paramref name="maxChars"/> is trimmed in the middle, keeping the root and as many
    /// trailing folders as fit — the tail is what tells similar folders apart
    /// ("C:\…\scratchpad\ado\API_Collection").</summary>
    public static string Compact(string? path, string? workspaceFolder = null, int maxChars = 44)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var p = path.TrimEnd('\\', '/');

        var ws = workspaceFolder?.TrimEnd('\\', '/');
        if (!string.IsNullOrEmpty(ws) && p.StartsWith(ws, StringComparison.OrdinalIgnoreCase)
            && p.Length > ws.Length && p[ws.Length] is '\\' or '/')
            p = Path.GetFileName(ws) + p[ws.Length..];
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
            if (!string.IsNullOrEmpty(home) && p.StartsWith(home, StringComparison.OrdinalIgnoreCase)
                && (p.Length == home.Length || p[home.Length] is '\\' or '/'))
                p = "~" + p[home.Length..];
        }
        if (p.Length <= maxChars) return p;

        var sep = p.Contains('\\') ? '\\' : '/';
        var segs = p.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length < 3) return "…" + p[^(maxChars - 1)..];
        var head = p.StartsWith(sep) ? sep + segs[0] : segs[0];
        var tail = segs[^1];
        for (var i = segs.Length - 2; i >= 1; i--)
        {
            var candidate = segs[i] + sep + tail;
            if (head.Length + 2 + candidate.Length > maxChars) break;
            tail = candidate;
        }
        var result = $"{head}{sep}…{sep}{tail}";
        // A single very long last folder: fall back to end-trimming it.
        return result.Length <= maxChars + 8 ? result : "…" + p[^(maxChars - 1)..];
    }
}
