using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Importers;

/// <summary>
/// Emits a <see cref="RequestItem"/> as Bruno <c>.bru</c> source text. Reverse of
/// <see cref="BruToRequestConverter"/>; mirrors <c>bruno-lang/v2/src/jsonToBru.js</c>.
///
/// Coverage parity with the importer (HTTP methods, dict-shaped auths, JSON/text/XML/SPARQL
/// body, form-urlencoded/multipart-form pairs, vars:*, scripts, tests, docs). GraphQL split,
/// custom HTTP method via "http" block, and binary/file body are TODO.
/// </summary>
public static class BruEmitter
{
    public static string Emit(RequestItem request)
    {
        var sb = new StringBuilder();

        EmitMeta(sb, request);
        EmitVerbBlock(sb, request);
        EmitDictBlock(sb, "params:query", request.Params);
        EmitDictBlock(sb, "params:path", request.PathParams);
        EmitDictBlock(sb, "headers", request.Headers);
        EmitAuth(sb, request.Auth);
        EmitBody(sb, request.Body);
        EmitDictBlock(sb, "vars:pre-request", request.PreRequestVars);
        EmitDictBlock(sb, "vars:post-response", request.PostResponseVars);
        EmitTextBlock(sb, "script:pre-request", request.PreRequestScript);
        EmitTextBlock(sb, "script:post-response", request.PostResponseScript);
        EmitTextBlock(sb, "tests", request.Tests);
        EmitTextBlock(sb, "docs", request.Docs);
        EmitSettings(sb, request.Settings);

        return sb.ToString();
    }

    /// <summary>Emit only when at least one setting differs from default (keeps round-tripped files clean).</summary>
    private static void EmitSettings(StringBuilder sb, RequestSettingsConfig settings)
    {
        var defaults = new RequestSettingsConfig();
        if (settings == defaults) return;

        sb.AppendLine("settings {");
        if (settings.FollowRedirects != defaults.FollowRedirects)
            sb.Append("  followRedirects: ").AppendLine(settings.FollowRedirects.ToString().ToLowerInvariant());
        if (settings.VerifySsl != defaults.VerifySsl)
            sb.Append("  verifySSL: ").AppendLine(settings.VerifySsl.ToString().ToLowerInvariant());
        if (settings.EncodeUrl != defaults.EncodeUrl)
            sb.Append("  encodeUrl: ").AppendLine(settings.EncodeUrl.ToString().ToLowerInvariant());
        if (settings.SendCookies != defaults.SendCookies)
            sb.Append("  sendCookies: ").AppendLine(settings.SendCookies.ToString().ToLowerInvariant());
        if (settings.SaveCookies != defaults.SaveCookies)
            sb.Append("  saveCookies: ").AppendLine(settings.SaveCookies.ToString().ToLowerInvariant());
        if (settings.EnableHttp2 != defaults.EnableHttp2)
            sb.Append("  http2: ").AppendLine(settings.EnableHttp2.ToString().ToLowerInvariant());
        sb.AppendLine("}");
        sb.AppendLine();
    }

    // ============================== Blocks ==============================

