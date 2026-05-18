using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Vegha.Core.Domain;

namespace Vegha.Core.Requests;

/// <summary>
/// Applies a request's <see cref="SoapConfig"/> to its outgoing SOAP envelope just before
/// send: injects a <c>&lt;wsse:Security&gt;</c> header carrying a fresh <c>&lt;wsu:Timestamp&gt;</c>
/// and/or <c>&lt;wsse:UsernameToken&gt;</c>, plus WS-Addressing headers.
///
/// A new Created/Expires pair (and a fresh nonce) is generated on every call — WS-Security
/// timestamps expire, so they cannot be baked statically at import time. The SOAP version is
/// detected from the envelope's own namespace rather than trusted from config. Elements that
/// already exist (e.g. a hand-authored <c>&lt;wsse:Security&gt;</c>) are reused, never duplicated.
/// If the body is not a parseable SOAP envelope the input is returned unchanged.
/// </summary>
public static class SoapSecurityProcessor
{
    private const string WsseNs = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private const string WsuNs = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    private const string WsaNs = "http://www.w3.org/2005/08/addressing";
    private const string UtProfileNs = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0";

    private static readonly XNamespace Wsse = WsseNs;
    private static readonly XNamespace Wsu = WsuNs;
    private static readonly XNamespace Wsa = WsaNs;

    /// <summary>True when the config carries at least one outgoing WS-* section worth applying.</summary>
    public static bool HasOutgoing(SoapConfig? config) =>
        config is not null &&
        (config.Timestamp is not null || config.UsernameToken is not null || config.Addressing is not null);

    /// <summary>Returns <paramref name="envelopeXml"/> with the configured WS-Security / WS-Addressing
    /// headers injected. <paramref name="interpolate"/> resolves <c>{{var}}</c> placeholders in
    /// usernames, passwords and addressing values; pass null for identity.</summary>
    public static string Apply(string envelopeXml, SoapConfig? config, Func<string, string>? interpolate = null)
    {
        if (string.IsNullOrWhiteSpace(envelopeXml) || !HasOutgoing(config))
            return envelopeXml;

        interpolate ??= static s => s;

        XDocument doc;
        try
        {
            doc = XDocument.Parse(envelopeXml, LoadOptions.PreserveWhitespace);
        }
        catch
        {
            // Not parseable XML — leave the body untouched rather than break the send.
            return envelopeXml;
        }

        var root = doc.Root;
        if (root is null || !root.Name.LocalName.Equals("Envelope", StringComparison.Ordinal))
            return envelopeXml;

        var soapNs = root.Name.Namespace;
        var header = EnsureHeader(root, soapNs);

        if (config!.Timestamp is not null || config.UsernameToken is not null)
            ApplySecurity(header, soapNs, config, interpolate);

        if (config.Addressing is not null)
            ApplyAddressing(header, config.Addressing, interpolate);

        var sb = new StringBuilder();
        if (doc.Declaration is not null)
            sb.AppendLine(doc.Declaration.ToString());
        sb.Append(doc.ToString(SaveOptions.DisableFormatting));
        return sb.ToString();
    }

    /// <summary>Returns the envelope's <c>&lt;Header&gt;</c>, creating it as the first child of
    /// <c>&lt;Envelope&gt;</c> (before <c>&lt;Body&gt;</c>) when absent.</summary>
    private static XElement EnsureHeader(XElement envelope, XNamespace soapNs)
    {
        var header = envelope.Element(soapNs + "Header");
        if (header is not null) return header;

        header = new XElement(soapNs + "Header");
        var body = envelope.Element(soapNs + "Body");
        if (body is not null) body.AddBeforeSelf(header);
        else envelope.AddFirst(header);
        return header;
    }

