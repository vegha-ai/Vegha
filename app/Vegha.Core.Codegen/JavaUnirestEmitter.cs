using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits a Java snippet using the Unirest client.</summary>
public sealed class JavaUnirestEmitter : ICodegenEmitter
{
    public string Language => "java";
    public string DisplayName => "Java - Unirest";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine("HttpResponse<String> response = Unirest");
        sb.AppendLine($"  .{MethodCall(ctx.Method)}({CodegenText.Json(ctx.Url)})");
        foreach (var h in ctx.HeadersWithContentType())
            sb.AppendLine($"  .header({CodegenText.Json(h.Name)}, {CodegenText.Json(h.Value)})");
        if (ctx.Body is not null)
            sb.AppendLine($"  .body({CodegenText.Json(ctx.Body)})");
        sb.AppendLine("  .asString();");
        sb.Append("System.out.println(response.getBody());");
        return sb.ToString();
    }

    private static string MethodCall(string method) => method.ToUpperInvariant() switch
    {
        "GET" => "get",
        "POST" => "post",
        "PUT" => "put",
        "PATCH" => "patch",
        "DELETE" => "delete",
        "HEAD" => "head",
        "OPTIONS" => "options",
        _ => "get",
    };
}
