using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits an HTTPie command line.</summary>
public sealed class ShellHttpieEmitter : ICodegenEmitter
{
    public string Language => "shell";
    public string DisplayName => "Shell - HTTPie";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        if (ctx.Body is not null)
            sb.Append("printf ").Append(CodegenText.ShellSingle(ctx.Body)).Append(" | ");
        sb.Append("http --follow ").Append(ctx.Method).Append(' ').Append(CodegenText.ShellSingle(ctx.Url));
        foreach (var h in ctx.HeadersWithContentType())
        {
            sb.AppendLine(" \\");
            sb.Append(' ').Append(h.Name).Append(':').Append(CodegenText.ShellSingle(h.Value));
        }
        return sb.ToString();
    }
}

/// <summary>Emits a wget command line.</summary>
public sealed class ShellWgetEmitter : ICodegenEmitter
{
    public string Language => "shell";
    public string DisplayName => "Shell - wget";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine("wget --no-check-certificate --quiet \\");
        sb.Append("  --method ").Append(ctx.Method).AppendLine(" \\");
        sb.AppendLine("  --timeout=0 \\");
        foreach (var h in ctx.HeadersWithContentType())
        {
            sb.Append("  --header ").Append(CodegenText.ShellSingle($"{h.Name}: {h.Value}")).AppendLine(" \\");
        }
        if (ctx.Body is not null)
            sb.Append("  --body-data ").Append(CodegenText.ShellSingle(ctx.Body)).AppendLine(" \\");
        sb.Append("  -O - ").Append(CodegenText.ShellSingle(ctx.Url));
        return sb.ToString();
    }
}
