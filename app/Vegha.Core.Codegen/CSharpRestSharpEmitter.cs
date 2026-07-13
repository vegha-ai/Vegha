using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits a C# snippet using RestSharp.</summary>
public sealed class CSharpRestSharpEmitter : ICodegenEmitter
{
    public string Language => "csharp";
    public string DisplayName => "C# - RestSharp";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine($"var client = new RestClient({Verbatim(ctx.Url)});");
        sb.AppendLine($"var request = new RestRequest(\"\", Method.{MethodName(ctx.Method)});");

        foreach (var h in ctx.HeadersWithContentType())
            sb.AppendLine($"request.AddHeader({Verbatim(h.Name)}, {Verbatim(h.Value)});");

        if (ctx.Body is not null)
        {
            sb.AppendLine($"var body = {Verbatim(ctx.Body)};");
            sb.AppendLine($"request.AddStringBody(body, {Verbatim(ctx.ContentType ?? "text/plain")});");
        }

        sb.AppendLine("RestResponse response = await client.ExecuteAsync(request);");
        sb.Append("Console.WriteLine(response.Content);");
        return sb.ToString();
    }

    private static string MethodName(string method)
    {
        var lower = method.ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower[1..];
    }

    private static string Verbatim(string s) => "@\"" + s.Replace("\"", "\"\"") + "\"";
}
