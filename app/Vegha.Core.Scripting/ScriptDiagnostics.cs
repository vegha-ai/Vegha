using Acornima;

namespace Vegha.Core.Scripting;

/// <summary>A single syntax problem found in a script.</summary>
/// <param name="Offset">Zero-based absolute character offset into the source (maps directly to an
/// editor document offset).</param>
/// <param name="Length">Length of the squiggled span (at least 1).</param>
/// <param name="Line">1-based line number.</param>
/// <param name="Column">0-based column.</param>
/// <param name="Message">Human-readable error description.</param>
public sealed record ScriptDiagnostic(int Offset, int Length, int Line, int Column, string Message);

/// <summary>
/// Syntax-only analysis of user scripts using Acornima (the parser Jint already bundles).
/// Parses without executing, so it's safe to run on every keystroke (debounced by the editor).
/// Undeclared identifiers like <c>bru</c>/<c>res</c> are NOT syntax errors, so there are no
/// false positives for the host-injected globals — only genuine JavaScript syntax errors squiggle.
/// </summary>
public static class ScriptDiagnostics
{
    private static readonly IReadOnlyList<ScriptDiagnostic> None = Array.Empty<ScriptDiagnostic>();

    /// <summary>Returns syntax diagnostics for <paramref name="code"/>, or an empty list when it
    /// parses cleanly (or is empty). Never throws — parser failures degrade to "no diagnostics".</summary>
    public static IReadOnlyList<ScriptDiagnostic> Analyze(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return None;

        try
        {
            // Non-strict script parse mirrors how Bruno/Postman scripts run.
            new Parser().ParseScript(code, sourceFile: null, strict: false);
            return None;
        }
        catch (ParseErrorException ex)
        {
            var err = ex.Error;
            var index = err.IsIndexDefined ? err.Index : 0;
            var offset = Math.Clamp(index, 0, code.Length);
            var length = TokenLengthAt(code, offset);
            var message = !string.IsNullOrEmpty(err.Description) ? err.Description : ex.Message;
            // err.LineNumber is 1-based, err.Column is 0-based.
            return new[] { new ScriptDiagnostic(offset, length, err.LineNumber, err.Column, message) };
        }
        catch
        {
            // A parser bug must never break typing — fail open with no squiggle.
            return None;
        }
    }

    /// <summary>Length of the token starting at <paramref name="offset"/> — runs to the next
    /// whitespace or end-of-text so the squiggle covers something visible (min length 1).</summary>
    private static int TokenLengthAt(string code, int offset)
    {
        if (offset >= code.Length) return 1;
        var end = offset;
        while (end < code.Length && !char.IsWhiteSpace(code[end])) end++;
        return Math.Max(1, end - offset);
    }
}
