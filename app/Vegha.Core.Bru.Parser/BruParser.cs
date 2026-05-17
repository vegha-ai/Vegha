namespace Vegha.Core.Bru.Parser;

/// <summary>
/// Hand-rolled parser for Bruno's .bru v2 grammar. Mirrors
/// <c>bruno/packages/bruno-lang/v2/src/bruToJson.js</c>.
///
/// Scope:
/// - Top-level blocks: name + dict-or-text body (closed by "\n}" per Bruno's <c>tagend</c>).
/// - Dict body pairs: optional annotations, optional <c>~</c> disable prefix, quoted or
///   unquoted keys, single-line / list / multiline-text-with-contentType values.
/// - Text blocks: script:*, body:json/xml/text/graphql/sparql, tests, docs, body, example.
///
/// Out of scope (intentionally — these are interpretation, not parsing):
/// - Splitting "@file(path) @contentType(type)" inside a body:file value (Bruno does this post-parse).
/// - Multipart "@file()" / type=file detection.
/// - Custom HTTP method recognition (we expose any block name; consumers decide).
/// </summary>
public static class BruParser
{
    public static BruDocument Parse(string bru)
    {
        if (TryParse(bru, out var doc, out var error)) return doc;
        throw new BruParseException(error ?? "Unknown parse error");
    }

    public static bool TryParse(string bru, out BruDocument document, out string? error)
    {
        try
        {
            var scanner = new Scanner(bru);
            var blocks = new List<BruBlock>();
            while (true)
            {
                scanner.SkipBlankLinesAndWhitespace();
                if (scanner.AtEnd) break;
                blocks.Add(ReadBlock(scanner));
            }
            document = new BruDocument(blocks);
            error = null;
            return true;
        }
        catch (BruParseException ex)
        {
            document = new BruDocument(Array.Empty<BruBlock>());
            error = ex.Message;
            return false;
        }
    }

    // ============================== Block-level ==============================

    private static BruBlock ReadBlock(Scanner s)
    {
        var nameStart = s.Position;
        var name = s.ReadWhile(IsBlockNameChar);
        if (name.Length == 0)
            throw s.Error($"Expected block name", nameStart);

        s.SkipHorizontalWs();

        // List-style block: vars:secret [ name1, name2, ... ]
        if (!s.AtEnd && s.Current == '[')
        {
            s.Advance(); // consume '['
            var items = ReadListBlockBody(s, name);
            return new ListBlock(name, items);
        }

        s.Expect('{', $"Expected '{{' after block name '{name}'");

        if (IsTextBlock(name))
        {
            var text = ReadTextBlockBody(s, name);
            return new TextBlock(name, text);
        }
        else
        {
            var pairs = ReadDictBody(s, name);
            return new DictBlock(name, pairs);
        }
    }

    /// <summary>Reads a comma-separated identifier list terminated by ']'. Items can span
    /// multiple lines; whitespace and trailing commas are tolerated.</summary>
    private static IReadOnlyList<string> ReadListBlockBody(Scanner s, string blockName)
    {
        var items = new List<string>();
        while (true)
        {
            s.SkipBlankLinesAndWhitespace();
            if (s.AtEnd) throw s.Error($"Unterminated list block '{blockName}'");
            if (s.Current == ']') { s.Advance(); return items; }

            var ident = s.ReadWhile(IsListItemChar);
            if (ident.Length == 0)
                throw s.Error($"Expected identifier in list block '{blockName}'");
            items.Add(ident);

            s.SkipBlankLinesAndWhitespace();
            if (s.AtEnd) throw s.Error($"Unterminated list block '{blockName}'");
            if (s.Current == ',') { s.Advance(); continue; }
            if (s.Current == ']') { s.Advance(); return items; }
            throw s.Error($"Expected ',' or ']' after '{ident}' in list block '{blockName}'");
        }
    }