    private static void EmitMeta(StringBuilder sb, RequestItem r)
    {
        sb.AppendLine("meta {");
        if (!string.IsNullOrEmpty(r.Name))
            sb.Append("  name: ").AppendLine(r.Name);
        // Round-trip the raw type string when it was captured at load time — that keeps
        // `type: soap` intact even though SOAP requests open in the HTTP workspace and
        // therefore carry Kind = Http. Falls back to mapping from Kind when no raw value
        // was preserved (e.g. brand-new requests created in-app).
        var typeStr = string.IsNullOrEmpty(r.MetaType) ? KindToString(r.Kind) : r.MetaType;
        sb.Append("  type: ").AppendLine(typeStr);
        if (r.Sequence > 0)
            sb.Append("  seq: ").AppendLine(r.Sequence.ToString());
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitVerbBlock(StringBuilder sb, RequestItem r)
    {
        var verb = r.Method.ToLowerInvariant();
        sb.Append(verb).AppendLine(" {");
        sb.Append("  url: ").AppendLine(r.Url);
        sb.Append("  body: ").AppendLine(BodyTypeToString(r.Body.Mode));
        sb.Append("  auth: ").AppendLine(AuthTypeToString(r.Auth));
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitDictBlock(StringBuilder sb, string blockName, IList<KvPair> pairs)
    {
        if (pairs.Count == 0) return;
        sb.Append(blockName).AppendLine(" {");
        foreach (var p in pairs)
        {
            EmitPair(sb, p);
        }
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitPair(StringBuilder sb, KvPair p)
    {
        sb.Append("  ");
        if (!p.Enabled) sb.Append('~');
        sb.Append(NeedsQuoting(p.Name) ? QuoteKey(p.Name) : p.Name);
        sb.Append(": ").AppendLine(p.Value);
    }

    private static void EmitAuth(StringBuilder sb, AuthConfig? auth)
    {
        if (auth is null) return;
        var blockName = auth.Type switch
        {
            AuthType.None    => null,
            AuthType.Inherit => null, // declared on the verb block; no body
            AuthType.ApiKey  => "auth:apikey",
            AuthType.Bearer  => "auth:bearer",
            AuthType.Basic   => "auth:basic",
            AuthType.Digest  => "auth:digest",
            AuthType.OAuth1  => "auth:oauth1",
            AuthType.OAuth2  => "auth:oauth2",
            AuthType.AwsV4   => "auth:awsv4",
            AuthType.Ntlm    => "auth:ntlm",
            AuthType.Wsse    => "auth:wsse",
            _ => null
        };
        if (blockName is null || auth.Parameters.Count == 0) return;

        sb.Append(blockName).AppendLine(" {");
        foreach (var (k, v) in auth.Parameters)
        {
            sb.Append("  ").Append(k).Append(": ").AppendLine(v);
        }
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitBody(StringBuilder sb, BodyConfig body)
    {
        switch (body.Mode)
        {
            case BodyMode.Json:           EmitTextBlock(sb, "body:json", body.Content); break;
            case BodyMode.Text:           EmitTextBlock(sb, "body:text", body.Content); break;
            case BodyMode.Xml:            EmitTextBlock(sb, "body:xml", body.Content); break;
            case BodyMode.Sparql:         EmitTextBlock(sb, "body:sparql", body.Content); break;
            case BodyMode.GraphQL:
                EmitTextBlock(sb, "body:graphql", body.GraphQLQuery);
                EmitTextBlock(sb, "body:graphql:vars", body.GraphQLVariables);
                break;
            case BodyMode.FormUrlEncoded: EmitDictBlock(sb, "body:form-urlencoded", body.FormData); break;
            case BodyMode.MultipartForm:  EmitDictBlock(sb, "body:multipart-form", body.FormData); break;
            case BodyMode.None:
            case BodyMode.Binary:
            default:
                break;
        }
    }

    private static void EmitTextBlock(StringBuilder sb, string blockName, string? content)
    {
        if (string.IsNullOrEmpty(content)) return;
        sb.Append(blockName).AppendLine(" {");
        sb.AppendLine(IndentTextBlockContent(content.TrimEnd('\n', '\r')));
        sb.AppendLine("}");
        sb.AppendLine();
    }

    /// <summary>Bruno's grammar closes a text block at "\n}" (a closing brace at column 0).
    /// Body / script content that contains a column-0 "}" — common in JSON bodies and JS
    /// blocks — would prematurely terminate the block when read back. Indents every line
    /// by two spaces in that case. Skipped when the content is already Bruno-formatted
    /// (no line starts with "}"), so files round-tripped through parse → emit stay byte-stable.</summary>
    private static string IndentTextBlockContent(string content)
    {
        var lines = content.Split('\n');
        var needsIndent = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("}", StringComparison.Ordinal)) { needsIndent = true; break; }
        }
        if (!needsIndent) return content;

        var sb = new StringBuilder(content.Length + lines.Length * 2);
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            if (lines[i].Length > 0) sb.Append("  ");
            sb.Append(lines[i]);
        }
        return sb.ToString();
    }

    // ============================== Helpers ==============================

    private static string KindToString(RequestKind kind) => kind switch
    {
        RequestKind.Http      => "http",
        RequestKind.GraphQL   => "graphql",
        RequestKind.Grpc      => "grpc",
        RequestKind.WebSocket => "ws",
        RequestKind.Soap      => "soap",
        _ => "http"
    };

    private static string BodyTypeToString(BodyMode mode) => mode switch
    {
        BodyMode.None           => "none",
        BodyMode.Json           => "json",
        BodyMode.Text           => "text",
        BodyMode.Xml            => "xml",
        BodyMode.Sparql         => "sparql",
        BodyMode.GraphQL        => "graphql",
        BodyMode.FormUrlEncoded => "form-urlencoded",
        BodyMode.MultipartForm  => "multipart-form",
        BodyMode.Binary         => "binary",
        _ => "none"
    };

    private static string AuthTypeToString(AuthConfig? auth) => auth?.Type switch
    {
        null or AuthType.None => "none",
        AuthType.Inherit      => "inherit",
        AuthType.ApiKey       => "apikey",
        AuthType.Bearer       => "bearer",
        AuthType.Basic        => "basic",
        AuthType.Digest       => "digest",
        AuthType.OAuth1       => "oauth1",
        AuthType.OAuth2       => "oauth2",
        AuthType.AwsV4        => "awsv4",
        AuthType.Ntlm         => "ntlm",
        AuthType.Wsse         => "wsse",
        _ => "none"
    };

    /// <summary>A key needs quoting if it has spaces, colons, or braces.</summary>
    private static bool NeedsQuoting(string name) =>
        name.Contains(' ') || name.Contains(':') || name.Contains('{') || name.Contains('}') ||
        name.Contains('\t');

    private static string QuoteKey(string name)
    {
        var escaped = name.Replace("\"", "\\\"");
        return "\"" + escaped + "\"";
    }
}
