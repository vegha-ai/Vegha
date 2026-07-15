using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits the raw HTTP/1.1 message for the request.</summary>
public sealed class HttpRawEmitter : ICodegenEmitter
{
    public string Language => "http";
    public string DisplayName => "HTTP";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var (_, host, port, pathAndQuery) = CodegenText.SplitUrl(ctx.Url);
        var sb = new StringBuilder();
        sb.Append(ctx.Method).Append(' ').Append(pathAndQuery).AppendLine(" HTTP/1.1");
        sb.Append("Host: ").AppendLine(port is null ? host : $"{host}:{port}");
        foreach (var h in ctx.HeadersWithContentType())
            sb.Append(h.Name).Append(": ").AppendLine(h.Value);
        if (ctx.Body is not null)
        {
            sb.Append("Content-Length: ")
              .AppendLine(Encoding.UTF8.GetByteCount(ctx.Body).ToString());
            sb.AppendLine();
            sb.Append(ctx.Body);
        }
        return sb.ToString();
    }
}
