using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits a PHP snippet using the cURL extension.</summary>
public sealed class PhpCurlEmitter : ICodegenEmitter
{
    public string Language => "php";
    public string DisplayName => "PHP - cURL";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine("<?php");
        sb.AppendLine();
        sb.AppendLine("$curl = curl_init();");
        sb.AppendLine();
        sb.AppendLine("curl_setopt_array($curl, array(");
        sb.AppendLine($"  CURLOPT_URL => {CodegenText.PhpSingle(ctx.Url)},");
        sb.AppendLine("  CURLOPT_RETURNTRANSFER => true,");
        sb.AppendLine("  CURLOPT_ENCODING => '',");
        sb.AppendLine("  CURLOPT_MAXREDIRS => 10,");
        sb.AppendLine("  CURLOPT_TIMEOUT => 0,");
        sb.AppendLine("  CURLOPT_FOLLOWLOCATION => true,");
        sb.AppendLine("  CURLOPT_HTTP_VERSION => CURL_HTTP_VERSION_1_1,");
        sb.AppendLine($"  CURLOPT_CUSTOMREQUEST => {CodegenText.PhpSingle(ctx.Method)},");
        if (ctx.Body is not null)
            sb.AppendLine($"  CURLOPT_POSTFIELDS => {CodegenText.PhpSingle(ctx.Body)},");
        var headers = ctx.HeadersWithContentType();
        if (headers.Count > 0)
        {
            sb.AppendLine("  CURLOPT_HTTPHEADER => array(");
            for (var i = 0; i < headers.Count; i++)
            {
                sb.Append("    ").Append(CodegenText.PhpSingle($"{headers[i].Name}: {headers[i].Value}"));
                sb.AppendLine(i < headers.Count - 1 ? "," : "");
            }
            sb.AppendLine("  ),");
        }
        sb.AppendLine("));");
        sb.AppendLine();
        sb.AppendLine("$response = curl_exec($curl);");
        sb.AppendLine();
        sb.AppendLine("curl_close($curl);");
        sb.Append("echo $response;");
        return sb.ToString();
    }
}

/// <summary>Emits a PHP snippet using Guzzle.</summary>
public sealed class PhpGuzzleEmitter : ICodegenEmitter
{
    public string Language => "php";
    public string DisplayName => "PHP - Guzzle";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine("<?php");
        sb.AppendLine("use GuzzleHttp\\Client;");
        sb.AppendLine("use GuzzleHttp\\Psr7\\Request;");
        sb.AppendLine();
        sb.AppendLine("$client = new Client();");
        var headers = ctx.HeadersWithContentType();
        if (headers.Count > 0)
        {
            sb.AppendLine("$headers = [");
            foreach (var h in headers)
                sb.AppendLine($"  {CodegenText.PhpSingle(h.Name)} => {CodegenText.PhpSingle(h.Value)},");
            sb.AppendLine("];");
        }
        if (ctx.Body is not null)
            sb.AppendLine($"$body = {CodegenText.PhpSingle(ctx.Body)};");
        sb.Append($"$request = new Request({CodegenText.PhpSingle(ctx.Method)}, {CodegenText.PhpSingle(ctx.Url)}");
        sb.Append(headers.Count > 0 ? ", $headers" : ", []");
        if (ctx.Body is not null) sb.Append(", $body");
        sb.AppendLine(");");
        sb.AppendLine("$res = $client->sendAsync($request)->wait();");
        sb.Append("echo $res->getBody();");
        return sb.ToString();
    }
}

/// <summary>Emits a PHP snippet using PEAR's HTTP_Request2.</summary>
public sealed class PhpHttpRequest2Emitter : ICodegenEmitter
{
    public string Language => "php";
    public string DisplayName => "PHP - HTTP_Request2";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine("<?php");
        sb.AppendLine("require_once 'HTTP/Request2.php';");
        sb.AppendLine("$request = new HTTP_Request2();");
        sb.AppendLine($"$request->setUrl({CodegenText.PhpSingle(ctx.Url)});");
        sb.AppendLine($"$request->setMethod({MethodConstant(ctx.Method)});");
        sb.AppendLine("$request->setConfig(array(");
        sb.AppendLine("  'follow_redirects' => TRUE");
        sb.AppendLine("));");
        var headers = ctx.HeadersWithContentType();
        if (headers.Count > 0)
        {
            sb.AppendLine("$request->setHeader(array(");
            for (var i = 0; i < headers.Count; i++)
            {
                sb.Append($"  {CodegenText.PhpSingle(headers[i].Name)} => {CodegenText.PhpSingle(headers[i].Value)}");
                sb.AppendLine(i < headers.Count - 1 ? "," : "");
            }
            sb.AppendLine("));");
        }
        if (ctx.Body is not null)
            sb.AppendLine($"$request->setBody({CodegenText.PhpSingle(ctx.Body)});");
        sb.AppendLine("try {");
        sb.AppendLine("  $response = $request->send();");
        sb.AppendLine("  if ($response->getStatus() == 200) {");
        sb.AppendLine("    echo $response->getBody();");
        sb.AppendLine("  }");
        sb.AppendLine("  else {");
        sb.AppendLine("    echo 'Unexpected HTTP status: ' . $response->getStatus() . ' ' . $response->getReasonPhrase();");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine("catch (HTTP_Request2_Exception $e) {");
        sb.AppendLine("  echo 'Error: ' . $e->getMessage();");
        sb.Append('}');
        return sb.ToString();
    }

    private static string MethodConstant(string method) => method.ToUpperInvariant() switch
    {
        "GET" or "POST" or "PUT" or "DELETE" or "HEAD" or "OPTIONS" or "TRACE" or "CONNECT"
            => "HTTP_Request2::METHOD_" + method.ToUpperInvariant(),
        _ => CodegenText.PhpSingle(method.ToUpperInvariant()),
    };
}