    private static void ApplySecurity(
        XElement header, XNamespace soapNs, SoapConfig config, Func<string, string> interpolate)
    {
        var security = header.Elements(Wsse + "Security").FirstOrDefault();
        if (security is null)
        {
            security = new XElement(Wsse + "Security",
                new XAttribute(XNamespace.Xmlns + "wsse", WsseNs),
                new XAttribute(XNamespace.Xmlns + "wsu", WsuNs),
                new XAttribute(soapNs + "mustUnderstand", "1"));
            header.Add(security);
        }

        // Timestamp first — WS-Security best practice puts it ahead of the token.
        if (config.Timestamp is { } ts && security.Elements(Wsu + "Timestamp").FirstOrDefault() is null)
        {
            var created = DateTime.UtcNow;
            var expires = created.AddSeconds(Math.Max(1, ts.TimeToLiveSeconds));
            security.AddFirst(new XElement(Wsu + "Timestamp",
                new XAttribute(Wsu + "Id", "TS-" + Guid.NewGuid().ToString("N")),
                new XElement(Wsu + "Created", Iso(created)),
                new XElement(Wsu + "Expires", Iso(expires))));
        }

        if (config.UsernameToken is { } ut && security.Elements(Wsse + "UsernameToken").FirstOrDefault() is null)
            security.Add(BuildUsernameToken(ut, interpolate));
    }

    private static XElement BuildUsernameToken(WssUsernameTokenConfig ut, Func<string, string> interpolate)
    {
        var username = interpolate(ut.Username);
        var password = interpolate(ut.Password);
        var digest = ut.PasswordType == WssPasswordType.Digest;

        // PasswordDigest is defined as Base64(SHA1(nonce + created + password)); the nonce and
        // created therefore MUST travel with a digest token regardless of the add-* flags.
        var includeNonce = ut.AddNonce || digest;
        var includeCreated = ut.AddCreated || digest;

        var nonceBytes = RandomNumberGenerator.GetBytes(16);
        var createdStr = Iso(DateTime.UtcNow);

        string passwordValue;
        string passwordType;
        if (digest)
        {
            var material = new byte[nonceBytes.Length + Encoding.UTF8.GetByteCount(createdStr + password)];
            Buffer.BlockCopy(nonceBytes, 0, material, 0, nonceBytes.Length);
            Encoding.UTF8.GetBytes(createdStr + password, 0, (createdStr + password).Length,
                material, nonceBytes.Length);
            passwordValue = Convert.ToBase64String(SHA1.HashData(material));
            passwordType = UtProfileNs + "#PasswordDigest";
        }
        else
        {
            passwordValue = password;
            passwordType = UtProfileNs + "#PasswordText";
        }

        var token = new XElement(Wsse + "UsernameToken",
            new XAttribute(Wsu + "Id", "UsernameToken-" + Guid.NewGuid().ToString("N")),
            new XElement(Wsse + "Username", username),
            new XElement(Wsse + "Password",
                new XAttribute("Type", passwordType),
                passwordValue));

        if (includeNonce)
            token.Add(new XElement(Wsse + "Nonce",
                new XAttribute("EncodingType", UtProfileNs + "#Base64Binary"),
                Convert.ToBase64String(nonceBytes)));
        if (includeCreated)
            token.Add(new XElement(Wsu + "Created", createdStr));

        return token;
    }

    private static void ApplyAddressing(
        XElement header, WsAddressingConfig wsa, Func<string, string> interpolate)
    {
        EnsureWsaPrefix(header);

        void AddOnce(string localName, XElement element)
        {
            if (header.Elements(Wsa + localName).Any()) return;
            header.Add(element);
        }

        if (!string.IsNullOrWhiteSpace(wsa.Action))
            AddOnce("Action", new XElement(Wsa + "Action", interpolate(wsa.Action!)));

        if (!string.IsNullOrWhiteSpace(wsa.To))
            AddOnce("To", new XElement(Wsa + "To", interpolate(wsa.To!)));

        if (!string.IsNullOrWhiteSpace(wsa.ReplyTo))
            AddOnce("ReplyTo", new XElement(Wsa + "ReplyTo",
                new XElement(Wsa + "Address", interpolate(wsa.ReplyTo!))));

        var messageId = string.IsNullOrWhiteSpace(wsa.MessageId)
            ? (wsa.AutoMessageId ? "urn:uuid:" + Guid.NewGuid() : null)
            : interpolate(wsa.MessageId!);
        if (messageId is not null)
            AddOnce("MessageID", new XElement(Wsa + "MessageID", messageId));
    }

    private static void EnsureWsaPrefix(XElement header)
    {
        var declared = header.Attributes()
            .Any(a => a.IsNamespaceDeclaration && a.Value == WsaNs);
        if (!declared)
            header.Add(new XAttribute(XNamespace.Xmlns + "wsa", WsaNs));
    }

    /// <summary>WS-Security utility timestamp format: UTC, millisecond precision, trailing Z.</summary>
    private static string Iso(DateTime utc) =>
        utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
