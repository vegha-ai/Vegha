using GraphQLParser;
using GraphQLParser.AST;
using GraphQLParser.Exceptions;
using Vegha.Core.GraphQL.Schema;

namespace Vegha.Core.GraphQL.Validation;

/// <summary>
/// Schema validation for executable documents — the "unknowns" class of mistakes that
/// squiggle in the editor: unknown types/fields/arguments/directives/enum literals, missing
/// required arguments, and undefined <c>$variables</c>. Deliberately NOT a full spec
/// validator (no overlapping-field merge rules, no fragment cycle detection) — those are
/// rare authoring mistakes with poor effort/value in an editor. Only runs when a schema is
/// loaded; documents with syntax errors are skipped (the analyzer already squiggles those).
/// Offsets are on the masked text, which is length-identical to the editor text.
/// </summary>
public static class GraphQLDocumentValidator
{
    private static readonly HashSet<string> BuiltInScalars =
        new(StringComparer.Ordinal) { "String", "Int", "Float", "Boolean", "ID" };

    private static readonly HashSet<string> BuiltInDirectives =
        new(StringComparer.Ordinal) { "skip", "include", "deprecated", "specifiedBy", "oneOf" };

    public static IReadOnlyList<GraphQLDiagnostic> Validate(string? text, GraphQLSchemaModel schema)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<GraphQLDiagnostic>();
        var masked = InterpolationMasker.Mask(text);
        GraphQLDocument doc;
        try
        {
            doc = Parser.Parse(masked);
        }
        catch (GraphQLSyntaxErrorException)
        {
            return Array.Empty<GraphQLDiagnostic>(); // syntax squiggles come from the analyzer
        }
        catch (Exception)
        {
            return Array.Empty<GraphQLDiagnostic>();
        }

