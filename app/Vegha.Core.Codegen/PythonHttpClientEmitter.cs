using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits a Python snippet using the stdlib http.client module.</summary>
public sealed class PythonHttpClientEmitter : ICodegenEmitter
{
    public string Language => "python";
    public string DisplayName => "Python - http.client";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var (scheme, host, port, pathAndQuery) = CodegenText.SplitUrl(ctx.Url);
        var connClass = scheme == "http" ? "HTTPConnection" : "HTTPSConnection";
        var sb = new StringBuilder();
        sb.AppendLine("import http.client");
        sb.AppendLine();
        sb.Append($"conn = http.client.{connClass}({PyStr(host)}");
        if (port is not null) sb.Append($", {port}");
        sb.AppendLine(")");
        sb.AppendLine(ctx.Body is not null ? $"payload = {PyStr(ctx.Body)}" : "payload = ''");
        var headers = ctx.HeadersWithContentType();
        if (headers.Count > 0)
        {
            sb.AppendLine("headers = {");
            foreach (var h in headers)
                sb.AppendLine($"    {PyStr(h.Name)}: {PyStr(h.Value)},");
            sb.AppendLine("}");
        }
        else
        {
            sb.AppendLine("headers = {}");
        }
        sb.AppendLine($"conn.request({PyStr(ctx.Method)}, {PyStr(pathAndQuery)}, payload, headers)");
        sb.AppendLine("res = conn.getresponse()");
        sb.AppendLine("data = res.read()");
        sb.Append("print(data.decode(\"utf-8\"))");
        return sb.ToString();
    }

    private static string PyStr(string s) => JsonSerializer.Serialize(s, s_jsonOptions);
}
