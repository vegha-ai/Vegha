using GraphQLParser;
using GraphQLParser.AST;
using GraphQLParser.Exceptions;

namespace Vegha.Core.GraphQL;

/// <summary>Kind of a GraphQL operation definition.</summary>
public enum GraphQLOperationKind { Query, Mutation, Subscription }

/// <summary>One <c>$variable</c> declared by an operation, e.g. <c>$id: ID! = "u_1"</c>.</summary>
public sealed record GraphQLVariableInfo(string Name, string TypeText, bool HasDefault);

/// <summary>One operation definition found in the document. <see cref="Name"/> is null for
/// anonymous (shorthand) operations.</summary>
public sealed record GraphQLOperationInfo(
    string? Name,
    GraphQLOperationKind Kind,
    IReadOnlyList<GraphQLVariableInfo> Variables);

/// <summary>A syntax problem with editor-addressable coordinates. <see cref="Offset"/> and
/// <see cref="Length"/> are in character units on the original (unmasked) text.</summary>
public sealed record GraphQLDiagnostic(int Offset, int Length, int Line, int Column, string Message);

/// <summary>Result of analyzing a GraphQL document text.</summary>
public sealed record GraphQLDocumentInfo(
    IReadOnlyList<GraphQLOperationInfo> Operations,
    IReadOnlyList<GraphQLDiagnostic> SyntaxErrors)
{
    public static readonly GraphQLDocumentInfo Empty =
        new(Array.Empty<GraphQLOperationInfo>(), Array.Empty<GraphQLDiagnostic>());
}

/// <summary>
/// Parses GraphQL editor text into operation metadata + syntax diagnostics. Never throws —
/// parse failures surface as <see cref="GraphQLDocumentInfo.SyntaxErrors"/>. Vegha
/// <c>{{var}}</c> interpolation tokens are masked to same-length identifiers before parsing
/// (see <see cref="InterpolationMasker"/>), so offsets map straight onto the editor text.
/// </summary>
public static class GraphQLDocumentAnalyzer
{
    public static GraphQLDocumentInfo Analyze(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return GraphQLDocumentInfo.Empty;
        var masked = InterpolationMasker.Mask(text);
        GraphQLDocument doc;
        try
        {
            doc = Parser.Parse(masked);
        }
        catch (GraphQLSyntaxErrorException ex)
        {
            return new GraphQLDocumentInfo(
                Array.Empty<GraphQLOperationInfo>(),
                new[] { ToDiagnostic(ex, masked) });
        }
        catch (Exception ex)
        {
            // The parser is not documented to throw anything else, but the editor must
            // never crash on keystrokes — degrade to a document-start diagnostic.
            return new GraphQLDocumentInfo(
                Array.Empty<GraphQLOperationInfo>(),
                new[] { new GraphQLDiagnostic(0, 1, 1, 1, ex.Message) });
        }

        var ops = new List<GraphQLOperationInfo>();
        foreach (var def in doc.Definitions)
        {
            if (def is not GraphQLOperationDefinition op) continue;
            var vars = new List<GraphQLVariableInfo>();
            if (op.Variables is { Items.Count: > 0 })
            {
                foreach (var v in op.Variables.Items)
                {
                    vars.Add(new GraphQLVariableInfo(
                        v.Variable.Name.StringValue,
                        RenderType(v.Type),
                        v.DefaultValue is not null));
                }
            }
            ops.Add(new GraphQLOperationInfo(
                op.Name?.StringValue,
                op.Operation switch
                {
                    OperationType.Mutation => GraphQLOperationKind.Mutation,
                    OperationType.Subscription => GraphQLOperationKind.Subscription,
                    _ => GraphQLOperationKind.Query,
                },
                vars));
        }
        return new GraphQLDocumentInfo(ops, Array.Empty<GraphQLDiagnostic>());
    }

    /// <summary>The <c>operationName</c> to include on the wire: null for 0/1-operation documents
    /// (servers don't require it and Bruno omits it); otherwise <paramref name="preferred"/> when
    /// it names an operation in the document, else the first named operation. A multi-operation
    /// document with no name sent is a guaranteed server error, so always picking one is the
    /// user-friendly wire behavior.</summary>
    public static string? ResolveOperationNameForSend(string? query, string? preferred = null)
    {
        var info = Analyze(query);
        if (info.Operations.Count <= 1) return null;
        var names = info.Operations
            .Where(o => !string.IsNullOrEmpty(o.Name))
            .Select(o => o.Name!)
            .ToList();
        if (names.Count == 0) return null;
        return preferred is not null && names.Contains(preferred) ? preferred : names[0];
    }

    /// <summary>Renders a type node back to source form (<c>[User!]!</c> etc.).</summary>
    internal static string RenderType(GraphQLType type) => type switch
    {
        GraphQLNonNullType nn => RenderType(nn.Type) + "!",
        GraphQLListType list => "[" + RenderType(list.Type) + "]",
        GraphQLNamedType named => named.Name.StringValue,
        _ => type.ToString() ?? string.Empty,
    };

    private static GraphQLDiagnostic ToDiagnostic(GraphQLSyntaxErrorException ex, string text)
    {
        // The exception's Location is 1-based line/column on the source we passed in.
        var line = Math.Max(1, ex.Location.Line);
        var column = Math.Max(1, ex.Location.Column);
        var offset = Math.Clamp(LineColumnToOffset(text, line, column), 0, Math.Max(0, text.Length - 1));
        // Squiggle the rest of the token at the error position (up to the next whitespace),
        // minimum 1 char so zero-width errors at EOF stay visible.
        var end = offset;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        var length = Math.Max(1, end - offset);
        return new GraphQLDiagnostic(offset, length, line, column, CleanMessage(ex.Description));
    }

    internal static int LineColumnToOffset(string text, int line, int column)
    {
        var offset = 0;
        for (var current = 1; current < line && offset < text.Length; offset++)
        {
            if (text[offset] == '\n') current++;
        }
        return offset + (column - 1);
    }

    private static string CleanMessage(string description) =>
        string.IsNullOrWhiteSpace(description) ? "Syntax error" : description.Trim();

    internal static (int Line, int Column) OffsetToLineColumn(string text, int offset)
    {
        int line = 1, col = 1;
        var max = Math.Min(offset, text.Length);
        for (var i = 0; i < max; i++)
        {
            if (text[i] == '\n') { line++; col = 1; }
            else col++;
        }
        return (line, col);
    }
}
