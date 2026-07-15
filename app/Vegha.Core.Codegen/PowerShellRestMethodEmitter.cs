using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits a PowerShell snippet using Invoke-RestMethod.</summary>
public sealed class PowerShellRestMethodEmitter : ICodegenEmitter
{
    public string Language => "powershell";
    public string DisplayName => "PowerShell - RestMethod";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        var headers = ctx.HeadersWithContentType();
        if (headers.Count > 0)
        {
            sb.AppendLine("$headers = New-Object \"System.Collections.Generic.Dictionary[[String],[String]]\"");
            foreach (var h in headers)
                sb.AppendLine($"$headers.Add({PsSingle(h.Name)}, {PsSingle(h.Value)})");
            sb.AppendLine();
        }
        if (ctx.Body is not null)
        {
            // Single-quoted here-string: no interpolation, body pastes through verbatim.
            sb.AppendLine("$body = @'");
            sb.AppendLine(ctx.Body);
            sb.AppendLine("'@");
            sb.AppendLine();
        }
        sb.Append($"$response = Invoke-RestMethod {PsSingle(ctx.Url)} -Method {PsSingle(ctx.Method)}");
        if (headers.Count > 0) sb.Append(" -Headers $headers");
        if (ctx.Body is not null) sb.Append(" -Body $body");
        sb.AppendLine();
        sb.Append("$response | ConvertTo-Json");
        return sb.ToString();
    }

    /// <summary>PowerShell single-quoted literal — embedded ' doubles.</summary>
    private static string PsSingle(string s) => "'" + s.Replace("'", "''") + "'";
}
