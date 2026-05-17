using System.Text;
using Vegha.Core.Domain;
using Vegha.Core.Interpolation;

namespace Vegha.Core.Requests;

/// <summary>
/// Applies an <see cref="AuthConfig"/> to a request: resolves any <c>{{var}}</c> placeholders
/// in the auth parameters and emits the headers (or query-string additions) the server expects.
///
/// First pass covers None / Inherit (no-op) / Bearer / API Key / Basic. Digest, OAuth2, OAuth1,
/// AWS SigV4, NTLM, WSSE will layer on as separate strategies in subsequent MVP themes
/// (mirrors bruno-requests/src/auth/).
/// </summary>
public static class AuthApplier
{
    public sealed record Result(string Url, IReadOnlyList<KeyValuePair<string, string>> Headers);

    private static readonly Result Empty = new(string.Empty, Array.Empty<KeyValuePair<string, string>>());

    public static Result Apply(
        AuthConfig? auth,
        string url,
        IReadOnlyDictionary<string, string>? vars = null)
    {
        if (auth is null || auth.Type is AuthType.None or AuthType.Inherit)
            return new Result(url, Array.Empty<KeyValuePair<string, string>>());

        return auth.Type switch
        {
            AuthType.Bearer => ApplyBearer(auth, url, vars),
            AuthType.Basic  => ApplyBasic(auth, url, vars),
            AuthType.ApiKey => ApplyApiKey(auth, url, vars),
            _ => new Result(url, Array.Empty<KeyValuePair<string, string>>())
        };
    }

    private static Result ApplyBearer(AuthConfig auth, string url, IReadOnlyDictionary<string, string>? vars)
    {
        var token = Resolve(Get(auth, "token"), vars);
        if (string.IsNullOrEmpty(token))
            return new Result(url, Array.Empty<KeyValuePair<string, string>>());

        var headers = new[] { new KeyValuePair<string, string>("Authorization", "Bearer " + token) };
        return new Result(url, headers);
    }

    private static Result ApplyBasic(AuthConfig auth, string url, IReadOnlyDictionary<string, string>? vars)
    {
        var user = Resolve(Get(auth, "username"), vars);
        var pass = Resolve(Get(auth, "password"), vars);
        var raw = $"{user}:{pass}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        var headers = new[] { new KeyValuePair<string, string>("Authorization", "Basic " + encoded) };
        return new Result(url, headers);
    }

    private static Result ApplyApiKey(AuthConfig auth, string url, IReadOnlyDictionary<string, string>? vars)
    {
        var name = Resolve(Get(auth, "key"), vars);
        var value = Resolve(Get(auth, "value"), vars);
        var placement = Get(auth, "placement");

        // Default to "header" if unspecified — matches Bruno's default.
        var inQuery = string.Equals(placement, "queryparams", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(name)) return new Result(url, Array.Empty<KeyValuePair<string, string>>());

        if (inQuery)
        {
            var separator = url.Contains('?') ? "&" : "?";
            var newUrl = url + separator +
                Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
            return new Result(newUrl, Array.Empty<KeyValuePair<string, string>>());
        }

        return new Result(url, new[] { new KeyValuePair<string, string>(name, value) });
    }

    // ---- helpers ----

    private static string Get(AuthConfig auth, string key) =>
        auth.Parameters.TryGetValue(key, out var v) ? v : string.Empty;

    private static string Resolve(string template, IReadOnlyDictionary<string, string>? vars) =>
        vars is null ? template : Interpolator.Resolve(template, vars);
}
