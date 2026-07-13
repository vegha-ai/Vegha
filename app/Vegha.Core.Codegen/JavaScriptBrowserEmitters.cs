using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits a browser JavaScript snippet using jQuery's $.ajax.</summary>
public sealed class JavaScriptJQueryEmitter : ICodegenEmitter
{
    public string Language => "javascript";
    public string DisplayName => "JavaScript - jQuery";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine("var settings = {");
        sb.AppendLine($"  \"url\": {CodegenText.Json(ctx.Url)},");
        sb.AppendLine($"  \"method\": {CodegenText.Json(ctx.Method)},");
        sb.AppendLine("  \"timeout\": 0,");
        var headers = ctx.HeadersWithContentType();
        if (headers.Count > 0)
        {
            sb.AppendLine("  \"headers\": {");
            foreach (var h in headers)
                sb.AppendLine($"    {CodegenText.Json(h.Name)}: {CodegenText.Json(h.Value)},");
            sb.AppendLine("  },");
        }
        if (ctx.Body is not null)
            sb.AppendLine($"  \"data\": {CodegenText.Json(ctx.Body)},");
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine("$.ajax(settings).done(function (response) {");
        sb.AppendLine("  console.log(response);");
        sb.Append("});");
        return sb.ToString();
    }
}

/// <summary>Emits a browser JavaScript snippet using XMLHttpRequest.</summary>
public sealed class JavaScriptXhrEmitter : ICodegenEmitter
{
    public string Language => "javascript";
    public string DisplayName => "JavaScript - XHR";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        if (ctx.Body is not null)
        {
            sb.AppendLine($"var data = {CodegenText.Json(ctx.Body)};");
            sb.AppendLine();
        }
        sb.AppendLine("var xhr = new XMLHttpRequest();");
        sb.AppendLine();
        sb.AppendLine("xhr.addEventListener(\"readystatechange\", function () {");
        sb.AppendLine("  if (this.readyState === 4) {");
        sb.AppendLine("    console.log(this.responseText);");
        sb.AppendLine("  }");
        sb.AppendLine("});");
        sb.AppendLine();
        sb.AppendLine($"xhr.open({CodegenText.Json(ctx.Method)}, {CodegenText.Json(ctx.Url)});");
        foreach (var h in ctx.HeadersWithContentType())
            sb.AppendLine($"xhr.setRequestHeader({CodegenText.Json(h.Name)}, {CodegenText.Json(h.Value)});");
        sb.AppendLine();
        sb.Append(ctx.Body is not null ? "xhr.send(data);" : "xhr.send();");
        return sb.ToString();
    }
}
