using System.Text;
using System.Xml.Linq;

namespace Vegha.Integrations.Wsdl;

/// <summary>
/// Wraps user-provided body XML in a SOAP 1.1 or 1.2 envelope and supplies the
/// matching content-type. Body must be a fragment containing the operation's
/// request element (e.g., <c>&lt;ns:GetWeather xmlns:ns="..."&gt;...&lt;/ns:GetWeather&gt;</c>).
/// </summary>
public static class SoapEnvelopeBuilder
{
    private static readonly XNamespace Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Soap12 = "http://www.w3.org/2003/05/soap-envelope";

    public enum Version { Soap11, Soap12 }

    public sealed record BuiltEnvelope(string Body, string ContentType, IReadOnlyList<KeyValuePair<string, string>> Headers);

    public static BuiltEnvelope Build(string innerBodyXml, string soapAction, Version version = Version.Soap11)
    {
        var ns = version == Version.Soap11 ? Soap11 : Soap12;

        var bodyDoc = XDocument.Parse("<root xmlns:s=\"" + ns + "\">" + innerBodyXml + "</root>");
        var bodyContent = bodyDoc.Root!.Nodes().ToArray();

        var envelope = new XElement(ns + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", ns.NamespaceName),
            new XElement(ns + "Body", bodyContent));

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append(envelope);

        var headers = new List<KeyValuePair<string, string>>();
        if (version == Version.Soap11)
        {
            headers.Add(new("SOAPAction", "\"" + soapAction + "\""));
        }
        // SOAP 1.2 puts the action in the content-type's action="..." parameter.

        var contentType = version == Version.Soap11
            ? "text/xml; charset=utf-8"
            : $"application/soap+xml; charset=utf-8; action=\"{soapAction}\"";

        return new BuiltEnvelope(sb.ToString(), contentType, headers);
    }

    /// <summary>Pulls out the inner contents of <c>&lt;Body&gt;</c> from a SOAP response so the
    /// UI can show just the payload, not the envelope chrome.</summary>
    public static string ExtractBodyContents(string soapResponseXml)
    {
        try
        {
            var doc = XDocument.Parse(soapResponseXml);
            var ns = doc.Root?.Name.Namespace ?? Soap11;
            var body = doc.Root?.Element(ns + "Body");
            if (body is null) return soapResponseXml;
            return string.Concat(body.Nodes().Select(n => n.ToString()));
        }
        catch
        {
            return soapResponseXml;
        }
    }
}
