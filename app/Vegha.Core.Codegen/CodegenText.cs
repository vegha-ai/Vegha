using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Vegha.Core.Codegen;

/// <summary>String-literal escaping shared by the emitters. Each target language has its own
/// quoting rules; helpers here return the COMPLETE literal (quotes included) so call sites
/// can't mismatch the escape style and the delimiter.</summary>
internal static class CodegenText
{
    private static readonly JsonSerializerOptions s_relaxed = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Double-quoted literal with JSON escapes — valid in JavaScript, Java, Kotlin,
    /// Dart, Swift, Go, OCaml and R.</summary>
    public static string Json(string s) => JsonSerializer.Serialize(s, s_relaxed);

    /// <summary>Double-quoted C string ("\\", "\"", newlines as \n) — C and Objective-C.</summary>
    public static string CString(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Shell single-quoted literal; embedded ' becomes '\'' (same trick as curl).</summary>
    public static string ShellSingle(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>PHP single-quoted literal — only \ and ' need escaping, and $ stays inert.</summary>
    public static string PhpSingle(string s) =>
        "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    /// <summary>Ruby double-quoted literal — escapes \, " and the #{ interpolation opener.</summary>
    public static string RubyDouble(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("#{", "\\#{") + "\"";

    /// <summary>Best-effort URL split for emitters that address host and path separately
    /// (raw HTTP, http.client, Node native). Falls back to treating the whole string as the
    /// path when it isn't an absolute URL (e.g. an unresolved {{var}}).</summary>
    public static (string Scheme, string Host, int? Port, string PathAndQuery) SplitUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            int? port = uri.IsDefaultPort ? null : uri.Port;
            return (uri.Scheme, uri.Host, port, uri.PathAndQuery);
        }
        return ("https", url, null, "/");
    }
}
