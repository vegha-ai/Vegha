using System.Text;

namespace Vegha.Core.GraphQL.Schema;

/// <summary>
/// Renders a <see cref="GraphQLSchemaModel"/> back to SDL text for the "View SDL" /
/// "Export SDL" features. Root types print first, then everything else alphabetically.
/// </summary>
public static class SdlRenderer
{
    private static readonly HashSet<string> BuiltInScalars =
        new(StringComparer.Ordinal) { "String", "Int", "Float", "Boolean", "ID" };

    public static string Render(GraphQLSchemaModel schema)
    {
        var sb = new StringBuilder(64 * 1024);

        // Explicit schema block only when the roots deviate from the conventional names.
        var conventional = schema.QueryTypeName is null or "Query"
            && schema.MutationTypeName is null or "Mutation"
            && schema.SubscriptionTypeName is null or "Subscription";
        if (!conventional)
        {
            sb.AppendLine("schema {");
            if (schema.QueryTypeName is { } q) sb.AppendLine($"  query: {q}");
            if (schema.MutationTypeName is { } m) sb.AppendLine($"  mutation: {m}");
            if (schema.SubscriptionTypeName is { } s) sb.AppendLine($"  subscription: {s}");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        var roots = new[] { schema.QueryTypeName, schema.MutationTypeName, schema.SubscriptionTypeName }
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();
        var ordered = roots
            .Select(schema.FindType)
            .Where(t => t is not null)
            .Select(t => t!)
            .Concat(schema.Types.Values
                .Where(t => !roots.Contains(t.Name))
                .OrderBy(t => t.Name, StringComparer.Ordinal));

        foreach (var type in ordered)
        {
            if (type.Kind == GraphQLTypeKind.Scalar && BuiltInScalars.Contains(type.Name))
                continue;
            RenderType(sb, type);
            sb.AppendLine();
        }

        foreach (var directive in schema.Directives
            .Where(d => d.Name is not ("skip" or "include" or "deprecated" or "specifiedBy"))
            .OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            Description(sb, directive.Description, indent: "");
            sb.Append("directive @").Append(directive.Name);
            AppendArgs(sb, directive.Args);
            sb.Append(" on ").AppendLine(string.Join(" | ", directive.Locations));
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static void RenderType(StringBuilder sb, GraphQLTypeInfo type)
    {
        Description(sb, type.Description, indent: "");
        switch (type.Kind)
        {
            case GraphQLTypeKind.Scalar:
                sb.Append("scalar ").AppendLine(type.Name);
                break;
            case GraphQLTypeKind.Enum:
                sb.Append("enum ").Append(type.Name).AppendLine(" {");
                foreach (var v in type.EnumValues)
                {
                    Description(sb, v.Description, indent: "  ");
                    sb.Append("  ").Append(v.Name);
                    if (v.IsDeprecated) sb.Append(" @deprecated");
                    sb.AppendLine();
                }
                sb.AppendLine("}");
                break;
            case GraphQLTypeKind.Union:
                sb.Append("union ").Append(type.Name).Append(" = ")
                  .AppendLine(string.Join(" | ", type.PossibleTypes));
                break;
            case GraphQLTypeKind.InputObject:
                sb.Append("input ").Append(type.Name).AppendLine(" {");
                foreach (var f in type.InputFields)
                {
                    Description(sb, f.Description, indent: "  ");
                    sb.Append("  ").Append(f.Name).Append(": ").Append(f.Type.Display);
                    if (f.DefaultValue is not null) sb.Append(" = ").Append(f.DefaultValue);
                    sb.AppendLine();
                }
                sb.AppendLine("}");
                break;
            default: // Object / Interface / Unknown-with-fields
                sb.Append(type.Kind == GraphQLTypeKind.Interface ? "interface " : "type ")
                  .Append(type.Name);
                if (type.Interfaces.Count > 0)
                    sb.Append(" implements ").Append(string.Join(" & ", type.Interfaces));
                sb.AppendLine(" {");
                foreach (var f in type.Fields)
                {
                    Description(sb, f.Description, indent: "  ");
                    sb.Append("  ").Append(f.Name);
                    AppendArgs(sb, f.Args);
                    sb.Append(": ").Append(f.Type.Display);
                    if (f.IsDeprecated)
                    {
                        sb.Append(" @deprecated");
                        if (!string.IsNullOrEmpty(f.DeprecationReason))
                            sb.Append("(reason: ").Append(Quote(f.DeprecationReason!)).Append(')');
                    }
                    sb.AppendLine();
                }
                sb.AppendLine("}");
                break;
        }
    }

    private static void AppendArgs(StringBuilder sb, IReadOnlyList<GraphQLArgInfo> args)
    {
        if (args.Count == 0) return;
        sb.Append('(');
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(args[i].Name).Append(": ").Append(args[i].Type.Display);
            if (args[i].DefaultValue is not null) sb.Append(" = ").Append(args[i].DefaultValue);
        }
        sb.Append(')');
    }

    private static void Description(StringBuilder sb, string? description, string indent)
    {
        if (string.IsNullOrWhiteSpace(description)) return;
        // Single-line descriptions use the compact "..." form; multi-line use block strings.
        if (!description.Contains('\n'))
        {
            sb.Append(indent).AppendLine(Quote(description));
            return;
        }
        sb.Append(indent).AppendLine("\"\"\"");
        foreach (var line in description.Split('\n'))
            sb.Append(indent).AppendLine(line.TrimEnd());
        sb.Append(indent).AppendLine("\"\"\"");
    }

    private static string Quote(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
