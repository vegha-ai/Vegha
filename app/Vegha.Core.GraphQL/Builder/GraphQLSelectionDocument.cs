using System.Text;
using GraphQLParser;
using GraphQLParser.AST;
using GraphQLParser.Exceptions;

namespace Vegha.Core.GraphQL.Builder;

/// <summary>One selected field in the builder tree: name, argument literals (verbatim
/// source text, so <c>{{var}}</c> and <c>$var</c> survive), and child selections.</summary>
public sealed class SelectionNode
{
    public required string Name { get; init; }
    public List<KeyValuePair<string, string>> Args { get; } = new();
    public List<SelectionNode> Children { get; } = new();

    /// <summary>Render <c>{ }</c> even with no children — set for composite-typed fields the
    /// user just checked, so the text shows where selections are still needed (Postman does
    /// the same; the resulting squiggle is the prompt to pick fields).</summary>
    public bool ForceSelectionSet { get; set; }
}

/// <summary>One operation reconstructed from (or rendered to) the query document.</summary>
public sealed class SelectionOperation
{
    public GraphQLOperationKind Kind { get; init; } = GraphQLOperationKind.Query;
    public string? Name { get; init; }
    public List<SelectionNode> Selections { get; } = new();
}

/// <summary>Result of parsing query text for the builder. <see cref="IsBuilderCompatible"/>
/// is false when the document uses constructs the checkbox tree can't represent losslessly
/// (fragments, inline fragments, directives, aliases) — the builder then goes read-only so a
/// regenerate can't silently destroy them.</summary>
public sealed record SelectionParseResult(
    IReadOnlyList<SelectionOperation> Operations,
    bool IsBuilderCompatible,
    string? IncompatibleReason)
{
    public static readonly SelectionParseResult Empty =
        new(Array.Empty<SelectionOperation>(), true, null);
}

/// <summary>
/// Bridges query text and the query-builder tree (Postman-style checkbox builder).
/// Parse: document → per-operation selection trees, with argument values sliced verbatim
/// from the original text. Render: selection trees → pretty 2-space-indented document.
/// Both directions are lossy only for the constructs flagged by
/// <see cref="SelectionParseResult.IsBuilderCompatible"/>.
/// </summary>
public static class GraphQLSelectionDocument
{
    public static SelectionParseResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return SelectionParseResult.Empty;
        var masked = InterpolationMasker.Mask(text);
        GraphQLDocument doc;
        try
        {
            doc = Parser.Parse(masked);
        }
        catch (GraphQLSyntaxErrorException)
        {
            // Half-typed text: keep the last good tree; caller treats null-ish as "no update".
            return new SelectionParseResult(Array.Empty<SelectionOperation>(), false, "syntax error");
        }
        catch (Exception)
        {
            return new SelectionParseResult(Array.Empty<SelectionOperation>(), false, "syntax error");
        }

        if (doc.Definitions.Any(d => d is GraphQLFragmentDefinition))
            return Incompatible("fragments");

        var operations = new List<SelectionOperation>();
        foreach (var def in doc.Definitions)
        {
            if (def is not GraphQLOperationDefinition op) continue;
            if (op.Directives is { Items.Count: > 0 }) return Incompatible("directives");
            var operation = new SelectionOperation
            {
                Kind = op.Operation switch
                {
                    OperationType.Mutation => GraphQLOperationKind.Mutation,
                    OperationType.Subscription => GraphQLOperationKind.Subscription,
                    _ => GraphQLOperationKind.Query,
                },
                Name = op.Name?.StringValue,
            };
            var (nodes, reason) = ReadSelections(op.SelectionSet, text);
            if (reason is not null) return Incompatible(reason);
            operation.Selections.AddRange(nodes);
            operations.Add(operation);
        }
        return new SelectionParseResult(operations, true, null);

        static SelectionParseResult Incompatible(string what) => new(
            Array.Empty<SelectionOperation>(), false,
            $"The document uses {what}, which the builder can't represent — edit the text directly.");
    }

    private static (List<SelectionNode> Nodes, string? IncompatibleReason) ReadSelections(
        GraphQLSelectionSet? set, string originalText)
    {
        var nodes = new List<SelectionNode>();
        if (set is null) return (nodes, null);
        foreach (var selection in set.Selections)
        {
            switch (selection)
            {
                case GraphQLField field:
                {
                    if (field.Alias is not null) return (nodes, "aliases");
                    if (field.Directives is { Items.Count: > 0 }) return (nodes, "directives");
                    var node = new SelectionNode { Name = field.Name.StringValue };
                    if (field.Arguments is not null)
                    {
                        foreach (var arg in field.Arguments.Items)
                        {
                            // Slice the ORIGINAL text (mask is length-preserving) so
                            // {{var}} / $var / enum literals come through verbatim.
                            var start = arg.Value.Location.Start;
                            var end = Math.Min(arg.Value.Location.End, originalText.Length);
                            node.Args.Add(new(
                                arg.Name.StringValue,
                                originalText[start..end].Trim()));
                        }
                    }
                    var (children, reason) = ReadSelections(field.SelectionSet, originalText);
                    if (reason is not null) return (nodes, reason);
                    node.Children.AddRange(children);
                    nodes.Add(node);
                    break;
                }
                case GraphQLInlineFragment:
                    return (nodes, "inline fragments");
                case GraphQLFragmentSpread:
                    return (nodes, "fragments");
            }
        }
        return (nodes, null);
    }

    /// <summary>Renders operations to query text. Operations with no selections are skipped;
    /// returns an empty string when nothing is selected anywhere.</summary>
    public static string Render(IEnumerable<SelectionOperation> operations)
    {
        var sb = new StringBuilder();
        foreach (var op in operations)
        {
            if (op.Selections.Count == 0) continue;
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(op.Kind switch
            {
                GraphQLOperationKind.Mutation => "mutation",
                GraphQLOperationKind.Subscription => "subscription",
                _ => "query",
            });
            if (!string.IsNullOrEmpty(op.Name)) sb.Append(' ').Append(op.Name);
            sb.AppendLine(" {");
            foreach (var node in op.Selections) RenderNode(sb, node, 1);
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    private static void RenderNode(StringBuilder sb, SelectionNode node, int depth)
    {
        sb.Append(new string(' ', depth * 2)).Append(node.Name);
        if (node.Args.Count > 0)
        {
            sb.Append('(')
              .Append(string.Join(", ", node.Args.Select(a => $"{a.Key}: {a.Value}")))
              .Append(')');
        }
        if (node.Children.Count > 0 || node.ForceSelectionSet)
        {
            sb.AppendLine(" {");
            foreach (var child in node.Children) RenderNode(sb, child, depth + 1);
            sb.Append(new string(' ', depth * 2)).AppendLine("}");
        }
        else
        {
            sb.AppendLine();
        }
    }
}