        var diagnostics = new List<GraphQLDiagnostic>();
        var walker = new Walker(schema, masked, diagnostics);
        try
        {
            walker.Walk(doc);
        }
        catch (Exception)
        {
            // A validator crash must never break the editor — degrade to whatever was found.
        }
        return diagnostics;
    }

    private sealed class Walker
    {
        private readonly GraphQLSchemaModel _schema;
        private readonly string _text;
        private readonly List<GraphQLDiagnostic> _diagnostics;
        private readonly Dictionary<string, GraphQLFragmentDefinition> _fragments = new(StringComparer.Ordinal);

        public Walker(GraphQLSchemaModel schema, string text, List<GraphQLDiagnostic> diagnostics)
        {
            _schema = schema;
            _text = text;
            _diagnostics = diagnostics;
        }

        public void Walk(GraphQLDocument doc)
        {
            foreach (var def in doc.Definitions)
                if (def is GraphQLFragmentDefinition frag)
                    _fragments[frag.FragmentName.Name.StringValue] = frag;

            foreach (var def in doc.Definitions)
            {
                switch (def)
                {
                    case GraphQLOperationDefinition op:
                        WalkOperation(op);
                        break;
                    case GraphQLFragmentDefinition frag:
                        WalkFragmentDefinition(frag);
                        break;
                }
            }
        }

        private void WalkOperation(GraphQLOperationDefinition op)
        {
            var declared = new HashSet<string>(StringComparer.Ordinal);
            if (op.Variables is not null)
            {
                foreach (var v in op.Variables.Items)
                {
                    declared.Add(v.Variable.Name.StringValue);
                    CheckTypeExists(v.Type);
                }
            }

            var rootKind = op.Operation switch
            {
                OperationType.Mutation => GraphQLOperationKind.Mutation,
                OperationType.Subscription => GraphQLOperationKind.Subscription,
                _ => GraphQLOperationKind.Query,
            };
            var root = _schema.RootTypeFor(rootKind);
            if (root is null)
            {
                Report(op, $"The schema does not define a {rootKind.ToString().ToLowerInvariant()} root type.");
                return;
            }
            CheckDirectives(op.Directives, declared);
            WalkSelectionSet(op.SelectionSet, root, declared, isRoot: rootKind == GraphQLOperationKind.Query);
        }

        private void WalkFragmentDefinition(GraphQLFragmentDefinition frag)
        {
            var typeName = frag.TypeCondition.Type.Name.StringValue;
            var type = _schema.FindType(typeName);
            if (type is null)
            {
                Report(frag.TypeCondition.Type, $"Unknown type \"{typeName}\".");
                return;
            }
            // $vars inside fragment definitions belong to whichever operation spreads the
            // fragment — undefined-variable checking is skipped here (declared = null).
            WalkSelectionSet(frag.SelectionSet, type, declaredVariables: null, isRoot: false);
        }

        private void WalkSelectionSet(
            GraphQLSelectionSet? set, GraphQLTypeInfo type,
            HashSet<string>? declaredVariables, bool isRoot)
        {
            if (set is null) return;
            foreach (var selection in set.Selections)
            {
                switch (selection)
                {
                    case GraphQLField field:
                        WalkField(field, type, declaredVariables, isRoot);
                        break;
                    case GraphQLInlineFragment inline:
                    {
                        CheckDirectives(inline.Directives, declaredVariables);
                        var target = type;
                        if (inline.TypeCondition is { } cond)
                        {
                            var name = cond.Type.Name.StringValue;
                            var found = _schema.FindType(name);
                            if (found is null)
                            {
                                Report(cond.Type, $"Unknown type \"{name}\".");
                                continue;
                            }
                            target = found;
                        }
                        WalkSelectionSet(inline.SelectionSet, target, declaredVariables, isRoot: false);
                        break;
                    }
                    case GraphQLFragmentSpread spread:
                        CheckDirectives(spread.Directives, declaredVariables);
                        if (!_fragments.ContainsKey(spread.FragmentName.Name.StringValue))
                            Report(spread, $"Unknown fragment \"{spread.FragmentName.Name.StringValue}\".");
                        break;
                }
            }
        }

        private void WalkField(
            GraphQLField field, GraphQLTypeInfo parent,
            HashSet<string>? declaredVariables, bool isRoot)
        {
            CheckDirectives(field.Directives, declaredVariables);
            var name = field.Name.StringValue;

            // Meta fields: __typename anywhere; __schema/__type only on the query root.
            if (name == "__typename") return;
            if (isRoot && name is "__schema" or "__type") return;

            var info = parent.Fields.FirstOrDefault(f => f.Name == name);
            if (info is null)
            {
                var hint = parent.Kind == GraphQLTypeKind.Union
                    ? " (union members need an inline fragment: \"... on TypeName\")"
                    : string.Empty;
                Report(field.Name, $"Field \"{name}\" does not exist on type \"{parent.Name}\"{hint}.");
                return;
            }

            CheckArguments(field, info, declaredVariables);

            if (field.SelectionSet is not null)
            {
                var child = _schema.FindType(info.Type.UnwrappedName);
                if (child is not null)
                    WalkSelectionSet(field.SelectionSet, child, declaredVariables, isRoot: false);
            }
        }

        private void CheckArguments(GraphQLField field, GraphQLFieldInfo info, HashSet<string>? declaredVariables)
        {
            var provided = new HashSet<string>(StringComparer.Ordinal);
            if (field.Arguments is not null)
            {
                foreach (var arg in field.Arguments.Items)
                {
                    var argName = arg.Name.StringValue;
                    provided.Add(argName);
                    var argInfo = info.Args.FirstOrDefault(a => a.Name == argName);
                    if (argInfo is null)
                    {
                        Report(arg.Name,
                            $"Unknown argument \"{argName}\" on field \"{info.Name}\".");
                        continue;
                    }
                    CheckValue(arg.Value, argInfo.Type, declaredVariables);
                }
            }

            foreach (var required in info.Args.Where(a =>
                a.Type.Kind == TypeRefKind.NonNull && a.DefaultValue is null))
            {
                if (!provided.Contains(required.Name))
                {
                    Report(field.Name,
                        $"Field \"{info.Name}\" is missing required argument \"{required.Name}: {required.Type.Display}\".");
                }
            }
        }

        private void CheckValue(GraphQLValue value, TypeRef expected, HashSet<string>? declaredVariables)
        {
            switch (value)
            {
                case GraphQLVariable variable:
                    if (declaredVariables is not null
                        && !declaredVariables.Contains(variable.Name.StringValue))
                    {
                        Report(variable, $"Variable \"${variable.Name.StringValue}\" is not defined by the operation.");
                    }
                    break;
                case GraphQLEnumValue enumValue:
                {
                    var enumType = _schema.FindType(expected.UnwrappedName);
                    if (enumType?.Kind == GraphQLTypeKind.Enum
                        && enumType.EnumValues.All(v => v.Name != enumValue.Name.StringValue))
                    {
                        Report(enumValue,
                            $"\"{enumValue.Name.StringValue}\" is not a value of enum \"{enumType.Name}\".");
                    }
                    break;
                }
                case GraphQLListValue list when list.Values is not null:
                {
                    // Unwrap one list level (best-effort; nested nullability not tracked).
                    var inner = Unwrap(expected, TypeRefKind.List) ?? expected;
                    foreach (var item in list.Values) CheckValue(item, inner, declaredVariables);
                    break;
                }
                case GraphQLObjectValue obj when obj.Fields is not null:
                {
                    var inputType = _schema.FindType(expected.UnwrappedName);
                    if (inputType?.Kind != GraphQLTypeKind.InputObject) break;
                    foreach (var f in obj.Fields)
                    {
                        var inputField = inputType.InputFields.FirstOrDefault(x => x.Name == f.Name.StringValue);
                        if (inputField is null)
                        {
                            Report(f.Name,
                                $"Unknown field \"{f.Name.StringValue}\" on input type \"{inputType.Name}\".");
                            continue;
                        }
                        CheckValue(f.Value, inputField.Type, declaredVariables);
                    }
                    break;
                }
            }
        }

        private void CheckDirectives(GraphQLDirectives? directives, HashSet<string>? declaredVariables)
        {
            if (directives is null) return;
            foreach (var d in directives.Items)
            {
                var name = d.Name.StringValue;
                if (!BuiltInDirectives.Contains(name)
                    && _schema.Directives.All(x => x.Name != name)
                    // Schemas introspected via the no-directives fallback can't vouch for
                    // any custom directive — only flag when we actually know the full list.
                    && _schema.Directives.Count > 0)
                {
                    Report(d, $"Unknown directive \"@{name}\".");
                }
                if (d.Arguments is not null && declaredVariables is not null)
                {
                    foreach (var arg in d.Arguments.Items)
                        CheckValue(arg.Value, TypeRef.Named("Unknown"), declaredVariables);
                }
            }
        }

        private void CheckTypeExists(GraphQLType type)
        {
            var named = Innermost(type);
            var name = named.Name.StringValue;
            if (!BuiltInScalars.Contains(name) && _schema.FindType(name) is null)
                Report(named, $"Unknown type \"{name}\".");
        }

        private static GraphQLNamedType Innermost(GraphQLType type) => type switch
        {
            GraphQLNonNullType nn => Innermost(nn.Type),
            GraphQLListType list => Innermost(list.Type),
            GraphQLNamedType named => named,
            _ => throw new InvalidOperationException("Unexpected type node"),
        };

        private static TypeRef? Unwrap(TypeRef t, TypeRefKind kind)
        {
            var current = t;
            while (current is not null)
            {
                if (current.Kind == kind) return current.OfType;
                current = current.OfType;
            }
            return null;
        }

        private void Report(ASTNode node, string message)
        {
            var start = node.Location.Start;
            var length = Math.Max(1, node.Location.End - node.Location.Start);
            var (line, column) = GraphQLDocumentAnalyzer.OffsetToLineColumn(_text, start);
            _diagnostics.Add(new GraphQLDiagnostic(start, length, line, column, message));
        }
    }
}
