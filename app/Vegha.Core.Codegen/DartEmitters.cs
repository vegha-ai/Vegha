using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits a Dart snippet using the dio package.</summary>
public sealed class DartDioEmitter : ICodegenEmitter
{
    public string Language => "dart";
    public string DisplayName => "Dart - dio";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        var headers = ctx.HeadersWithContentType();
        if (headers.Count > 0)
        {
            sb.AppendLine("var headers = {");
            foreach (var h in headers)
                sb.AppendLine($"  {CodegenText.Json(h.Name)}: {CodegenText.Json(h.Value)},");
            sb.AppendLine("};");
        }
        if (ctx.Body is not null)
            sb.AppendLine($"var data = {CodegenText.Json(ctx.Body)};");

        sb.AppendLine("var dio = Dio();");
        sb.AppendLine("var response = await dio.request(");
        sb.AppendLine($"  {CodegenText.Json(ctx.Url)},");
        sb.Append("  options: Options(").Append($"method: {CodegenText.Json(ctx.Method)}");
        if (headers.Count > 0) sb.Append(", headers: headers");
        sb.AppendLine("),");
        if (ctx.Body is not null) sb.AppendLine("  data: data,");
        sb.AppendLine(");");
        sb.AppendLine();
        sb.AppendLine("if (response.statusCode == 200) {");
        sb.AppendLine("  print(json.encode(response.data));");
        sb.AppendLine("}");
        sb.AppendLine("else {");
        sb.AppendLine("  print(response.statusMessage);");
        sb.Append('}');
        return sb.ToString();
    }
}

/// <summary>Emits a Dart snippet using the http package.</summary>
public sealed class DartHttpEmitter : ICodegenEmitter
{
    public string Language => "dart";
    public string DisplayName => "Dart - http";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        var headers = ctx.HeadersWithContentType();
        if (headers.Count > 0)
        {
            sb.AppendLine("var headers = {");
            foreach (var h in headers)
                sb.AppendLine($"  {CodegenText.Json(h.Name)}: {CodegenText.Json(h.Value)},");
            sb.AppendLine("};");
        }
        sb.AppendLine($"var request = http.Request({CodegenText.Json(ctx.Method)}, Uri.parse({CodegenText.Json(ctx.Url)}));");
        if (ctx.Body is not null)
            sb.AppendLine($"request.body = {CodegenText.Json(ctx.Body)};");
        if (headers.Count > 0)
            sb.AppendLine("request.headers.addAll(headers);");
        sb.AppendLine();
        sb.AppendLine("http.StreamedResponse response = await request.send();");
        sb.AppendLine();
        sb.AppendLine("if (response.statusCode == 200) {");
        sb.AppendLine("  print(await response.stream.bytesToString());");
        sb.AppendLine("}");
        sb.AppendLine("else {");
        sb.AppendLine("  print(response.reasonPhrase);");
        sb.Append('}');
        return sb.ToString();
    }
}
