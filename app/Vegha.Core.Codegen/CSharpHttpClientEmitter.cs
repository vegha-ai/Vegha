using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits a C# HttpClient snippet using HttpRequestMessage.</summary>
public sealed class CSharpHttpClientEmitter : ICodegenEmitter
{
    public string Language => "csharp";
    public string DisplayName => "C# - HttpClient";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine("using var client = new HttpClient();");
        sb.AppendLine($"using var request = new HttpRequestMessage(HttpMethod.{Pascal(ctx.Method)}, {CSharpString(ctx.Url)});");

        foreach (var h in ctx.Headers)
        {
            // Content-Type goes onto Content; other Content-* headers similarly. Treat all here as request-level
            // for simplicity; users can move them if needed.
            if (h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            sb.AppendLine($"request.Headers.TryAddWithoutValidation({CSharpString(h.Name)}, {CSharpString(h.Value)});");
        }

        if (ctx.Body is not null)
        {
            var ct = ctx.ContentType ?? "text/plain";
            sb.AppendLine($"request.Content = new StringContent({CSharpString(ctx.Body)}, System.Text.Encoding.UTF8, {CSharpString(ct)});");
        }

        sb.AppendLine();
        sb.AppendLine("using var response = await client.SendAsync(request);");
        sb.AppendLine("var body = await response.Content.ReadAsStringAsync();");
        sb.Append("Console.WriteLine($\"{(int)response.StatusCode} {response.ReasonPhrase}: {body}\");");
        return sb.ToString();
    }

    private static string Pascal(string method)
    {
        if (string.IsNullOrEmpty(method)) return "Get";
        var lower = method.ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower[1..];
    }

    private static string CSharpString(string s)
    {
        // Verbatim string with embedded "" escape — easier than escaping every backslash.
        return "@\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