    /// <summary>Reads body until "\n}" terminator (per Bruno tagend). Strips one leading newline after "{".</summary>
    private static string ReadTextBlockBody(Scanner s, string blockName)
    {
        // After "{", optional horizontal ws, then a newline, then content, then "\n}".
        s.SkipHorizontalWs();
        if (!s.TryConsumeNewline())
            throw s.Error($"Expected newline after '{{' in text block '{blockName}'");

        var start = s.Position;
        while (!s.AtEnd)
        {
            if (s.Current == '\n' && s.Peek(1) == '}')
            {
                var text = s.Substring(start, s.Position - start);
                s.Advance();   // consume '\n'
                s.Expect('}', "internal: expected '}' after newline terminator");
                return text;
            }
            s.Advance();
        }
        throw s.Error($"Unterminated text block '{blockName}'", start);
    }

    private static bool IsTextBlock(string name) =>
        name.StartsWith("script", StringComparison.Ordinal) ||
        name.StartsWith("body:json", StringComparison.Ordinal) ||
        name.StartsWith("body:text", StringComparison.Ordinal) ||
        name.StartsWith("body:xml", StringComparison.Ordinal) ||
        name.StartsWith("body:sparql", StringComparison.Ordinal) ||
        name.StartsWith("body:graphql", StringComparison.Ordinal) ||
        name == "body" ||
        name == "tests" ||
        name == "docs" ||
        name == "example";

    // ============================== Dict body ==============================

    private static IReadOnlyList<BruPair> ReadDictBody(Scanner s, string blockName)
    {
        var pairs = new List<BruPair>();
        while (true)
        {
            s.SkipBlankLinesAndWhitespace();
            if (s.AtEnd)
                throw s.Error($"Unterminated dict block '{blockName}'");
            if (s.Current == '}')
            {
                s.Advance();
                return pairs;
            }

            var annotations = ReadAnnotations(s);
            s.SkipBlankLinesAndWhitespace();

            // After annotations, we may have closing brace.
            if (s.Current == '}')
            {
                s.Advance();
                if (annotations.Count > 0)
                {
                    // Trailing annotations with no pair — preserve as a synthetic empty pair? Bruno drops them.
                    // We choose to drop them to match Bruno's behavior.
                }
                return pairs;
            }

            pairs.Add(ReadPair(s, annotations));
        }
    }

    private static IReadOnlyList<BruAnnotation> ReadAnnotations(Scanner s)
    {
        List<BruAnnotation>? list = null;
        while (true)
        {
            s.SkipHorizontalWs();
            if (s.Current != '@') return (IReadOnlyList<BruAnnotation>?)list ?? Array.Empty<BruAnnotation>();

            // Speculative parse: @name(args?) followed by NOT ':' (per Bruno's "annotationentry = ... ~':'").
            var checkpoint = s.Position;
            s.Advance(); // '@'
            var name = s.ReadWhile(IsAnnotationNameChar);
            if (name.Length == 0)
            {
                s.Position = checkpoint;
                return (IReadOnlyList<BruAnnotation>?)list ?? Array.Empty<BruAnnotation>();
            }

            string? args = null;
            if (s.Current == '(')
            {
                args = ReadAnnotationArgs(s);
            }

            s.SkipHorizontalWs();
            // Disambiguate: if next is ':', this was actually a key starting with '@'.
            if (s.Current == ':')
            {
                s.Position = checkpoint;
                return (IReadOnlyList<BruAnnotation>?)list ?? Array.Empty<BruAnnotation>();
            }

            // Must end with newline (or EOF / '}').
            if (!s.TryConsumeNewline() && s.Current != '}' && !s.AtEnd)
            {
                s.Position = checkpoint;
                return (IReadOnlyList<BruAnnotation>?)list ?? Array.Empty<BruAnnotation>();
            }

            list ??= new List<BruAnnotation>();
            list.Add(new BruAnnotation(name, args));
        }
    }

