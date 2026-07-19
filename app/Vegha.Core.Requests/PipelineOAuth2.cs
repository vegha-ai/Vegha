using System.Text.Json;
using Vegha.Core.Domain;

namespace Vegha.Core.Requests;

/// <summary>
/// Headless OAuth2 support for the collection-runner pipeline. Resolves an
/// <see cref="AuthType.OAuth2"/> config into a concrete Bearer/ApiKey auth by acquiring
/// a token through <see cref="OAuth2TokenAcquirer"/> before the request is sent.
///
/// Only non-interactive grants are supported here: <c>client_credentials</c> (the
/// machine-to-machine staple) and <c>password</c>. <c>authorization_code</c> needs a
/// browser round-trip and stays editor-only.
///
/// The acquirer instance is shared process-wide so its token cache spans all requests
/// of a runner invocation — a 50-request collection fetches the token once, not 50 times.
/// </summary>
public static class PipelineOAuth2
{
    private static readonly Lazy<OAuth2TokenAcquirer> s_acquirer =
        new(() => new OAuth2TokenAcquirer(new HttpClient()));

    /// <summary>Resolves an OAuth2 auth config to an applicable auth (Bearer or ApiKey
    /// shaped per <c>add_token_to</c>/<c>header_prefix</c>), or an error message when the
    /// grant is unsupported or token acquisition fails. All config values support
    /// <c>{{var}}</c> interpolation (handled inside the acquirer).</summary>
    public static async Task<(AuthConfig? Auth, string? Error)> ResolveAsync(
        AuthConfig auth,
        IReadOnlyDictionary<string, string> vars,
        CancellationToken cancellationToken = default)
    {
        string Get(string key) => auth.Parameters.TryGetValue(key, out var v) ? v : string.Empty;

        var grantType = Get("grant_type");
        if (string.IsNullOrEmpty(grantType)) grantType = "client_credentials";

        var tokenUrl = Get("access_token_url");
        var clientId = Get("client_id");
        var clientSecret = Get("client_secret");
        var scope = Get("scope");
        var placement = string.IsNullOrEmpty(Get("credentials_placement")) ? "body" : Get("credentials_placement");
        var tokenId = string.IsNullOrEmpty(Get("token_id")) ? "credentials" : Get("token_id");
        var tokenSource = string.IsNullOrEmpty(Get("token_source")) ? "access_token" : Get("token_source");
        var refreshUrl = string.IsNullOrEmpty(Get("refresh_token_url")) ? null : Get("refresh_token_url");
        var tokenParams = ParseAdditionalParams(Get("additional_token_params"));
        var refreshParams = ParseAdditionalParams(Get("additional_refresh_params"));

        OAuth2TokenResult token;
        switch (grantType)
        {
            case "client_credentials":
                token = await s_acquirer.Value.AcquireClientCredentialsAsync(
                    new OAuth2ClientCredentialsConfig(
                        TokenUrl: tokenUrl,
                        ClientId: clientId,
                        ClientSecret: clientSecret,
                        Scope: string.IsNullOrWhiteSpace(scope) ? null : scope,
                        CredentialsPlacement: placement,
                        AdditionalParameters: tokenParams,
                        TokenId: tokenId,
                        TokenSource: tokenSource,
                        RefreshTokenUrl: refreshUrl,
                        RefreshParameters: refreshParams),
                    vars, cancellationToken).ConfigureAwait(false);
                break;

            case "password":
                token = await s_acquirer.Value.AcquirePasswordAsync(
                    new OAuth2PasswordConfig(
                        TokenUrl: tokenUrl,
                        ClientId: clientId,
                        ClientSecret: clientSecret,
                        Username: Get("username"),
                        Password: Get("password"),
                        Scope: string.IsNullOrWhiteSpace(scope) ? null : scope,
                        CredentialsPlacement: placement,
                        AdditionalParameters: tokenParams,
                        TokenId: tokenId,
                        TokenSource: tokenSource,
                        RefreshTokenUrl: refreshUrl,
                        RefreshParameters: refreshParams),
                    vars, cancellationToken).ConfigureAwait(false);
                break;

            default:
                return (null,
                    $"OAuth2 grant type '{grantType}' requires an interactive browser and is not supported by the collection runner. Run via the request editor.");
        }

        if (!token.IsSuccess || string.IsNullOrEmpty(token.AccessToken))
            return (null, token.ErrorMessage ?? "OAuth2 token acquisition failed.");

        return (BuildTokenAuth(auth, token.AccessToken!), null);
    }

    /// <summary>Maps the acquired token onto an auth the <see cref="AuthApplier"/> can emit,
    /// honoring <c>add_token_to</c> (headers | queryparams) and <c>header_prefix</c>. Mirrors
    /// the request editor's BuildOAuth2BearerAuth so GUI and runner behave identically.</summary>
    private static AuthConfig BuildTokenAuth(AuthConfig auth, string accessToken)
    {
        string Get(string key) => auth.Parameters.TryGetValue(key, out var v) ? v : string.Empty;

        var addTokenTo = Get("add_token_to");
        var headerPrefix = Get("header_prefix");

        if (string.Equals(addTokenTo, "queryparams", StringComparison.OrdinalIgnoreCase))
        {
            return new AuthConfig
            {
                Type = AuthType.ApiKey,
                Parameters = new Dictionary<string, string>
                {
                    ["key"] = "access_token",
                    ["value"] = accessToken,
                    ["placement"] = "queryparams",
                },
            };
        }

        // headers (default). Non-Bearer prefix goes out as a raw header so the applier
        // doesn't re-prefix "Bearer ".
        if (!string.IsNullOrEmpty(headerPrefix) && !string.Equals(headerPrefix, "Bearer", StringComparison.Ordinal))
        {
            return new AuthConfig
            {
                Type = AuthType.ApiKey,
                Parameters = new Dictionary<string, string>
                {
                    ["key"] = "Authorization",
                    ["value"] = headerPrefix + " " + accessToken,
                    ["placement"] = "header",
                },
            };
        }

        return new AuthConfig
        {
            Type = AuthType.Bearer,
            Parameters = new Dictionary<string, string> { ["token"] = accessToken },
        };
    }

    /// <summary>Parses the JSON-serialized Additional Parameters rows the editor stores in
    /// the flat auth parameters ("[{key,value,sendIn,enabled}]"). Tolerant of bad JSON and
    /// missing fields; disabled rows are dropped.</summary>
    private static IReadOnlyList<OAuth2AdditionalParam>? ParseAdditionalParams(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return null; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var list = new List<OAuth2AdditionalParam>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False) continue;
                var key = el.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                if (string.IsNullOrEmpty(key)) continue;
                var value = el.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? string.Empty) : string.Empty;
                var sendIn = el.TryGetProperty("sendIn", out var s) && s.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(s.GetString()) ? s.GetString()! : "body";
                list.Add(new OAuth2AdditionalParam(key!, value, sendIn));
            }
            return list.Count == 0 ? null : list;
        }
    }
}
