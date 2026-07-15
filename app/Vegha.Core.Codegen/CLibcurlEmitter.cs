using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits a C snippet using libcurl's easy interface.</summary>
public sealed class CLibcurlEmitter : ICodegenEmitter
{
    public string Language => "c";
    public string DisplayName => "C - libcurl";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine("#include <stdio.h>");
        sb.AppendLine("#include <curl/curl.h>");
        sb.AppendLine();
        sb.AppendLine("int main(void)");
        sb.AppendLine("{");
        sb.AppendLine("  CURL *curl;");
        sb.AppendLine("  CURLcode res;");
        sb.AppendLine();
        sb.AppendLine("  curl = curl_easy_init();");
        sb.AppendLine("  if(curl) {");
        sb.AppendLine($"    curl_easy_setopt(curl, CURLOPT_CUSTOMREQUEST, {CodegenText.CString(ctx.Method)});");
        sb.AppendLine($"    curl_easy_setopt(curl, CURLOPT_URL, {CodegenText.CString(ctx.Url)});");
        sb.AppendLine("    curl_easy_setopt(curl, CURLOPT_FOLLOWLOCATION, 1L);");

        var headers = ctx.HeadersWithContentType();
        if (headers.Count > 0)
        {
            sb.AppendLine("    struct curl_slist *headers = NULL;");
            foreach (var h in headers)
                sb.AppendLine($"    headers = curl_slist_append(headers, {CodegenText.CString($"{h.Name}: {h.Value}")});");
            sb.AppendLine("    curl_easy_setopt(curl, CURLOPT_HTTPHEADER, headers);");
        }

        if (ctx.Body is not null)
        {
            sb.AppendLine($"    const char *data = {CodegenText.CString(ctx.Body)};");
            sb.AppendLine("    curl_easy_setopt(curl, CURLOPT_POSTFIELDS, data);");
        }

        sb.AppendLine("    res = curl_easy_perform(curl);");
        sb.AppendLine("    if(res != CURLE_OK)");
        sb.AppendLine("      fprintf(stderr, \"curl_easy_perform() failed: %s\\n\", curl_easy_strerror(res));");
        sb.AppendLine();
        sb.AppendLine("    curl_easy_cleanup(curl);");
        sb.AppendLine("  }");
        sb.Append("  return 0;").AppendLine();
        sb.Append('}');
        return sb.ToString();
    }
}