    /// <summary>Reads "(...)" for an annotation. Supports single-quoted, double-quoted, and unquoted args.</summary>
    private static string ReadAnnotationArgs(Scanner s)
    {
        s.Expect('(', "Expected '(' for annotation args");
        var start = s.Position;

        // Special case: triple-quote multiline arg.
        if (s.StartsWith("'''"))
        {
            s.Advance(3);
            while (!s.AtEnd && !s.StartsWith("'''")) s.Advance();
            if (!s.StartsWith("'''")) throw s.Error("Unterminated multiline arg in annotation");
            s.Advance(3);
            // Then expect ')'
            var raw = s.Substring(start, s.Position - start);
            s.Expect(')', "Expected ')' after annotation multiline arg");
            return raw;
        }

        // Single or double-quoted: scan until matching quote (allowing nothing inside, since Bruno's
        // grammar says "annotationsinglequotedargchar = ~'\\''" — no escaping).
        if (s.Current == '\'' || s.Current == '"')
        {
            var quote = s.Current;
            s.Advance();
            while (!s.AtEnd && s.Current != quote) s.Advance();
            if (s.AtEnd) throw s.Error($"Unterminated quoted annotation arg");
            s.Advance(); // closing quote
            var raw = s.Substring(start, s.Position - start);
            s.Expect(')', "Expected ')' after annotation quoted arg");
            return raw;
        }

        // Unquoted: anything until ')'
        while (!s.AtEnd && s.Current != ')') s.Advance();
        if (s.AtEnd) throw s.Error("Unterminated annotation args");
        var rawText = s.Substring(start, s.Position - start);
        s.Advance(); // ')'
        return rawText;
    }

    private static BruPair ReadPair(Scanner s, IReadOnlyList<BruAnnotation> annotations)
    {
        var enabled = true;
        if (s.Current == '~')
        {
            enabled = false;
            s.Advance();
        }

        string name;
        if (s.Current == '"')
        {
            name = ReadQuotedKey(s);
        }
        else
        {
            name = s.ReadWhile(IsKeyChar);
            if (name.Length == 0)
                throw s.Error("Expected pair key");
        }

        s.SkipHorizontalWs();
        s.Expect(':', $"Expected ':' after key '{name}'");
        s.SkipHorizontalWs();

        var value = ReadValue(s);

        // Trailing horizontal ws + optional newline
        s.SkipHorizontalWs();
        s.TryConsumeNewline();

        return new BruPair(name, value, enabled,
            annotations.Count == 0 ? null : annotations);
    }

    private static string ReadQuotedKey(Scanner s)
    {
        s.Expect('"', "Expected '\"' for quoted key");
        var sb = new System.Text.StringBuilder();
        while (!s.AtEnd)
        {
            if (s.Current == '\\' && s.Peek(1) == '"')
            {
                sb.Append('"');
                s.Advance(2);
                continue;
            }
            if (s.Current == '"')
            {
                s.Advance();
                return sb.ToString();
            }
            if (s.Current == '\n' || s.Current == '\r')
                throw s.Error("Newline in quoted key");
            sb.Append(s.Current);
            s.Advance();
        }
        throw s.Error("Unterminated quoted key");
    }

    private static BruValue ReadValue(Scanner s)
    {
        if (s.Current == '[') return ReadListValue(s);
        if (s.StartsWith("'''")) return ReadMultilineValue(s);

        // Single-line: read to end of line (excluding "\n}" terminator)
        var start = s.Position;
        while (!s.AtEnd && s.Current != '\n' && s.Current != '\r')
        {
            if (s.Current == '\n' && s.Peek(1) == '}') break;
            s.Advance();
        }
        var text = s.Substring(start, s.Position - start).TrimEnd(' ', '\t');
        return new StringValue(text);
    }

    private static ListValue ReadListValue(Scanner s)
    {
        s.Expect('[', "Expected '[' to start list value");
        var items = new List<string>();
        while (true)
        {
            // Lists allow blank lines; skip them.
            while (!s.AtEnd && (s.Current == ' ' || s.Current == '\t' || s.Current == '\n' || s.Current == '\r'))
                s.Advance();

            if (s.AtEnd)
                throw s.Error("Unterminated list value");

            if (s.Current == ']')
            {
                s.Advance();
                return new ListValue(items);
            }

            // listitem = alnum | "_" | "-"
            var item = s.ReadWhile(IsListItemChar);
            if (item.Length == 0)
                throw s.Error($"Unexpected '{s.Current}' in list — items must be alnum/_/-");
            items.Add(item);
        }
    }

