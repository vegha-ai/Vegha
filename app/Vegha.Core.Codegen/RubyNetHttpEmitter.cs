using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits a Ruby snippet using Net::HTTP.</summary>
public sealed class RubyNetHttpEmitter : ICodegenEmitter
{
    public string Language => "ruby";
    public string DisplayName => "Ruby - Net::HTTP";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var (scheme, _, _, _) = CodegenText.SplitUrl(ctx.Url);
        var sb = new StringBuilder();
        sb.AppendLine("require \"uri\"");
        sb.AppendLine("require \"net/http\"");
        sb.AppendLine();
        sb.AppendLine($"url = URI({CodegenText.RubyDouble(ctx.Url)})");
        sb.AppendLine();
        sb.AppendLine("http = Net::HTTP.new(url.host, url.port)");
        if (scheme == "https")
            sb.AppendLine("http.use_ssl = true");
        sb.AppendLine();
        sb.AppendLine($"request = Net::HTTP::{MethodClass(ctx.Method)}.new(url)");
        foreach (var h in ctx.HeadersWithContentType())
            sb.AppendLine($"request[{CodegenText.RubyDouble(h.Name)}] = {CodegenText.RubyDouble(h.Value)}");
        if (ctx.Body is not null)
            sb.AppendLine($"request.body = {CodegenText.RubyDouble(ctx.Body)}");
        sb.AppendLine();
        sb.AppendLine("response = http.request(request)");
        sb.Append("puts response.read_body");
        return sb.ToString();
    }

    private static string MethodClass(string method)
    {
        var lower = method.ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower[1..];
    }
}
