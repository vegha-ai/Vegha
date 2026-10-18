using System.Text;
using System.Xml;

namespace Vegha.Core.Scripting.Testing;

/// <summary>
/// Maps a caret position in XML text (the pretty-printed response body) to an XPath for the
/// element under it — the XML counterpart of <see cref="JsonPathLocator"/>. Steps use
/// <c>*[local-name()='x']</c> for namespaced elements (SOAP envelopes) so the path evaluates
/// without a namespace manager, plain names otherwise, and a <c>[n]</c> position when an
/// element has same-named siblings.
/// </summary>
public static class XmlPathLocator
{
    public sealed record Location(string XPath, string SuggestedName);

    private sealed class Frame
    {
        public required string Step;
        public required string LocalName;
        public readonly Dictionary<string, int> ChildCounts = new(StringComparer.Ordinal);
    }

    public static Location? Locate(string xml, int charOffset)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        charOffset = Math.Clamp(charOffset, 0, xml.Length);
        var lineStarts = LineStarts(xml);

        // Sibling positions need the full sibling set, so count names per parent first.
        var siblingTotals = CountSiblings(xml);
        if (siblingTotals is null) return null;

        var stack = new List<Frame>();
        Location? best = null;
        var elementOrdinal = 0;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
            });
            var info = (IXmlLineInfo)reader;
            while (reader.Read())
            {
                var pos = ToOffset(lineStarts, info.LineNumber, info.LinePosition);
                if (pos > charOffset) break;
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        var parent = stack.Count > 0 ? stack[^1] : null;
                        var key = reader.NamespaceURI + "|" + reader.LocalName;
                        var index = 1;
                        if (parent is not null)
                        {
                            index = parent.ChildCounts.GetValueOrDefault(key) + 1;
                            parent.ChildCounts[key] = index;
                        }
                        var total = siblingTotals.GetValueOrDefault((elementOrdinal, key), 1);
                        elementOrdinal++;
                        var step = string.IsNullOrEmpty(reader.NamespaceURI)
                            ? reader.LocalName
                            : $"*[local-name()='{reader.LocalName}']";
                        if (total > 1) step += $"[{index}]";
                        var frame = new Frame { Step = step, LocalName = reader.LocalName };
                        stack.Add(frame);
                        best = Build(stack);
                        if (reader.IsEmptyElement) stack.RemoveAt(stack.Count - 1);
                        break;
                    }
                    case XmlNodeType.EndElement:
                        if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                        // Caret after this close tag but before the next node: the parent is
                        // the closest container; keep the last element pointed at instead.
                        break;
                }
            }
        }
        catch (XmlException)
        {
            // Malformed / truncated: keep what was resolved before the error.
        }
        return best;
    }

    private static Location Build(List<Frame> stack)
    {
        var sb = new StringBuilder();
        foreach (var f in stack) sb.Append('/').Append(f.Step);
        return new Location(sb.ToString(), JsonPathLocator.ToVariableName(stack[^1].LocalName));
    }

    /// <summary>For each element (by document order) the number of same-named siblings it has,
    /// keyed by (ordinal of the element, namespace|name). Null when the XML doesn't parse.</summary>
    private static Dictionary<(int, string), int>? CountSiblings(string xml)
    {
        var result = new Dictionary<(int, string), int>();
        var parents = new List<(List<(int Ordinal, string Key)> Children, bool Root)>
        {
            (new List<(int, string)>(), true),
        };
        var ordinal = 0;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
            });
            void Close(List<(int Ordinal, string Key)> children)
            {
                foreach (var g in children.GroupBy(c => c.Key))
                    foreach (var c in g) result[(c.Ordinal, c.Key)] = g.Count();
            }
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    var key = reader.NamespaceURI + "|" + reader.LocalName;
                    parents[^1].Children.Add((ordinal++, key));
                    if (!reader.IsEmptyElement) parents.Add((new List<(int, string)>(), false));
                }
                else if (reader.NodeType == XmlNodeType.EndElement && parents.Count > 1)
                {
                    Close(parents[^1].Children);
                    parents.RemoveAt(parents.Count - 1);
                }
            }
            foreach (var p in parents) Close(p.Children);
        }
        catch (XmlException)
        {
            if (ordinal == 0) return null;
        }
        return result;
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        return starts;
    }

    /// <summary>1-based line / column (XmlReader points at the name, after '<') → char offset
    /// of the '<'.</summary>
    private static int ToOffset(List<int> lineStarts, int line, int column)
    {
        if (line <= 0 || line > lineStarts.Count) return int.MaxValue;
        return lineStarts[line - 1] + Math.Max(0, column - 2);
    }
}
