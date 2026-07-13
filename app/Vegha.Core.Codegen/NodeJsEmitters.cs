using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits a Node.js snippet using axios.</summary>
public sealed class NodeJsAxiosEmitter : ICodegenEmitter
{
    public string Language => "javascript";
    public string DisplayName => "NodeJs - Axios";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine("const axios = require('axios');");
        if (ctx.Body is not null)
            sb.AppendLine($"let data = {CodegenText.Json(ctx.Body)};");
        sb.AppendLine();
        sb.AppendLine("let config = {");
        sb.AppendLine($"  method: {CodegenText.Json(ctx.Method.ToLowerInvariant())},");
        sb.AppendLine("  maxBodyLength: Infinity,");
        sb.AppendLine($"  url: {CodegenText.Json(ctx.Url)},");
        var headers = ctx.HeadersWithContentType();
        if (headers.Count > 0)
        {
            sb.AppendLine("  headers: {");
            foreach (var h in headers)
                sb.AppendLine($"    {CodegenText.Json(h.Name)}: {CodegenText.Json(h.Value)},");
            sb.AppendLine("  },");
        }
        if (ctx.Body is not null)
            sb.AppendLine("  data: data,");
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine("axios.request(config)");
        sb.AppendLine("  .then((response) => {");
        sb.AppendLine("    console.log(JSON.stringify(response.data));");
        sb.AppendLine("  })");
        sb.AppendLine("  .catch((error) => {");
        sb.AppendLine("    console.log(error);");
        sb.Append("  });");
        return sb.ToString();
    }
}

/// <summary>Emits a Node.js snippet using the built-in http/https modules.</summary>
public sealed class NodeJsNativeEmitter : ICodegenEmitter
{
    public string Language => "javascript";
    public string DisplayName => "NodeJs - Native";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var (scheme, host, port, pathAndQuery) = CodegenText.SplitUrl(ctx.Url);
        var module = scheme == "http" ? "http" : "https";
        var sb = new StringBuilder();
        sb.AppendLine($"var {module} = require('{module}');");
        sb.AppendLine();
        sb.AppendLine("var options = {");
        sb.AppendLine($"  'method': {CodegenText.Json(ctx.Method)},");
        sb.AppendLine($"  'hostname': {CodegenText.Json(host)},");
        if (port is not null)
            sb.AppendLine($"  'port': {port},");
        sb.AppendLine($"  'path': {CodegenText.Json(pathAndQuery)},");
        var headers = ctx.HeadersWithContentType();
        if (headers.Count > 0)
        {
            sb.AppendLine("  'headers': {");
            foreach (var h in headers)
                sb.AppendLine($"    {CodegenText.Json(h.Name)}: {CodegenText.Json(h.Value)},");
            sb.AppendLine("  },");
        }
        sb.AppendLine("  'maxRedirects': 20");
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine($"var req = {module}.request(options, function (res) {{");
        sb.AppendLine("  var chunks = [];");
        sb.AppendLine();
        sb.AppendLine("  res.on(\"data\", function (chunk) {");
        sb.AppendLine("    chunks.push(chunk);");
        sb.AppendLine("  });");
        sb.AppendLine();
        sb.AppendLine("  res.on(\"end\", function (chunk) {");
        sb.AppendLine("    var body = Buffer.concat(chunks);");
        sb.AppendLine("    console.log(body.toString());");
        sb.AppendLine("  });");
        sb.AppendLine();
        sb.AppendLine("  res.on(\"error\", function (error) {");
        sb.AppendLine("    console.error(error);");
        sb.AppendLine("  });");
        sb.AppendLine("});");
        sb.AppendLine();
        if (ctx.Body is not null)
        {
            sb.AppendLine($"var postData = {CodegenText.Json(ctx.Body)};");
            sb.AppendLine();
            sb.AppendLine("req.write(postData);");
            sb.AppendLine();
        }
        sb.Append("req.end();");
        return sb.ToString();
    }
}

/// <summary>Emits a Node.js snippet using the request package.</summary>
public sealed class NodeJsRequestEmitter : ICodegenEmitter
{
    public string Language => "javascript";
    public string DisplayName => "NodeJs - Request";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine("var request = require('request');");
        sb.AppendLine("var options = {");
        sb.AppendLine($"  'method': {CodegenText.Json(ctx.Method)},");
        sb.AppendLine($"  'url': {CodegenText.Json(ctx.Url)},");
        var headers = ctx.HeadersWithContentType();
        if (headers.Count > 0)
        {
            sb.AppendLine("  'headers': {");
            foreach (var h in headers)
                sb.AppendLine($"    {CodegenText.Json(h.Name)}: {CodegenText.Json(h.Value)},");
            sb.AppendLine("  },");
        }
        if (ctx.Body is not null)
            sb.AppendLine($"  body: {CodegenText.Json(ctx.Body)}");
        sb.AppendLine("};");
        sb.AppendLine("request(options, function (error, response) {");
        sb.AppendLine("  if (error) throw new Error(error);");
        sb.AppendLine("  console.log(response.body);");
        sb.Append("});");
        return sb.ToString();
    }
}

/// <summary>Emits a Node.js snippet using the unirest package.</summary>
public sealed class NodeJsUnirestEmitter : ICodegenEmitter
{
    public string Language => "javascript";
    public string DisplayName => "NodeJs - Unirest";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine("var unirest = require('unirest');");
        sb.AppendLine($"var req = unirest({CodegenText.Json(ctx.Method)}, {CodegenText.Json(ctx.Url)})");
        var headers = ctx.HeadersWithContentType();
        if (headers.Count > 0)
        {
            sb.AppendLine("  .headers({");
            foreach (var h in headers)
                sb.AppendLine($"    {CodegenText.Json(h.Name)}: {CodegenText.Json(h.Value)},");
            sb.AppendLine("  })");
        }
        if (ctx.Body is not null)
            sb.AppendLine($"  .send({CodegenText.Json(ctx.Body)})");
        sb.AppendLine("  .end(function (res) {");
        sb.AppendLine("    if (res.error) throw new Error(res.error);");
        sb.AppendLine("    console.log(res.raw_body);");
        sb.Append("  });");
        return sb.ToString();
    }
}
