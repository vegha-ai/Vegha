using Vegha.Core.Domain;
using Vegha.Integrations.Wsdl;

namespace Vegha.Core.Importers;

/// <summary>
/// Imports a SOAP WSDL document into a <see cref="Collection"/> — one request per
/// <c>portType/operation</c>. Each request is materialized as a directly-executable
/// HTTP POST: full SOAP 1.1 envelope in the body (with a SoapUI-style sample tree
/// derived from the WSDL's embedded XSD schemas), <c>SOAPAction</c> + <c>Content-Type</c>
/// headers, and the literal endpoint URL extracted from the WSDL's <c>&lt;wsdl:port&gt;</c>
/// (SOAP 1.1, SOAP 1.2, or HTTP binding) or WSDL 2.0 <c>&lt;endpoint @address&gt;</c>.
/// Multi-binding WSDLs route each operation through portType → binding → port so each
/// operation gets its own service URL.
/// </summary>
public static class WsdlImporter
{
    public static Collection ImportFromFile(string path) =>
        ImportFromString(File.ReadAllText(path));

    public static Collection ImportFromString(string wsdlXml)
    {
        WsdlDocument doc;
        try
        {
            doc = WsdlParser.Parse(wsdlXml);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException("WSDL: " + ex.Message, ex);
        }

        // The sample generator walks XSD types to produce SoapUI-style bodies. We build
        // one and reuse it across operations so message/element/type indexing happens
        // once. If construction fails (malformed schema, etc.), fall back to the stub
        // body so at least the operation list still imports.
        WsdlSampleEnvelopeGenerator? generator = null;
        try { generator = new WsdlSampleEnvelopeGenerator(wsdlXml); }
        catch { /* leave null — BuildRequest falls back. */ }

        return ToCollection(doc, generator);
    }

    public static Collection ToCollection(WsdlDocument doc) => ToCollection(doc, generator: null);

    private static Collection ToCollection(WsdlDocument doc, WsdlSampleEnvelopeGenerator? generator)
    {
        var name = string.IsNullOrWhiteSpace(doc.ServiceName) ? "WSDL Collection" : doc.ServiceName;

        var requests = new List<RequestItem>();
        var seq = 0;
        foreach (var op in doc.Operations)
        {
            seq++;
            requests.Add(BuildRequest(op, doc, generator, seq));
        }

        return new Collection
        {
            Name = name,
            Requests = requests,
        };
    }

    private static RequestItem BuildRequest(
        WsdlOperation op, WsdlDocument doc, WsdlSampleEnvelopeGenerator? generator, int seq)
    {
        var innerBody = generator?.GenerateForOperation(op.Name) ?? FallbackBody(op, doc);

        var built = SoapEnvelopeBuilder.Build(
            innerBody, op.SoapAction, SoapEnvelopeBuilder.Version.Soap11);

        var headers = new List<KvPair>
        {
            new("Content-Type", built.ContentType),
        };
        foreach (var h in built.Headers)
            headers.Add(new KvPair(h.Key, h.Value));

        // Per-operation URL (multi-port WSDLs assign different ports to different bindings);
        // fall back to the WSDL-level address when the routing chain dropped a link.
        var url = !string.IsNullOrEmpty(op.EndpointUrl) ? op.EndpointUrl : doc.EndpointUrl;

        return new RequestItem
        {
            Name = op.Name,
            Kind = RequestKind.Soap,
            Method = "POST",
            Url = url,
            Sequence = seq,
            Headers = headers,
            Body = new BodyConfig
            {
                Mode = BodyMode.Xml,
                Content = built.Body,
            },
            Docs = string.IsNullOrEmpty(op.SoapAction)
                ? null
                : $"SOAP operation `{op.Name}` (SOAPAction: `{op.SoapAction}`).",
        };
    }

    /// <summary>If the XSD walk fails (no schema, unresolvable refs), emit a minimal stub
    /// using the message's local name. Better than refusing the import.</summary>
    private static string FallbackBody(WsdlOperation op, WsdlDocument doc)
    {
        var localName = op.InputMessage.Contains(':')
            ? op.InputMessage[(op.InputMessage.IndexOf(':') + 1)..]
            : op.InputMessage;
        if (string.IsNullOrEmpty(localName)) localName = op.Name;

        var ns = string.IsNullOrEmpty(doc.TargetNamespace) ? "http://example.org/" : doc.TargetNamespace;
        return $"<{localName} xmlns=\"{ns}\">\n  <!-- TODO: parameters -->\n</{localName}>";
    }
}
