using Vegha.Core.GraphQL.Schema;

namespace Vegha.Core.GraphQL.Editor;

public enum GraphQLCompletionContextKind
{
    /// <summary>No sensible completion at the caret (inside a string, weird spot, …).</summary>
    None,
    /// <summary>Top level of the document — operation keywords.</summary>
    OperationKeyword,
    /// <summary>Inside a selection set — fields of <see cref="GraphQLCursorContext.ContainerTypeName"/>.</summary>
    FieldSelection,
    /// <summary>Inside <c>field(…)</c> before a colon — argument names.</summary>
    ArgumentName,
    /// <summary>After <c>arg:</c> — enum values / variables.</summary>
    ArgumentValue,
    /// <summary>After <c>$x:</c> in an operation header — input type names.</summary>
    VariableDefinitionType,
    /// <summary>After <c>on</c> — composite type names.</summary>
    FragmentConditionType,
    /// <summary>After <c>@</c> — directive names.</summary>
    Directive,
    /// <summary>After <c>$</c> in a value position — declared variables.</summary>
    VariableUse,
}

/// <summary>What the caret is positioned to type, plus everything the completion engine
/// needs to rank candidates. <see cref="ReplaceStart"/>…caret is the partial word being
/// typed (empty when the caret follows a trigger character).</summary>
public sealed record GraphQLCursorContext(
    GraphQLCompletionContextKind Kind,
    string? ContainerTypeName,
    string? FieldName,
    string? ArgumentName,
    IReadOnlyList<string> DeclaredVariables,
    int ReplaceStart,
    string PartialWord);

/// <summary>
/// Computes the completion context at a caret from a (possibly half-typed) GraphQL document.
/// A tolerant token scan maintains a selection-set type stack seeded from the operation
/// kind's root type; braces inside argument parentheses are input-object literals and do not
/// touch the stack. Fails open: anything unrecognized degrades to <c>None</c> or an
/// unknown container type — never an exception.
/// </summary>
public static class GraphQLCursorContextEngine
{
    private sealed class Frame
    {
        public string? TypeName;
        public string? LastFieldName;
    }

    public static GraphQLCursorContext Compute(string text, int caret, GraphQLSchemaModel? schema)
    {
        text ??= string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);
        var masked = InterpolationMasker.Mask(text);

        // Partial word: identifier chars behind the caret, optionally led by $ or @.
        var wordStart = caret;
        while (wordStart > 0 && GraphQLTokenScanner.IsNameChar(masked[wordStart - 1])) wordStart--;
        var sigil = '\0';
        if (wordStart > 0 && (masked[wordStart - 1] is '$' or '@'))
        {
            sigil = masked[wordStart - 1];
            wordStart--;
        }
        var partial = masked[wordStart..caret];

        var tokens = GraphQLTokenScanner.Scan(masked, wordStart);

        var frames = new List<Frame>();
        var declaredVars = new List<string>();
        var parenDepth = 0;
        var literalBraceDepth = 0;
        var inOperationHeader = false;
        var inFragmentHeader = false;
        var pendingOpKind = GraphQLOperationKind.Query;
        string? pendingOnType = null;
        var pendingOn = false;          // just consumed "on", expecting a type name
        var afterSpread = false;        // just consumed "...", expecting "on" or a fragment name
        var afterColon = false;
        string? currentArgName = null;
        var inString = false;           // caret inside an unterminated string?

        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case GqlTokenKind.String:
                    // Unterminated string reaching the scan limit → caret is inside it.
                    inString = token.End >= wordStart
                        && (token.Length < 2 || masked[token.End - 1] != '"' || token.Length == 1);
                    break;

                case GqlTokenKind.Spread:
                    afterSpread = true;
                    continue;

                case GqlTokenKind.Name:
                    if (pendingOn)
                    {
                        pendingOnType = token.Value;
                        pendingOn = false;
                    }
                    else if (afterSpread)
                    {
                        if (token.Value == "on") pendingOn = true;
                        // else: named fragment spread — no state change
                    }
                    else if (frames.Count == 0 && parenDepth == 0 && !inOperationHeader && !inFragmentHeader)
                    {
                        switch (token.Value)
                        {
                            case "query": pendingOpKind = GraphQLOperationKind.Query; inOperationHeader = true; declaredVars.Clear(); break;
                            case "mutation": pendingOpKind = GraphQLOperationKind.Mutation; inOperationHeader = true; declaredVars.Clear(); break;
                            case "subscription": pendingOpKind = GraphQLOperationKind.Subscription; inOperationHeader = true; declaredVars.Clear(); break;
                            case "fragment": inFragmentHeader = true; pendingOnType = null; break;
                        }
                    }
                    else if (inOperationHeader || inFragmentHeader)
                    {
                        if (token.Value == "on") pendingOn = true;
                        // else: operation/fragment name, or a type name in a vardef — no state change
                    }
                    else if (parenDepth > 0)
                    {
                        if (!afterColon && literalBraceDepth == 0) currentArgName = token.Value;
                        // values / nested input-object field names don't affect selection state
                    }
                    else if (frames.Count > 0)
                    {
                        frames[^1].LastFieldName = token.Value;
                    }
                    afterSpread = false;
                    continue;

