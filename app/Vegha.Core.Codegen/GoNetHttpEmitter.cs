using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits idiomatic Go using <c>net/http</c>. Body is sent as a string Reader;
/// errors are returned via the standard <c>err != nil</c> guard pattern.</summary>
public sealed class GoNetHttpEmitter : ICodegenEmitter
{
    public string Language => "go";
    public string DisplayName => "Go (net/http)";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();

        sb.AppendLine("package main");
        sb.AppendLine();
        sb.AppendLine("import (");
        sb.AppendLine("\t\"fmt\"");
        sb.AppendLine("\t\"io\"");
        sb.AppendLine("\t\"net/http\"");
        if (ctx.Body is not null) sb.AppendLine("\t\"strings\"");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("func main() {");

        if (ctx.Body is not null)
        {
            sb.AppendLine($"\tbody := strings.NewReader({GoString(ctx.Body)})");
            sb.AppendLine($"\treq, err := http.NewRequest({GoString(ctx.Method)}, {GoString(ctx.Url)}, body)");
        }
        else
        {
            sb.AppendLine($"\treq, err := http.NewRequest({GoString(ctx.Method)}, {GoString(ctx.Url)}, nil)");
        }
        sb.AppendLine("\tif err != nil { panic(err) }");

        foreach (var h in ctx.Headers)
            sb.AppendLine($"\treq.Header.Set({GoString(h.Name)}, {GoString(h.Value)})");
        if (ctx.ContentType is not null && !ctx.Headers.Any(h => string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine($"\treq.Header.Set(\"Content-Type\", {GoString(ctx.ContentType)})");

        sb.AppendLine();
        sb.AppendLine("\tresp, err := http.DefaultClient.Do(req)");
        sb.AppendLine("\tif err != nil { panic(err) }");
        sb.AppendLine("\tdefer resp.Body.Close()");
        sb.AppendLine();
        sb.AppendLine("\tdata, _ := io.ReadAll(resp.Body)");
        sb.AppendLine("\tfmt.Println(resp.Status)");
        sb.AppendLine("\tfmt.Println(string(data))");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GoString(string s) =>
        "\"" + s
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t") + "\"";
}
