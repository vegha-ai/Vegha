using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace Vegha.Core.Scripting;

/// <summary>
/// Correctness-critical helpers backing the <c>require(...)</c> module shims (see
/// <see cref="JsModules"/>). Bound into the Jint engine as the internal global
/// <c>__vegha</c>; user scripts never touch it directly — they go through the JS
/// module wrappers (<c>crypto-js</c>, <c>xml2js</c>, <c>uuid</c>, <c>atob/btoa</c>, ...).
///
/// These operations (hashing, HMAC, XML parsing, base64) live in C# rather than hand-
/// rolled JS because getting them byte-for-byte correct in pure JS under Jint is
/// error-prone and slow — the same reason <c>axios</c> delegates to <c>bru.sendRequest</c>.
/// Every method takes/returns strings so Jint marshaling stays trivial (structured
/// results come back as JSON strings the JS side re-parses).
/// </summary>
public sealed class ScriptModuleHost
{
    // ---- Hashing / HMAC (crypto-js backing) ----

    /// <summary>Hex digest of <paramref name="message"/> (UTF-8) under the named algorithm.
    /// Supported: md5, sha1, sha256, sha384, sha512.</summary>
    public string hash(string algorithm, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message ?? string.Empty);
        using var algo = CreateHash(algorithm);
        return ToHex(algo.ComputeHash(bytes));
    }

    /// <summary>Hex HMAC digest of <paramref name="message"/> keyed by <paramref name="key"/>
    /// (both UTF-8). Supported: md5, sha1, sha256, sha384, sha512.</summary>
    public string hmac(string algorithm, string message, string key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key ?? string.Empty);
        var msgBytes = Encoding.UTF8.GetBytes(message ?? string.Empty);
        using var algo = CreateHmac(algorithm, keyBytes);
        return ToHex(algo.ComputeHash(msgBytes));
    }

    private static HashAlgorithm CreateHash(string algorithm) => (algorithm ?? "").ToLowerInvariant() switch
    {
        "md5" => MD5.Create(),
        "sha1" => SHA1.Create(),
        "sha256" => SHA256.Create(),
        "sha384" => SHA384.Create(),
        "sha512" => SHA512.Create(),
        _ => throw new NotSupportedException($"Unsupported hash algorithm '{algorithm}'."),
    };

    private static HMAC CreateHmac(string algorithm, byte[] key) => (algorithm ?? "").ToLowerInvariant() switch
    {
        "md5" => new HMACMD5(key),
        "sha1" => new HMACSHA1(key),
        "sha256" => new HMACSHA256(key),
        "sha384" => new HMACSHA384(key),
        "sha512" => new HMACSHA512(key),
        _ => throw new NotSupportedException($"Unsupported HMAC algorithm '{algorithm}'."),
    };

    // ---- Base64 (atob/btoa + Buffer backing) ----

    /// <summary>Base64-encode a binary string (each char = one byte / latin1) — the
    /// semantics of the WHATWG <c>btoa</c>.</summary>
    public string btoa(string data) =>
        Convert.ToBase64String(Latin1.GetBytes(data ?? string.Empty));

    /// <summary>Decode base64 to a binary string (each byte → one char / latin1) — the
    /// semantics of the WHATWG <c>atob</c>.</summary>
    public string atob(string data) =>
        Latin1.GetString(Convert.FromBase64String(data ?? string.Empty));

    /// <summary>Base64-encode UTF-8 text (Buffer.from(s,'utf8').toString('base64')).</summary>
    public string base64EncodeUtf8(string data) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(data ?? string.Empty));

    /// <summary>Decode base64 into UTF-8 text (Buffer.from(b64,'base64').toString('utf8')).</summary>
    public string base64DecodeUtf8(string data) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(data ?? string.Empty));

    /// <summary>UTF-8 text → hex (Buffer.from(s,'utf8').toString('hex')).</summary>
    public string utf8ToHex(string data) => ToHex(Encoding.UTF8.GetBytes(data ?? string.Empty));

    /// <summary>Hex → UTF-8 text (Buffer.from(hex,'hex').toString('utf8')).</summary>
    public string hexToUtf8(string hex) => Encoding.UTF8.GetString(FromHex(hex));

    /// <summary>Re-encode a hex digest as base64 (for crypto-js <c>.toString(enc.Base64)</c>).</summary>
    public string hexToBase64(string hex) => Convert.ToBase64String(FromHex(hex));

    /// <summary>base64 → hex (Buffer.from(b64,'base64').toString('hex')).</summary>
    public string base64ToHex(string b64) => ToHex(Convert.FromBase64String(b64 ?? string.Empty));

    // ---- UUID (uuid module backing) ----

    /// <summary>A random RFC-4122 v4 UUID.</summary>
    public string uuid() => Guid.NewGuid().ToString();

    // ---- XML → JSON (xml2js / xml2Json backing) ----

    /// <summary>Parses <paramref name="xml"/> and returns a JSON string shaped like
    /// <c>xml2js</c> with default options: element attributes under <c>$</c>, mixed/leaf
    /// text under <c>_</c> (leaf-only text collapses to the string), and every repeated or
    /// nested child element wrapped in an array (<c>explicitArray: true</c>). The JS side
    /// re-parses this with <c>JSON.parse</c>. Returns <c>"null"</c> on unparseable input,
    /// matching how a failed parse surfaces to callers.</summary>
    public string xmlToJson(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return "null";
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var root = doc.DocumentElement;
            if (root is null) return "null";

            var result = new Dictionary<string, object?> { [root.Name] = ConvertElement(root) };
            return JsonSerializer.Serialize(result);
        }
        catch (XmlException)
        {
            return "null";
        }
    }

    /// <summary>Returns the xml2js "value" of an element: a bare string for a leaf with no
    /// attributes, otherwise an object with <c>$</c> / <c>_</c> / arrayed child keys.</summary>
    private static object? ConvertElement(XmlElement element)
    {
        var hasAttrs = element.Attributes.Count > 0;
        var childElements = element.ChildNodes.OfType<XmlElement>().ToList();
        var text = string.Concat(element.ChildNodes.OfType<XmlNode>()
            .Where(n => n.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
            .Select(n => n.Value));

        // Leaf node with no attributes: xml2js emits the text directly (parent arrays it).
        if (!hasAttrs && childElements.Count == 0)
            return text;

        var obj = new Dictionary<string, object?>();

        if (hasAttrs)
        {
            var attrs = new Dictionary<string, object?>();
            foreach (XmlAttribute a in element.Attributes) attrs[a.Name] = a.Value;
            obj["$"] = attrs;
        }

        // Group repeated child tags into arrays (explicitArray: true → always an array).
        foreach (var group in childElements.GroupBy(c => c.Name))
            obj[group.Key] = group.Select(ConvertElement).ToList();

        if (!string.IsNullOrEmpty(text))
            obj["_"] = text;

        return obj;
    }

    // ---- helpers ----

    private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static byte[] FromHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
        if (hex.Length % 2 != 0) throw new FormatException("Hex string must have an even length.");
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}
