using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits an R snippet using the httr package.</summary>
public sealed class RHttrEmitter : ICodegenEmitter
{
    public string Language => "r";
    public string DisplayName => "R - httr";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine("library(httr)");
        sb.AppendLine();
        var headers = ctx.HeadersWithContentType();
        if (headers.Count > 0)
        {
            sb.AppendLine("headers = c(");
            for (var i = 0; i < headers.Count; i++)
            {
                sb.Append($"  {CodegenText.Json(headers[i].Name)} = {CodegenText.Json(headers[i].Value)}");
                sb.AppendLine(i < headers.Count - 1 ? "," : "");
            }
            sb.AppendLine(")");
            sb.AppendLine();
        }
        if (ctx.Body is not null)
        {
            sb.AppendLine($"body = {CodegenText.Json(ctx.Body)}");
            sb.AppendLine();
        }
        sb.Append($"res <- VERB({CodegenText.Json(ctx.Method)}, url = {CodegenText.Json(ctx.Url)}");
        if (ctx.Body is not null) sb.Append(", body = body");
        if (headers.Count > 0) sb.Append(", add_headers(.headers=headers)");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.Append("cat(content(res, 'text'))");
        return sb.ToString();
    }
}

/// <summary>Emits an R snippet using the RCurl package.</summary>
public sealed class RRCurlEmitter : ICodegenEmitter
{
    public string Language => "r";
    public string DisplayName => "R - RCurl";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine("library(RCurl)");
        var headers = ctx.HeadersWithContentType();
        if (headers.Count > 0)
        {
            sb.AppendLine("headers = c(");
            for (var i = 0; i < headers.Count; i++)
            {
                sb.Append($"  {CodegenText.Json(headers[i].Name)} = {CodegenText.Json(headers[i].Value)}");
                sb.AppendLine(i < headers.Count - 1 ? "," : "");
            }
            sb.AppendLine(")");
        }
        if (ctx.Body is not null)
        {
            sb.AppendLine($"params = {CodegenText.Json(ctx.Body)}");
            sb.Append($"res <- postForm({CodegenText.Json(ctx.Url)}, .opts=list(postfields = params");
            if (headers.Count > 0) sb.Append(", httpheader = headers");
            sb.AppendLine(", followlocation = TRUE), style = \"httppost\")");
        }
        else
        {
            sb.Append($"res <- getURL({CodegenText.Json(ctx.Url)}, .opts=list(");
            if (headers.Count > 0) sb.Append("httpheader = headers, ");
            sb.AppendLine("followlocation = TRUE))");
        }
        sb.Append("cat(res)");
        return sb.ToString();
    }
}