                case GqlTokenKind.Variable:
                    if ((inOperationHeader || inFragmentHeader) && parenDepth > 0 && !afterColon
                        && token.Value.Length > 0)
                    {
                        declaredVars.Add(token.Value);
                    }
                    break;

                case GqlTokenKind.Punct:
                    switch (token.Punct)
                    {
                        case '(':
                            parenDepth++;
                            afterColon = false;
                            break;
                        case ')':
                            if (parenDepth > 0) parenDepth--;
                            currentArgName = null;
                            afterColon = false;
                            literalBraceDepth = 0;
                            break;
                        case '{':
                            if (parenDepth > 0) { literalBraceDepth++; afterColon = false; break; }
                            frames.Add(new Frame { TypeName = ResolvePushType() });
                            pendingOnType = null;
                            inOperationHeader = false;
                            inFragmentHeader = false;
                            break;
                        case '}':
                            if (parenDepth > 0) { if (literalBraceDepth > 0) literalBraceDepth--; break; }
                            if (frames.Count > 0) frames.RemoveAt(frames.Count - 1);
                            break;
                        case ':':
                            afterColon = true;
                            break;
                        case ',':
                            afterColon = false;
                            if (literalBraceDepth == 0) currentArgName = null;
                            break;
                        case '=':
                            // vardef default value follows — value position
                            afterColon = true;
                            break;
                    }
                    break;
            }
            afterSpread = false;
            pendingOn = token.Kind == GqlTokenKind.Name && pendingOn; // keep only when set this token
        }

        // ---- Decide the caret's context ----
        if (inString)
            return Result(GraphQLCompletionContextKind.None);

        if (sigil == '@')
            return Result(GraphQLCompletionContextKind.Directive);

        if (sigil == '$')
        {
            // Declaring (header parens) vs using (value position) a variable.
            return inOperationHeader || inFragmentHeader
                ? Result(GraphQLCompletionContextKind.None)
                : Result(GraphQLCompletionContextKind.VariableUse);
        }

        if (pendingOn)
            return Result(GraphQLCompletionContextKind.FragmentConditionType);

        if (inOperationHeader || inFragmentHeader)
        {
            return parenDepth > 0 && afterColon
                ? Result(GraphQLCompletionContextKind.VariableDefinitionType)
                : Result(GraphQLCompletionContextKind.None);
        }

        if (parenDepth > 0)
        {
            if (literalBraceDepth > 0)
                return Result(GraphQLCompletionContextKind.None); // input-object literal internals: fail open
            return afterColon
                ? Result(GraphQLCompletionContextKind.ArgumentValue)
                : Result(GraphQLCompletionContextKind.ArgumentName);
        }

        if (frames.Count > 0)
            return Result(GraphQLCompletionContextKind.FieldSelection);

        return Result(GraphQLCompletionContextKind.OperationKeyword);

        GraphQLCursorContext Result(GraphQLCompletionContextKind kind) => new(
            kind,
            frames.Count > 0 ? frames[^1].TypeName : null,
            frames.Count > 0 ? frames[^1].LastFieldName : null,
            currentArgName,
            declaredVars,
            wordStart + (sigil == '\0' ? 0 : 1),
            partial);

        string? ResolvePushType()
        {
            if (pendingOnType is not null) return pendingOnType;                     // "... on X {" / fragment header
            if (frames.Count == 0)
                return inFragmentHeader ? null : schema?.RootTypeFor(pendingOpKind)?.Name;
            // Selection set of the frame's most recent field: its unwrapped return type.
            var parent = frames[^1];
            if (parent.TypeName is null || parent.LastFieldName is null || schema is null) return null;
            var field = schema.FindType(parent.TypeName)?
                .Fields.FirstOrDefault(f => f.Name == parent.LastFieldName);
            return field?.Type.UnwrappedName;
        }
    }
}
