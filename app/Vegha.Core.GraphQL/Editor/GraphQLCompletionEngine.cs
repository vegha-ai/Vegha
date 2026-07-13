using Vegha.Core.GraphQL.Schema;

namespace Vegha.Core.GraphQL.Editor;

public enum GraphQLCompletionItemKind { Field, Argument, Type, EnumValue, Directive, Variable, Keyword }

/// <summary>One completion candidate. <see cref="Detail"/> is the type signature shown next
/// to the label; <see cref="Description"/> is the schema doc string for the tooltip.</summary>
public sealed record GraphQLCompletionItem(
    string Label,
    string InsertText,
    string? Detail,
    string? Description,
    GraphQLCompletionItemKind Kind,
    bool IsDeprecated = false);

public sealed record GraphQLCompletionResult(
    IReadOnlyList<GraphQLCompletionItem> Items,
    int ReplaceStart,
    string PartialWord)
{
    public static readonly GraphQLCompletionResult Empty =
        new(Array.Empty<GraphQLCompletionItem>(), 0, string.Empty);
}

/// <summary>
/// Schema-aware completion: cursor context + schema → ranked candidates. Alphabetical
/// within kind, capped at 300 (the editor's CompletionWindow filters incrementally as the
/// user keeps typing). No schema → only schema-independent items (keywords, variables).
/// </summary>
public static class GraphQLCompletionEngine
{
    private const int Cap = 300;
    private static readonly string[] BuiltInScalars = { "Boolean", "Float", "ID", "Int", "String" };

    public static GraphQLCompletionResult GetCompletions(string text, int caret, GraphQLSchemaModel? schema)
    {
        var ctx = GraphQLCursorContextEngine.Compute(text, caret, schema);
        var items = new List<GraphQLCompletionItem>();

        switch (ctx.Kind)
        {
            case GraphQLCompletionContextKind.OperationKeyword:
                foreach (var kw in new[] { "query", "mutation", "subscription", "fragment" })
                    items.Add(new GraphQLCompletionItem(kw, kw, null, null, GraphQLCompletionItemKind.Keyword));
                break;

            case GraphQLCompletionContextKind.FieldSelection:
            {
                var type = schema?.FindType(ctx.ContainerTypeName);
                if (type is null) break;
                foreach (var f in type.Fields.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    items.Add(new GraphQLCompletionItem(
                        f.Name, f.Name, f.Type.Display, f.Description,
                        GraphQLCompletionItemKind.Field, f.IsDeprecated));
                }
                items.Add(new GraphQLCompletionItem(
                    "__typename", "__typename", "String!", "The name of the object's concrete type.",
                    GraphQLCompletionItemKind.Field));
                break;
            }

            case GraphQLCompletionContextKind.ArgumentName:
            {
                var field = schema?.FindType(ctx.ContainerTypeName)?
                    .Fields.FirstOrDefault(f => f.Name == ctx.FieldName);
                if (field is null) break;
                foreach (var a in field.Args.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                {
                    items.Add(new GraphQLCompletionItem(
                        a.Name, a.Name + ": ", a.Type.Display, a.Description,
                        GraphQLCompletionItemKind.Argument));
                }
                break;
            }

            case GraphQLCompletionContextKind.ArgumentValue:
            {
                var arg = schema?.FindType(ctx.ContainerTypeName)?
                    .Fields.FirstOrDefault(f => f.Name == ctx.FieldName)?
                    .Args.FirstOrDefault(a => a.Name == ctx.ArgumentName);
                var valueType = arg is null ? null : schema?.FindType(arg.Type.UnwrappedName);
                if (valueType?.Kind == GraphQLTypeKind.Enum)
                {
                    foreach (var v in valueType.EnumValues)
                        items.Add(new GraphQLCompletionItem(
                            v.Name, v.Name, valueType.Name, v.Description,
                            GraphQLCompletionItemKind.EnumValue, v.IsDeprecated));
                }
                else if (arg?.Type.UnwrappedName == "Boolean")
                {
                    items.Add(new GraphQLCompletionItem("true", "true", "Boolean", null, GraphQLCompletionItemKind.EnumValue));
                    items.Add(new GraphQLCompletionItem("false", "false", "Boolean", null, GraphQLCompletionItemKind.EnumValue));
                }
                foreach (var v in ctx.DeclaredVariables)
                    items.Add(new GraphQLCompletionItem(
                        "$" + v, "$" + v, null, "Declared operation variable",
                        GraphQLCompletionItemKind.Variable));
                break;
            }

            case GraphQLCompletionContextKind.VariableDefinitionType:
            {
                foreach (var s in BuiltInScalars)
                    items.Add(new GraphQLCompletionItem(s, s, "scalar", null, GraphQLCompletionItemKind.Type));
                if (schema is not null)
                {
                    foreach (var t in schema.Types.Values
                        .Where(t => t.Kind is GraphQLTypeKind.Scalar or GraphQLTypeKind.Enum or GraphQLTypeKind.InputObject)
                        .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        items.Add(new GraphQLCompletionItem(
                            t.Name, t.Name, KindLabel(t.Kind), t.Description, GraphQLCompletionItemKind.Type));
                    }
                }
                break;
            }

            case GraphQLCompletionContextKind.FragmentConditionType:
            {
                if (schema is null) break;
                foreach (var t in schema.Types.Values
                    .Where(t => t.Kind is GraphQLTypeKind.Object or GraphQLTypeKind.Interface or GraphQLTypeKind.Union)
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                {
                    items.Add(new GraphQLCompletionItem(
                        t.Name, t.Name, KindLabel(t.Kind), t.Description, GraphQLCompletionItemKind.Type));
                }
                break;
            }

            case GraphQLCompletionContextKind.Directive:
            {
                if (schema is null || schema.Directives.Count == 0)
                {
                    // Universal executable directives every server supports.
                    items.Add(new GraphQLCompletionItem("include", "include(if: ", "@include(if: Boolean!)", null, GraphQLCompletionItemKind.Directive));
                    items.Add(new GraphQLCompletionItem("skip", "skip(if: ", "@skip(if: Boolean!)", null, GraphQLCompletionItemKind.Directive));
                    break;
                }
                foreach (var d in schema.Directives.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var detail = d.Args.Count == 0
                        ? "@" + d.Name
                        : $"@{d.Name}({string.Join(", ", d.Args.Select(a =>
                            a.DefaultValue is null
                                ? $"{a.Name}: {a.Type.Display}"
                                : $"{a.Name}: {a.Type.Display} = {a.DefaultValue}"))})";
                    items.Add(new GraphQLCompletionItem(
                        d.Name, d.Name, detail, d.Description, GraphQLCompletionItemKind.Directive));
                }
                break;
            }

            case GraphQLCompletionContextKind.VariableUse:
                foreach (var v in ctx.DeclaredVariables)
                    items.Add(new GraphQLCompletionItem(
                        v, v, null, "Declared operation variable", GraphQLCompletionItemKind.Variable));
                break;
        }

        if (items.Count == 0) return GraphQLCompletionResult.Empty;
        if (items.Count > Cap) items.RemoveRange(Cap, items.Count - Cap);
        return new GraphQLCompletionResult(items, ctx.ReplaceStart, ctx.PartialWord);
    }

    private static string KindLabel(GraphQLTypeKind kind) => kind switch
    {
        GraphQLTypeKind.Scalar => "scalar",
        GraphQLTypeKind.Enum => "enum",
        GraphQLTypeKind.InputObject => "input",
        GraphQLTypeKind.Interface => "interface",
        GraphQLTypeKind.Union => "union",
        _ => "type",
    };
}
