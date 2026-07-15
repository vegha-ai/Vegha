namespace Vegha.Core.GraphQL.Editor;

internal enum GqlTokenKind
{
    Name,       // identifier (fields, types, keywords, enum literals)
    Variable,   // $name (includes bare "$")
    Directive,  // @name (includes bare "@")
    Punct,      // one of { } ( ) [ ] : = | & ! ,
    Spread,     // ...
    String,     // "..." or """...""" (possibly unterminated)
    Number,
    Invalid,    // anything unrecognized — skipped by consumers
}

internal readonly record struct GqlToken(GqlTokenKind Kind, int Start, int Length, char Punct, string Value)
{
    public int End => Start + Length;
}

/// <summary>
/// Tolerant forward GraphQL lexer for editor features. Never throws — half-typed documents
/// produce a best-effort token stream. Comments are skipped; unterminated strings consume
/// to end-of-line (so typing inside a string doesn't poison the rest of the document).
/// Callers should mask <c>{{var}}</c> tokens first (see <see cref="InterpolationMasker"/>).
/// </summary>
internal static class GraphQLTokenScanner
{
    /// <summary>Scans <paramref name="text"/> from the start up to <paramref name="limit"/>
    /// (exclusive). Pass <c>text.Length</c> to scan everything.</summary>
    public static List<GqlToken> Scan(string text, int limit)
    {
        var tokens = new List<GqlToken>();
        limit = Math.Min(limit, text.Length);
        var i = 0;
        while (i < limit)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '#') // comment to end of line
            {
                while (i < limit && text[i] != '\n') i++;
                continue;
            }

            if (c == '"')
            {
                i = ScanString(text, limit, tokens, i);
                continue;
            }

            if (c == '.')
            {
                if (i + 2 < limit && text[i + 1] == '.' && text[i + 2] == '.')
                {
                    tokens.Add(new GqlToken(GqlTokenKind.Spread, i, 3, '\0', "..."));
                    i += 3;
                }
                else
                {
                    tokens.Add(new GqlToken(GqlTokenKind.Invalid, i, 1, '\0', "."));
                    i++;
                }
                continue;
            }

            if (c is '$' or '@')
            {
                var start = i;
                i++;
                while (i < limit && IsNameChar(text[i])) i++;
                tokens.Add(new GqlToken(
                    c == '$' ? GqlTokenKind.Variable : GqlTokenKind.Directive,
                    start, i - start, '\0', text[(start + 1)..i]));
                continue;
            }

            if (IsNameStart(c))
            {
                var start = i;
                while (i < limit && IsNameChar(text[i])) i++;
                tokens.Add(new GqlToken(GqlTokenKind.Name, start, i - start, '\0', text[start..i]));
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '-' && i + 1 < limit && char.IsAsciiDigit(text[i + 1])))
            {
                var start = i;
                i++;
                while (i < limit && (char.IsAsciiDigit(text[i]) || text[i] is '.' or 'e' or 'E' or '+' or '-')) i++;
                tokens.Add(new GqlToken(GqlTokenKind.Number, start, i - start, '\0', text[start..i]));
                continue;
            }

            if (c is '{' or '}' or '(' or ')' or '[' or ']' or ':' or '=' or '|' or '&' or '!' or ',')
            {
                tokens.Add(new GqlToken(GqlTokenKind.Punct, i, 1, c, text[i].ToString()));
                i++;
                continue;
            }

            tokens.Add(new GqlToken(GqlTokenKind.Invalid, i, 1, '\0', text[i].ToString()));
            i++;
        }
        return tokens;
    }

    private static int ScanString(string text, int limit, List<GqlToken> tokens, int start)
    {
        // Block string?
        if (start + 2 < limit && text[start + 1] == '"' && text[start + 2] == '"')
        {
            var j = start + 3;
            while (j + 2 < limit && !(text[j] == '"' && text[j + 1] == '"' && text[j + 2] == '"')) j++;
            var end = j + 2 < limit ? j + 3 : limit; // unterminated: consume to limit
            tokens.Add(new GqlToken(GqlTokenKind.String, start, end - start, '\0', string.Empty));
            return end;
        }

        var i = start + 1;
        while (i < limit && text[i] != '"' && text[i] != '\n')
        {
            if (text[i] == '\\' && i + 1 < limit) i++; // escaped char (incl. \")
            i++;
        }
        var stop = i < limit && text[i] == '"' ? i + 1 : i; // unterminated: stop at EOL/limit
        tokens.Add(new GqlToken(GqlTokenKind.String, start, stop - start, '\0', string.Empty));
        return stop;
    }

    internal static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c == '_';
    internal static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
