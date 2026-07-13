using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits a Swift snippet using URLSession.</summary>
public sealed class SwiftUrlSessionEmitter : ICodegenEmitter
{
    public string Language => "swift";
    public string DisplayName => "Swift - URLSession";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine($"var request = URLRequest(url: URL(string: {CodegenText.Json(ctx.Url)})!, timeoutInterval: Double.infinity)");
        foreach (var h in ctx.HeadersWithContentType())
            sb.AppendLine($"request.addValue({CodegenText.Json(h.Value)}, forHTTPHeaderField: {CodegenText.Json(h.Name)})");
        sb.AppendLine();
        sb.AppendLine($"request.httpMethod = {CodegenText.Json(ctx.Method)}");
        if (ctx.Body is not null)
            sb.AppendLine($"request.httpBody = {CodegenText.Json(ctx.Body)}.data(using: .utf8)");
        sb.AppendLine();
        sb.AppendLine("let task = URLSession.shared.dataTask(with: request) { data, response, error in");
        sb.AppendLine("  guard let data = data else {");
        sb.AppendLine("    print(String(describing: error))");
        sb.AppendLine("    return");
        sb.AppendLine("  }");
        sb.AppendLine("  print(String(data: data, encoding: .utf8)!)");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.Append("task.resume()");
        return sb.ToString();
    }
}
