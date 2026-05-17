using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits a JavaScript fetch() call. Browser/Node compatible.</summary>
public sealed class JavaScriptFetchEmitter : ICodegenEmitter
{
    public string Language => "javascript";
    public string DisplayName => "JavaScript (fetch)";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();

        sb.Append("const response = await fetch(").Append(JsonSerializer.Serialize(ctx.Url, s_jsonOptions)).Append(", {");
        sb.AppendLine();
        sb.Append("  method: ").Append(JsonSerializer.Serialize(ctx.Method, s_jsonOptions));

        var allHeaders = ctx.Headers.ToList();
        if (ctx.ContentType is not null && !allHeaders.Any(h =>
                string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase)))
        {
            allHeaders.Add(new KvPair("Content-Type", ctx.ContentType, true));
        }

        if (allHeaders.Count > 0)
        {
            sb.AppendLine(",");
            sb.AppendLine("  headers: {");
            for (var i = 0; i < allHeaders.Count; i++)
            {
                var h = allHeaders[i];
                sb.Append("    ")
                  .Append(JsonSerializer.Serialize(h.Name, s_jsonOptions))
                  .Append(": ")
                  .Append(JsonSerializer.Serialize(h.Value, s_jsonOptions));
                if (i < allHeaders.Count - 1) sb.Append(',');
                sb.AppendLine();
            }
            sb.Append("  }");
        }

        if (ctx.Body is not null)
        {
            sb.AppendLine(",");
            sb.Append("  body: ").Append(JsonSerializer.Serialize(ctx.Body, s_jsonOptions));
        }

        sb.AppendLine();
        sb.AppendLine("});");
        sb.AppendLine();
        sb.Append("const data = await response.text();");
        return sb.ToString();
    }
}
