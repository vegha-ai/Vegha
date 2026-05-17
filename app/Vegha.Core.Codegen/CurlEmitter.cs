using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits a multi-line <c>curl</c> command. Single-quoted args; escapes embedded quotes.</summary>
public sealed class CurlEmitter : ICodegenEmitter
{
    public string Language => "curl";
    public string DisplayName => "cURL";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.Append("curl --request ").Append(ctx.Method).AppendLine(" \\");
        sb.Append("  --url ").Append(SingleQuote(ctx.Url));

        foreach (var h in ctx.Headers)
        {
            sb.AppendLine(" \\");
            sb.Append("  --header ").Append(SingleQuote($"{h.Name}: {h.Value}"));
        }

        if (ctx.ContentType is not null && !ctx.Headers.Any(h =>
                string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase)))
        {
            sb.AppendLine(" \\");
            sb.Append("  --header ").Append(SingleQuote($"Content-Type: {ctx.ContentType}"));
        }

        if (ctx.Body is not null)
        {
            sb.AppendLine(" \\");
            sb.Append("  --data ").Append(SingleQuote(ctx.Body));
        }

        return sb.ToString();
    }

    private static string SingleQuote(string s) =>
        // Replace ' with '\'' to escape inside a single-quoted shell string.
        "'" + s.Replace("'", "'\\''") + "'";
}
