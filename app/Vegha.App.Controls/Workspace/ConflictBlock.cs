using AvaloniaEdit.Document;

namespace Vegha.App.Controls.Workspace;

/// <summary>One conflict region delimited by <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>, <c>=======</c>,
/// and <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> markers. Offsets point into the document at which
/// the conflict was parsed.</summary>
public sealed record ConflictBlock(
    int StartLine,
    int SeparatorLine,
    int EndLine,
    string OursLabel,
    string TheirsLabel,
    string OursText,
    string TheirsText);

/// <summary>Parses a <see cref="TextDocument"/> for git conflict markers. Returns blocks in
/// document order; an empty list means the file is clean. Nested markers (rare) are not
/// handled — the parser treats the outermost block only.</summary>
internal static class ConflictParser
{
    public static IReadOnlyList<ConflictBlock> Parse(TextDocument doc)
    {
        var blocks = new List<ConflictBlock>();
        int n = doc.LineCount;
        int i = 1;
        while (i <= n)
        {
            var lineText = doc.GetText(doc.GetLineByNumber(i));
            if (lineText.StartsWith("<<<<<<<", StringComparison.Ordinal))
            {
                var oursLabel = lineText.Length > 8 ? lineText[8..].Trim() : "Current Change";
                int startLine = i;
                int separator = -1;
                int end = -1;
                string theirsLabel = "Incoming Change";

                for (int j = i + 1; j <= n; j++)
                {
                    var jt = doc.GetText(doc.GetLineByNumber(j));
                    if (separator < 0 && jt.StartsWith("=======", StringComparison.Ordinal))
                    {
                        separator = j;
                    }
                    else if (jt.StartsWith(">>>>>>>", StringComparison.Ordinal))
                    {
                        end = j;
                        theirsLabel = jt.Length > 8 ? jt[8..].Trim() : "Incoming Change";
                        break;
                    }
                }
                if (separator > 0 && end > 0)
                {
                    var oursText = ConcatLines(doc, startLine + 1, separator - 1);
                    var theirsText = ConcatLines(doc, separator + 1, end - 1);
                    blocks.Add(new ConflictBlock(startLine, separator, end, oursLabel, theirsLabel, oursText, theirsText));
                    i = end + 1;
                    continue;
                }
            }
            i++;
        }
        return blocks;
    }

    private static string ConcatLines(TextDocument doc, int fromLine, int toLine)
    {
        if (toLine < fromLine) return string.Empty;
        var sb = new System.Text.StringBuilder();
        for (int k = fromLine; k <= toLine; k++)
        {
            if (k > fromLine) sb.Append('\n');
            sb.Append(doc.GetText(doc.GetLineByNumber(k)));
        }
        return sb.ToString();
    }

    /// <summary>Rewrites a conflict block in the document, replacing the entire range
    /// (including markers) with the chosen text. Returns the new document length delta.</summary>
    public static void Resolve(TextDocument doc, ConflictBlock block, ConflictResolution choice)
    {
        var startOffset = doc.GetLineByNumber(block.StartLine).Offset;
        var endLine = doc.GetLineByNumber(block.EndLine);
        var endOffset = endLine.EndOffset;
        // Include the trailing newline so the resolved block doesn't leave a phantom line.
        if (endLine.DelimiterLength > 0) endOffset += endLine.DelimiterLength;

        var replacement = choice switch
        {
            ConflictResolution.Ours => block.OursText.Length > 0 ? block.OursText + "\n" : string.Empty,
            ConflictResolution.Theirs => block.TheirsText.Length > 0 ? block.TheirsText + "\n" : string.Empty,
            ConflictResolution.Both => CombineBoth(block),
            _ => string.Empty,
        };
        doc.Replace(startOffset, endOffset - startOffset, replacement);
    }

    private static string CombineBoth(ConflictBlock block)
    {
        var parts = new List<string>();
        if (block.OursText.Length > 0) parts.Add(block.OursText);
        if (block.TheirsText.Length > 0) parts.Add(block.TheirsText);
        if (parts.Count == 0) return string.Empty;
        return string.Join('\n', parts) + "\n";
    }
}

public enum ConflictResolution { Ours, Theirs, Both }
