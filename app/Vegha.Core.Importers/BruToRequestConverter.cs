using Vegha.Core.Bru.Parser;
using Vegha.Core.Domain;

namespace Vegha.Core.Importers;

/// <summary>
/// Maps a parsed <see cref="BruDocument"/> into a <see cref="RequestItem"/>.
/// Mirrors logic from bruno-filestore/src/formats/bru/index.ts (parseBruRequest).
/// </summary>
public static class BruToRequestConverter
{
    private static readonly HashSet<string> HttpVerbBlocks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "get", "post", "put", "delete", "patch", "options", "head", "connect", "trace", "http"
        };

    public static RequestItem Convert(BruDocument doc)
    {
        var meta = FindDict(doc, "meta");
        var (verbBlock, method) = FindHttpVerbBlock(doc);

        var name = meta?.Pairs.FirstOrDefault(p => p.Name == "name")?.Value is StringValue n
            ? n.Text
            : string.Empty;
        var seq = meta?.Pairs.FirstOrDefault(p => p.Name == "seq")?.Value is StringValue s
                  && int.TryParse(s.Text, out var sNum)
            ? sNum
            : 0;
        // Capture the raw `meta.type` string so it survives a round-trip even when the
        // UI normalizes Kind to Http (e.g. SOAP requests open in the HTTP workspace).
        var metaType = (meta?.Pairs.FirstOrDefault(p => p.Name == "type")?.Value as StringValue)?.Text;

        var url = verbBlock?.Pairs.FirstOrDefault(p => p.Name == "url")?.Value is StringValue u
            ? u.Text
            : string.Empty;
        var bodyType = verbBlock?.Pairs.FirstOrDefault(p => p.Name == "body")?.Value is StringValue b
            ? b.Text
            : "none";
        var authType = verbBlock?.Pairs.FirstOrDefault(p => p.Name == "auth")?.Value is StringValue a
            ? a.Text
            : "none";

        var headers = ToKvPairs(FindDict(doc, "headers"));
        var queryParams = ToKvPairs(FindDict(doc, "params:query"));
        var pathParams = ToKvPairs(FindDict(doc, "params:path"));
        var preVars = ToKvPairs(FindDict(doc, "vars:pre-request"));
        var postVars = ToKvPairs(FindDict(doc, "vars:post-response"));

        var body = BuildBody(doc, bodyType);
        var auth = BuildAuth(doc, authType);

        var preScript = FindText(doc, "script:pre-request");
        var postScript = FindText(doc, "script:post-response");
        var tests = FindText(doc, "tests");
        var docs = FindText(doc, "docs");
        var settings = BuildSettings(FindDict(doc, "settings"));
        var soap = BuildSoap(FindDict(doc, "soap"));

        return new RequestItem
        {
            Name = name,
            Kind = ResolveKind(doc),
            MetaType = metaType,
            Method = method,
            Url = url,
            Sequence = seq,
            Params = queryParams,
            PathParams = pathParams,
            Headers = headers,
            Body = body,
            Auth = auth,
            PreRequestVars = preVars,
            PostResponseVars = postVars,
            PreRequestScript = preScript,
            PostResponseScript = postScript,
            Tests = tests,
            Docs = docs,
            Settings = settings,
            Soap = soap,
        };
    }

    /// <summary>Reads the optional <c>soap { }</c> block (SOAP WS-Security / WS-Addressing).
    /// A section is materialized only when at least one of its keys is present.</summary>
    private static SoapConfig? BuildSoap(DictBlock? block)
    {
        if (block is null) return null;

        string? Read(string key) =>
            block.Pairs.FirstOrDefault(p => p.Name == key)?.Value is StringValue s ? s.Text.Trim() : null;
        bool ReadBool(string key, bool dflt) =>
            bool.TryParse(Read(key), out var b) ? b : dflt;

        WssTimestampConfig? timestamp = null;
        if (int.TryParse(Read("wssTimestampTtl"), out var ttl))
            timestamp = new WssTimestampConfig { TimeToLiveSeconds = ttl };

        WssUsernameTokenConfig? usernameToken = null;
        var wssUser = Read("wssUsername");
        if (wssUser is not null)
        {
            usernameToken = new WssUsernameTokenConfig
            {
                Username = wssUser,
                Password = Read("wssPassword") ?? string.Empty,
                PasswordType = string.Equals(Read("wssPasswordType"), "digest", StringComparison.OrdinalIgnoreCase)
                    ? WssPasswordType.Digest
                    : WssPasswordType.Text,
                AddNonce = ReadBool("wssAddNonce", true),
                AddCreated = ReadBool("wssAddCreated", true),
            };
        }

        WsAddressingConfig? addressing = null;
        var wsaAction = Read("wsaAction");
        var wsaTo = Read("wsaTo");
        var wsaReplyTo = Read("wsaReplyTo");
        var wsaMessageId = Read("wsaMessageId");
        if (wsaAction is not null || wsaTo is not null || wsaReplyTo is not null
            || wsaMessageId is not null || Read("wsaAutoMessageId") is not null)
        {
            addressing = new WsAddressingConfig
            {
                Action = wsaAction,
                To = wsaTo,
                ReplyTo = wsaReplyTo,
                MessageId = wsaMessageId,
                AutoMessageId = ReadBool("wsaAutoMessageId", true),
            };
        }

        if (timestamp is null && usernameToken is null && addressing is null)
            return null;

        return new SoapConfig
        {
            Timestamp = timestamp,
            UsernameToken = usernameToken,
            Addressing = addressing,
        };
    }

    /// <summary>Reads the optional <c>settings { }</c> block. Missing block → defaults.</summary>
    private static RequestSettingsConfig BuildSettings(DictBlock? block)
    {
        if (block is null) return new RequestSettingsConfig();

        bool ReadBool(string key, bool dflt)
        {
            var pair = block.Pairs.FirstOrDefault(p => p.Name == key);
            if (pair?.Value is StringValue s &&
                bool.TryParse(s.Text.Trim(), out var b)) return b;
            return dflt;
        }

        return new RequestSettingsConfig
        {
            FollowRedirects = ReadBool("followRedirects", true),
            VerifySsl       = ReadBool("verifySSL", true),
            EncodeUrl       = ReadBool("encodeUrl", true),
            SendCookies     = ReadBool("sendCookies", true),
            SaveCookies     = ReadBool("saveCookies", true),
            EnableHttp2     = ReadBool("http2", false),
        };
    }

    // ============================== Block lookup ==============================

    private static DictBlock? FindDict(BruDocument doc, string name) =>
        doc.Blocks.OfType<DictBlock>().FirstOrDefault(b => b.Name == name);

    private static string? FindText(BruDocument doc, string name) =>
        doc.Blocks.OfType<TextBlock>().FirstOrDefault(b => b.Name == name)?.Text;

    private static (DictBlock? Block, string Method) FindHttpVerbBlock(BruDocument doc)
    {
        foreach (var b in doc.Blocks.OfType<DictBlock>())
        {
            if (HttpVerbBlocks.Contains(b.Name))
            {
                // "http" is the custom-method block; the actual method is read from a "method" pair
                // (Bruno's httpcustom). For standard verbs the block name IS the method.
                if (string.Equals(b.Name, "http", StringComparison.OrdinalIgnoreCase))
                {
                    var custom = b.Pairs.FirstOrDefault(p => p.Name == "method")?.Value as StringValue;
                    return (b, custom?.Text.ToUpperInvariant() ?? "GET");
                }
                return (b, b.Name.ToUpperInvariant());
            }
        }
        return (null, "GET");
    }

    private static RequestKind ResolveKind(BruDocument doc)
    {
        // SOAP requests are intentionally NOT routed to a dedicated workspace — they open in
        // the regular HTTP tab (Body / Headers / Vars / etc) which is the right shape for
        // editing the SOAP envelope as raw XML. A previous version read `meta.type: soap`
        // and switched to the WSDL-loading SoapWorkspace; that turned out to be wrong UX
        // (user has to re-enter WSDL + endpoint), so we deliberately ignore meta.type and
        // sniff structural blocks only.
        if (doc.Blocks.Any(b => b.Name == "grpc")) return RequestKind.Grpc;
        if (doc.Blocks.Any(b => b.Name == "ws")) return RequestKind.WebSocket;
        if (doc.Blocks.OfType<TextBlock>().Any(b => b.Name == "body:graphql"))
            return RequestKind.GraphQL;
        // A declared graphql body (`body: graphql` in the verb block) also marks the request
        // as GraphQL — the body:graphql text block is absent while the query is still empty
        // (the emitter skips empty text blocks).
        if (doc.Blocks.OfType<DictBlock>().Any(b =>
                b.Pairs.Any(p => p.Name == "body" && p.Value is StringValue { Text: "graphql" })))
            return RequestKind.GraphQL;
        return RequestKind.Http;
    }

    // ============================== Pair → KvPair ==============================

    private static List<KvPair> ToKvPairs(DictBlock? block)
    {
        if (block is null) return new List<KvPair>();
        return block.Pairs.Select(ToKvPair).ToList();
    }

    private static KvPair ToKvPair(BruPair p) =>
        new(p.Name, p.Value switch
        {
            StringValue s => s.Text,
            ListValue l => string.Join(",", l.Items),
            MultilineValue m => m.Text,
            _ => string.Empty
        }, p.Enabled);

    // ============================== Body ==============================

    private static BodyConfig BuildBody(BruDocument doc, string declaredType)
    {
        // Look for body:* blocks in document and decide which one wins.
        // Order: the verb block's "body: <type>" tells us which to read.
        return declaredType switch
        {
            "none" or "" => new BodyConfig { Mode = BodyMode.None },
            "json" => TextBodyOrEmpty(doc, "body:json", BodyMode.Json),
            "text" => TextBodyOrEmpty(doc, "body:text", BodyMode.Text),
            "xml" => TextBodyOrEmpty(doc, "body:xml", BodyMode.Xml),
            "sparql" => TextBodyOrEmpty(doc, "body:sparql", BodyMode.Sparql),
            "graphql" => BuildGraphQLBody(doc),
            "form-urlencoded" => new BodyConfig
            {
                Mode = BodyMode.FormUrlEncoded,
                FormData = ToKvPairs(FindDict(doc, "body:form-urlencoded"))
            },
            "multipart-form" => new BodyConfig
            {
                Mode = BodyMode.MultipartForm,
                FormData = ToKvPairs(FindDict(doc, "body:multipart-form"))
            },
            "binary" or "file" => new BodyConfig
            {
                Mode = BodyMode.Binary,
                FormData = ToKvPairs(FindDict(doc, "body:file"))
            },
            _ => new BodyConfig { Mode = BodyMode.None }
        };
    }

    private static BodyConfig TextBodyOrEmpty(BruDocument doc, string blockName, BodyMode mode)
    {
        var content = FindText(doc, blockName);
        return new BodyConfig { Mode = mode, Content = content };
    }

    private static BodyConfig BuildGraphQLBody(BruDocument doc) =>
        new()
        {
            Mode = BodyMode.GraphQL,
            GraphQLQuery = FindText(doc, "body:graphql"),
            GraphQLVariables = FindText(doc, "body:graphql:vars"),
        };

    // ============================== Auth ==============================

    private static AuthConfig? BuildAuth(BruDocument doc, string declaredType)
    {
        if (string.IsNullOrEmpty(declaredType) || declaredType == "none")
            return null;
        if (declaredType == "inherit")
            return new AuthConfig { Type = AuthType.Inherit };

        var blockName = $"auth:{declaredType}";
        var block = FindDict(doc, blockName);
        if (block is null)
            return new AuthConfig { Type = ParseAuthType(declaredType) };

        var parameters = block.Pairs
            .Where(p => p.Enabled)
            .ToDictionary(
                p => p.Name,
                p => p.Value is StringValue s ? s.Text : string.Empty);

        return new AuthConfig
        {
            Type = ParseAuthType(declaredType),
            Parameters = parameters
        };
    }

    private static AuthType ParseAuthType(string s) => s switch
    {
        "apikey" => AuthType.ApiKey,
        "bearer" => AuthType.Bearer,
        "basic" => AuthType.Basic,
        "digest" => AuthType.Digest,
        "oauth1" => AuthType.OAuth1,
        "oauth2" => AuthType.OAuth2,
        "awsv4" => AuthType.AwsV4,
        "ntlm" => AuthType.Ntlm,
        "wsse" => AuthType.Wsse,
        "inherit" => AuthType.Inherit,
        _ => AuthType.None,
    };
}
