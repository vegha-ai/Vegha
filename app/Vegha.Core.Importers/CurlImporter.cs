using System.Text;
using System.Text.RegularExpressions;
using Vegha.Core.Domain;

namespace Vegha.Core.Importers;

/// <summary>
/// Parses a cURL command line into a <see cref="RequestItem"/>. Supports bash, cmd
/// (Chrome "Copy as cURL (cmd)" with caret quoting), and PowerShell continuations.
/// Also tolerates input where line continuations have been collapsed to a single line
/// (the URL editor strips newlines on paste, leaving the trailing escape char behind).
/// </summary>
public static class CurlImporter
{
    /// <summary>Cheap pre-check used by the URL field's paste handler before calling
    /// <see cref="Parse"/>. Matches "curl" as the first token; case-insensitive.</summary>
    public static bool LooksLikeCurl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.TrimStart();
        if (t.Length < 5) return false;
        if (!t.StartsWith("curl", StringComparison.OrdinalIgnoreCase)) return false;
        return char.IsWhiteSpace(t[4]);
    }

    public static RequestItem Parse(string curl)
    {
        ArgumentNullException.ThrowIfNull(curl);
        var tokens = Tokenize(curl);
        if (tokens.Count > 0 && string.Equals(tokens[0], "curl", StringComparison.OrdinalIgnoreCase))
            tokens.RemoveAt(0);
        return BuildRequest(tokens);
    }

    public static bool TryParse(string curl, out RequestItem? request)
    {
        try { request = Parse(curl); return true; }
        catch { request = null; return false; }
    }

    // ----- Builder -----
    private static RequestItem BuildRequest(List<string> tokens)
    {
        string? url = null;
        string? method = null;
        var headers = new List<KvPair>();
        var cookiePairs = new List<string>();
        var formFields = new List<(string Name, string Value)>();
        var dataChunks = new List<string>();
        var dataIsRaw = false;
        string? basicUser = null;

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            string? Next() => i + 1 < tokens.Count ? tokens[++i] : null;

            switch (t)
            {
                case "-X":
                case "--request":
                    method = Next() ?? method;
                    break;
                case "-H":
                case "--header":
                {
                    var h = Next();
                    if (h is not null) AddHeader(headers, h);
                    break;
                }
                case "-b":
                case "--cookie":
                {
                    var c = Next();
                    if (c is not null) cookiePairs.Add(c);
                    break;
                }
                case "-u":
                case "--user":
                    basicUser = Next();
                    break;
                case "-A":
                case "--user-agent":
                {
                    var v = Next();
                    if (v is not null) headers.Add(new KvPair("User-Agent", v));
                    break;
                }
                case "-e":
                case "--referer":
                {
                    var v = Next();
                    if (v is not null) headers.Add(new KvPair("Referer", v));
                    break;
                }
                case "-d":
                case "--data":
                case "--data-ascii":
                {
                    var v = Next();
                    if (v is not null) dataChunks.Add(v);
                    break;
                }
                case "--data-raw":
                {
                    var v = Next();
                    if (v is not null) { dataChunks.Add(v); dataIsRaw = true; }
                    break;
                }
                case "--data-binary":
                {
                    var v = Next();
                    if (v is not null) { dataChunks.Add(v); dataIsRaw = true; }
                    break;
                }
                case "--data-urlencode":
                {
                    var v = Next();
                    if (v is not null) dataChunks.Add(EncodeUrlData(v));
                    break;
                }
                case "-F":
                case "--form":
                {
                    var v = Next();
                    if (v is not null)
                    {
                        var (n, val) = SplitFirst(v, '=');
                        formFields.Add((n, val ?? string.Empty));
                    }
                    break;
                }
                case "--url":
                    url = Next() ?? url;
                    break;
                case "-G":
                case "--get":
                    method ??= "GET";
                    break;
                case "-I":
                case "--head":
                    method ??= "HEAD";
                    break;
                // Options that consume a value we don't model — skip the value.
                case "-o":
                case "--output":
                case "--max-time":
                case "--connect-timeout":
                case "--resolve":
                case "--cert":
                case "--key":
                case "--cacert":
                case "--proxy":
                case "-x":
                    _ = Next();
                    break;
                // Pure flags we don't model — drop silently.
                case "-k":
                case "--insecure":
                case "-L":
                case "--location":
                case "--compressed":
                case "--http2":
                case "--http1.1":
                case "-s":
                case "--silent":
                case "-v":
                case "--verbose":
                case "-i":
                case "--include":
                case "-#":
                case "--progress-bar":
                    break;
                default:
                    if (t.StartsWith("-", StringComparison.Ordinal))
                    {
                        // Unknown option: best-effort. Consume next token if it isn't another flag.
                        if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("-", StringComparison.Ordinal))
                            _ = Next();
                    }
                    else
                    {
                        url ??= t;
                    }
                    break;
            }
        }

        AuthConfig? auth = null;

        // -u user:pass → Basic.
        if (basicUser is not null)
        {
            var (user, pwd) = SplitFirst(basicUser, ':');
            auth = new AuthConfig
            {
                Type = AuthType.Basic,
                Parameters = new Dictionary<string, string>
                {
                    ["username"] = user,
                    ["password"] = pwd ?? string.Empty,
                },
            };
        }

        // Promote Authorization: Bearer / Basic into AuthConfig and drop the header.
        var authHeader = headers.FirstOrDefault(h =>
            string.Equals(h.Name, "Authorization", StringComparison.OrdinalIgnoreCase));
        if (authHeader is not null && auth is null)
        {
            var val = authHeader.Value ?? string.Empty;
            if (val.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                auth = new AuthConfig
                {
                    Type = AuthType.Bearer,
                    Parameters = new Dictionary<string, string>
                    {
                        ["token"] = val.Substring(7).Trim(),
                    },
                };
                headers.Remove(authHeader);
            }
            else if (val.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var decoded = Encoding.UTF8.GetString(
                        Convert.FromBase64String(val.Substring(6).Trim()));
                    var (u, p) = SplitFirst(decoded, ':');
                    auth = new AuthConfig
                    {
                        Type = AuthType.Basic,
                        Parameters = new Dictionary<string, string>
                        {
                            ["username"] = u,
                            ["password"] = p ?? string.Empty,
                        },
                    };
                    headers.Remove(authHeader);
                }
                catch
                {
                    // Malformed base64 — leave the header as-is.
                }
            }
        }

        // Cookies (-b) → merged Cookie header.
        if (cookiePairs.Count > 0)
        {
            var merged = string.Join("; ", cookiePairs);
            var existing = headers.FirstOrDefault(h =>
                string.Equals(h.Name, "Cookie", StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                headers.Remove(existing);
                var combined = string.IsNullOrEmpty(existing.Value)
                    ? merged
                    : existing.Value + "; " + merged;
                headers.Add(new KvPair("Cookie", combined));
            }
            else
            {
                headers.Add(new KvPair("Cookie", merged));
            }
        }

        var hasBody = dataChunks.Count > 0 || formFields.Count > 0;
        method ??= hasBody ? "POST" : "GET";

        BodyConfig body;
        if (formFields.Count > 0)
        {
            body = new BodyConfig
            {
                Mode = BodyMode.MultipartForm,
                MultipartItems = formFields.Select(f => new MultipartFormItem
                {
                    Name = f.Name,
                    Value = f.Value.StartsWith("@", StringComparison.Ordinal)
                        ? f.Value.Substring(1)
                        : f.Value,
                    Kind = f.Value.StartsWith("@", StringComparison.Ordinal) ? "file" : "text",
                    Enabled = true,
                }).ToList(),
            };
        }
        else if (dataChunks.Count > 0)
        {
            // -d / --data joins multiple values with '&'; --data-raw / --data-binary concatenate.
            var content = dataIsRaw
                ? string.Concat(dataChunks)
                : string.Join("&", dataChunks);

            var ctHeader = headers.FirstOrDefault(h =>
                string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase));
            var ct = ctHeader?.Value ?? string.Empty;

            BodyMode mode;
            if (ct.Contains("json", StringComparison.OrdinalIgnoreCase)) mode = BodyMode.Json;
            else if (ct.Contains("xml", StringComparison.OrdinalIgnoreCase)) mode = BodyMode.Xml;
            else if (ct.Contains("urlencoded", StringComparison.OrdinalIgnoreCase)) mode = BodyMode.FormUrlEncoded;
            else if (LooksLikeJson(content)) mode = BodyMode.Json;
            else mode = BodyMode.Text;

            if (mode == BodyMode.FormUrlEncoded)
            {
                body = new BodyConfig
                {
                    Mode = BodyMode.FormUrlEncoded,
                    FormData = ParseFormUrlEncoded(content),
                };
            }
            else
            {
                body = new BodyConfig { Mode = mode, Content = content };
            }
        }
        else
        {
            body = new BodyConfig { Mode = BodyMode.None };
        }

        var (cleanUrl, paramList) = SplitUrlQuery(url ?? string.Empty);

        return new RequestItem
        {
            Name = DeriveName(cleanUrl),
            Method = method.ToUpperInvariant(),
            Url = cleanUrl,
            Headers = headers,
            Params = paramList,
            Body = body,
            Auth = auth,
        };
    }

    // ----- Helpers -----
    private static (string Url, List<KvPair> ParamList) SplitUrlQuery(string url)
    {
        var qIdx = url.IndexOf('?');
        if (qIdx < 0) return (url, new List<KvPair>());
        return (url.Substring(0, qIdx), ParseFormUrlEncoded(url.Substring(qIdx + 1)));
    }

    private static List<KvPair> ParseFormUrlEncoded(string s)
    {
        var list = new List<KvPair>();
        if (string.IsNullOrEmpty(s)) return list;
        foreach (var pair in s.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            string n, v;
            if (eq < 0) { n = pair; v = string.Empty; }
            else { n = pair.Substring(0, eq); v = pair.Substring(eq + 1); }
            list.Add(new KvPair(SafeUnescape(n), SafeUnescape(v)));
        }
        return list;
    }

    private static string SafeUnescape(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch { return s; }
    }

    private static bool LooksLikeJson(string s)
    {
        var t = s.TrimStart();
        return t.Length > 0 && (t[0] == '{' || t[0] == '[');
    }

    private static void AddHeader(List<KvPair> headers, string h)
    {
        var idx = h.IndexOf(':');
        if (idx < 0) { headers.Add(new KvPair(h.Trim(), string.Empty)); return; }
        var name = h.Substring(0, idx).Trim();
        var val = h.Substring(idx + 1).Trim();
        headers.Add(new KvPair(name, val));
    }

    private static (string Name, string? Value) SplitFirst(string s, char sep)
    {
        var idx = s.IndexOf(sep);
        if (idx < 0) return (s, null);
        return (s.Substring(0, idx), s.Substring(idx + 1));
    }

    private static string EncodeUrlData(string v)
    {
        // --data-urlencode supports [name]=value form. Only the value gets percent-encoded.
        var eq = v.IndexOf('=');
        if (eq < 0) return Uri.EscapeDataString(v);
        var n = v.Substring(0, eq);
        var val = v.Substring(eq + 1);
        return string.IsNullOrEmpty(n)
            ? Uri.EscapeDataString(val)
            : n + "=" + Uri.EscapeDataString(val);
    }

    private static string DeriveName(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Pasted curl";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var leaf = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault();
            return string.IsNullOrEmpty(leaf) ? uri.Host : leaf;
        }
        return url;
    }

    // ----- Tokenizer -----
    internal static List<string> Tokenize(string raw)
    {
        var pre = Normalize(raw);
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        var hasToken = false;

        for (var i = 0; i < pre.Length; i++)
        {
            var c = pre[i];

            if (inSingle)
            {
                if (c == '\'') inSingle = false;
                else { sb.Append(c); hasToken = true; }
                continue;
            }
            if (inDouble)
            {
                if (c == '\\' && i + 1 < pre.Length)
                {
                    var n = pre[i + 1];
                    if (n == '"' || n == '\\' || n == '$' || n == '`' || n == '\n')
                    {
                        sb.Append(n); i++; hasToken = true; continue;
                    }
                    sb.Append(c); hasToken = true; continue;
                }
                if (c == '"') { inDouble = false; continue; }
                sb.Append(c); hasToken = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (hasToken) { tokens.Add(sb.ToString()); sb.Clear(); hasToken = false; }
                continue;
            }
            if (c == '\'') { inSingle = true; hasToken = true; continue; }
            if (c == '"') { inDouble = true; hasToken = true; continue; }
            if (c == '\\' && i + 1 < pre.Length)
            {
                sb.Append(pre[++i]); hasToken = true; continue;
            }
            sb.Append(c); hasToken = true;
        }
        if (hasToken) tokens.Add(sb.ToString());
        return tokens;
    }

    /// <summary>Convert cmd / PowerShell quoting into bash-equivalent syntax so a single
    /// tokenizer can handle all three formats. Also handles inputs where line continuations
    /// have been collapsed (the URL editor strips newlines on paste, leaving a trailing
    /// escape char with no newline behind it).</summary>
    internal static string Normalize(string raw)
    {
        var s = raw.Trim();

        // Line continuations (newline preserved).
        s = Regex.Replace(s, @"\\\r?\n[\t ]*", " ");   // bash
        s = Regex.Replace(s, @"\^\r?\n[\t ]*", " ");   // cmd
        s = Regex.Replace(s, @"`\r?\n[\t ]*", " ");    // PowerShell

        // Line continuations (newline already stripped by host, escape char left dangling).
        // Bash: "\ " — backslash followed by whitespace at top level.
        s = Regex.Replace(s, @"\\[\t ]+", " ");
        // PowerShell: "` " — backtick followed by whitespace at top level.
        s = Regex.Replace(s, @"`[\t ]+", " ");
        // (Cmd "^ " is handled below by the generic ^X → X pass.)

        // Cmd-style caret quoting (Chrome "Copy as cURL (cmd)").
        if (s.Contains("^\""))
        {
            // Preserve literal carets across the substitutions below.
            const char Sentinel = '\x01';
            s = s.Replace("^^", Sentinel.ToString());
            // ^\^"  →  \"   (escaped quote inside an arg)
            s = s.Replace("^\\^\"", "\\\"");
            // ^"    →  "    (arg boundary quote)
            s = s.Replace("^\"", "\"");
            // Any remaining ^X drops the caret (cmd escape of next char).
            s = Regex.Replace(s, @"\^(.)", "$1");
            s = s.Replace(Sentinel.ToString(), "^");
        }

        // PowerShell escapes inside double-quoted strings: `" → bash-style \"
        if (s.Contains("`\""))
        {
            s = s.Replace("`\"", "\\\"");
        }

        return s;
    }
}
