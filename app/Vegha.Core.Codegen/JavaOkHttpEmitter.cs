using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits Java using OkHttp 4.x. Single-class snippet with a <c>main</c> method;
/// the user pastes it into a project that already has the OkHttp dependency.</summary>
public sealed class JavaOkHttpEmitter : ICodegenEmitter
{
    public string Language => "java";
    public string DisplayName => "Java - OkHttp";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();

        sb.AppendLine("import okhttp3.*;");
        sb.AppendLine();
        sb.AppendLine("public class ApiCall {");
        sb.AppendLine("    public static void main(String[] args) throws Exception {");
        sb.AppendLine("        OkHttpClient client = new OkHttpClient();");
        sb.AppendLine();

        var bodyExpr = "null";
        if (ctx.Body is not null)
        {
            var mediaType = ctx.ContentType ?? "application/octet-stream";
            sb.AppendLine($"        MediaType MEDIA = MediaType.parse({JavaString(mediaType)});");
            sb.AppendLine($"        RequestBody body = RequestBody.create({JavaString(ctx.Body)}, MEDIA);");
            bodyExpr = "body";
        }

        sb.AppendLine("        Request.Builder builder = new Request.Builder()");
        sb.AppendLine($"            .url({JavaString(ctx.Url)})");
        sb.AppendLine($"            .method({JavaString(ctx.Method)}, {bodyExpr});");
        foreach (var h in ctx.Headers)
            sb.AppendLine($"        builder.addHeader({JavaString(h.Name)}, {JavaString(h.Value)});");
        if (ctx.ContentType is not null && !ctx.Headers.Any(h => string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine($"        builder.addHeader(\"Content-Type\", {JavaString(ctx.ContentType)});");

        sb.AppendLine();
        sb.AppendLine("        try (Response response = client.newCall(builder.build()).execute()) {");
        sb.AppendLine("            System.out.println(response.code());");
        sb.AppendLine("            System.out.println(response.body() != null ? response.body().string() : \"\");");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string JavaString(string s) =>
        "\"" + s
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t") + "\"";
}
