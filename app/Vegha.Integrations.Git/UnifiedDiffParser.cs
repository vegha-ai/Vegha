using System.Globalization;

namespace Vegha.Integrations.Git;

/// <summary>Parses libgit2sharp's per-file unified diff text into structured hunks. Inputs
/// look like:
/// <code>
/// diff --git a/foo.txt b/foo.txt
/// index abc..def 100644
/// --- a/foo.txt
/// +++ b/foo.txt
/// @@ -1,3 +1,4 @@
///  context
/// -removed
/// +added
/// +added2
///  context2
/// </code>
/// Headers before the first <c>@@</c> are discarded.</summary>
internal static class UnifiedDiffParser
{
    public static IReadOnlyList<GitDiffHunk> ParseHunks(string patch)
    {
        if (string.IsNullOrEmpty(patch)) return Array.Empty<GitDiffHunk>();
        var hunks = new List<GitDiffHunk>();
        var lines = patch.Split('\n');

        int i = 0;
        // Skip everything up to the first hunk header.
        while (i < lines.Length && !lines[i].StartsWith("@@", StringComparison.Ordinal)) i++;

        while (i < lines.Length)
        {
            var header = lines[i];
            if (!TryParseHunkHeader(header, out var oldStart, out var newStart))
            {
                i++;
                continue;
            }
            i++;
            var hunkLines = new List<GitDiffLine>();
            while (i < lines.Length && !lines[i].StartsWith("@@", StringComparison.Ordinal))
            {
                var line = lines[i];
                // Strip the trailing "\r" left over from CRLF splits (we split on '\n' only).
                if (line.EndsWith('\r')) line = line[..^1];

                if (line.Length == 0)
                {
                    // Blank line at the end of the patch (after the final newline) — drop it.
                    i++;
                    continue;
                }

                if (line.StartsWith(@"\ No newline at end of file", StringComparison.Ordinal))
                {
                    i++;
                    continue;
                }

                var marker = line[0];
                var text = line.Length > 1 ? line[1..] : "";
                var kind = marker switch
                {
                    '+' => DiffLineKind.Added,
                    '-' => DiffLineKind.Removed,
                    _   => DiffLineKind.Context,
                };
                hunkLines.Add(new GitDiffLine(kind, text));
                i++;
            }
            hunks.Add(new GitDiffHunk(oldStart, newStart, hunkLines));
        }
        return hunks;
    }

    /// <summary>Parses a <c>@@ -oldStart[,oldLen] +newStart[,newLen] @@</c> header.</summary>
    private static bool TryParseHunkHeader(string header, out int oldStart, out int newStart)
    {
        oldStart = newStart = 0;
        // header: "@@ -a,b +c,d @@ optional fn context"
        var minusIdx = header.IndexOf('-');
        var plusIdx = header.IndexOf('+');
        if (minusIdx < 0 || plusIdx < 0 || plusIdx < minusIdx) return false;
        var oldSegmentEnd = header.IndexOf(' ', minusIdx);
        if (oldSegmentEnd < 0) return false;
        var plusEnd = header.IndexOf(' ', plusIdx);
        if (plusEnd < 0) return false;

        var oldSeg = header[(minusIdx + 1)..oldSegmentEnd];
        var newSeg = header[(plusIdx + 1)..plusEnd];

        return TryParseLineNum(oldSeg, out oldStart) & TryParseLineNum(newSeg, out newStart);
    }

    private static bool TryParseLineNum(ReadOnlySpan<char> seg, out int n)
    {
        var comma = seg.IndexOf(',');
        var head = comma < 0 ? seg : seg[..comma];
        return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
    }
}
