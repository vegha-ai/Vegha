using GraphQLParser;
using GraphQLParser.Exceptions;
using GraphQLParser.Visitors;

namespace Vegha.Core.GraphQL;

/// <summary>
/// Pretty-prints GraphQL documents (2-space indent, matching the app's editor tab size).
/// Input that doesn't parse is returned unchanged — Prettify must never destroy the
/// user's half-written query.
/// </summary>
public static class GraphQLFormatter
{
    private static readonly SDLPrinter Printer = new(new SDLPrinterOptions
    {
        PrintComments = true,
    });

    public static string Prettify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
        // Parse the ORIGINAL text (not masked): {{var}} tokens inside a structurally valid
        // document would round-trip as garbage identifiers if masked. If the document only
        // parses when masked, formatting is skipped — better unformatted than corrupted.
        try
        {
            var doc = Parser.Parse(text);
            return Printer.Print(doc).TrimEnd() + Environment.NewLine;
        }
        catch (GraphQLSyntaxErrorException)
        {
            return text;
        }
        catch (Exception)
        {
            return text;
        }
    }
}
