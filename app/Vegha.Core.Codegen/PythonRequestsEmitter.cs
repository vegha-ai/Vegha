using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits a Python <c>requests</c> snippet.</summary>
public sealed class PythonRequestsEmitter : ICodegenEmitter
{
    public string Language => "python";
    public string DisplayName => "Python - Requests";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine("import requests");
        sb.AppendLine();
        sb.Append("response = requests.").Append(ctx.Method.ToLowerInvariant()).Append('(');
        sb.AppendLine();
        sb.Append("    url=").Append(PyStr(ctx.Url));

        var allHeaders = ctx.Headers.ToList();
        if (ctx.ContentType is not null && !allHeaders.Any(h =>
                string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase)))
        {
            allHeaders.Add(new KvPair("Content-Type", ctx.ContentType, true));
        }
        if (allHeaders.Count > 0)
        {
            sb.AppendLine(",");
            sb.AppendLine("    headers={");
            for (var i = 0; i < allHeaders.Count; i++)
            {
                var h = allHeaders[i];
                sb.Append("        ")
                  .Append(PyStr(h.Name))
                  .Append(": ")
                  .Append(PyStr(h.Value));
                if (i < allHeaders.Count - 1) sb.Append(',');
                sb.AppendLine();
            }
            sb.Append("    }");
        }

        if (ctx.Body is not null)
        {
            sb.AppendLine(",");
            sb.Append("    data=").Append(PyStr(ctx.Body));
        }

        sb.AppendLine();
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("print(response.status_code)");
        sb.Append("print(response.text)");
        return sb.ToString();
    }

    /// <summary>Produce a Python string literal — JSON-encoded works (Python accepts JSON strings as Python strings).</summary>
    private static string PyStr(string s) => JsonSerializer.Serialize(s, s_jsonOptions);
}
