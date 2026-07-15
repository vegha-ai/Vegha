using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits a Kotlin snippet using OkHttp.</summary>
public sealed class KotlinOkHttpEmitter : ICodegenEmitter
{
    public string Language => "kotlin";
    public string DisplayName => "Kotlin - OkHttp";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine("val client = OkHttpClient()");
        if (ctx.Body is not null)
        {
            sb.AppendLine($"val mediaType = {CodegenText.Json(ctx.ContentType ?? "text/plain")}.toMediaType()");
            sb.AppendLine($"val body = {CodegenText.Json(ctx.Body)}.toRequestBody(mediaType)");
        }
        sb.AppendLine("val request = Request.Builder()");
        sb.AppendLine($"  .url({CodegenText.Json(ctx.Url)})");
        sb.AppendLine(ctx.Body is not null
            ? $"  .method({CodegenText.Json(ctx.Method)}, body)"
            : $"  .method({CodegenText.Json(ctx.Method)}, null)");
        foreach (var h in ctx.HeadersWithContentType())
            sb.AppendLine($"  .addHeader({CodegenText.Json(h.Name)}, {CodegenText.Json(h.Value)})");
        sb.AppendLine("  .build()");
        sb.AppendLine("val response = client.newCall(request).execute()");
        sb.Append("println(response.body!!.string())");
        return sb.ToString();
    }
}