    private static MultilineValue ReadMultilineValue(Scanner s)
    {
        if (!s.StartsWith("'''")) throw s.Error("Expected '''");
        s.Advance(3);
        var start = s.Position;
        while (!s.AtEnd && !s.StartsWith("'''")) s.Advance();
        if (!s.StartsWith("'''"))
            throw s.Error("Unterminated multiline value");
        var text = s.Substring(start, s.Position - start);
        s.Advance(3);

        // Optional " @contentType(...)"
        s.SkipHorizontalWs();
        string? contentType = null;
        if (s.StartsWith("@contentType("))
        {
            s.Advance("@contentType(".Length);
            var ctStart = s.Position;
            while (!s.AtEnd && s.Current != ')') s.Advance();
            if (s.AtEnd) throw s.Error("Unterminated @contentType(...)");
            contentType = s.Substring(ctStart, s.Position - ctStart);
            s.Advance(); // ')'
        }
        return new MultilineValue(text, contentType);
    }

    // ============================== Char predicates ==============================

    private static bool IsBlockNameChar(char c) =>
        char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ':';

    private static bool IsAnnotationNameChar(char c) =>
        c is not ('(' or ')' or ' ' or '\t' or '\r' or '\n' or ':');

    private static bool IsKeyChar(char c) =>
        c is not (':' or ' ' or '\t' or '\r' or '\n' or '}');

    private static bool IsListItemChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '-';
}

public sealed class BruParseException(string message) : Exception(message);

// ============================== Scanner ==============================

internal sealed class Scanner
{
    private readonly string _input;

    public Scanner(string input)
    {
        _input = input;
        Position = 0;
    }

    public int Position { get; set; }
    public bool AtEnd => Position >= _input.Length;
    public char Current => Position < _input.Length ? _input[Position] : '\0';

    public char Peek(int offset)
    {
        var p = Position + offset;
        return p < _input.Length ? _input[p] : '\0';
    }

    public void Advance(int count = 1) => Position = Math.Min(Position + count, _input.Length);

    public bool StartsWith(string s) =>
        Position + s.Length <= _input.Length &&
        string.CompareOrdinal(_input, Position, s, 0, s.Length) == 0;

    public string Substring(int start, int length) =>
        _input.Substring(start, Math.Min(length, _input.Length - start));

    public string ReadWhile(Func<char, bool> predicate)
    {
        var start = Position;
        while (!AtEnd && predicate(_input[Position])) Position++;
        return _input.Substring(start, Position - start);
    }

    public void SkipHorizontalWs()
    {
        while (!AtEnd && (_input[Position] == ' ' || _input[Position] == '\t')) Position++;
    }

    public void SkipBlankLinesAndWhitespace()
    {
        while (!AtEnd && (_input[Position] is ' ' or '\t' or '\r' or '\n')) Position++;
    }

    public bool TryConsumeNewline()
    {
        if (AtEnd) return false;
        if (_input[Position] == '\r' && Peek(1) == '\n') { Advance(2); return true; }
        if (_input[Position] == '\n') { Advance(); return true; }
        return false;
    }

    public void Expect(char c, string message)
    {
        if (AtEnd || _input[Position] != c) throw Error(message);
        Position++;
    }

    public BruParseException Error(string message, int? at = null)
    {
        var pos = at ?? Position;
        var line = 1;
        var col = 1;
        for (var i = 0; i < pos && i < _input.Length; i++)
        {
            if (_input[i] == '\n') { line++; col = 1; }
            else col++;
        }
        return new BruParseException($"{message} at line {line}, col {col} (offset {pos})");
    }
}
