using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Vegha.Integrations.Wsdl;

/// <summary>
/// Template-driven JSON-to-XML converter for SOAP request bodies. Given an existing SOAP
/// envelope (the body of a WSDL-imported request, where namespaces were materialized at
/// import time) and a JSON document, produces a new envelope whose <c>soap:Body</c>
/// content matches the JSON shape while preserving the namespaces, prefixes, and
/// envelope structure of the template.
///
/// Namespace resolution: walks the template tree and indexes every element by the path of
/// local names from the operation root, then assigns each new element the namespace of the
/// matching template path. This handles the multi-namespace WSDLs you see in practice
/// (e.g. operation root in <c>ns7</c>, IEC TC57 Header children in <c>ns8</c>, payload back
/// in <c>ns7</c>) — single-namespace envelopes still work since every child resolves to
/// the same namespace as its parent.
///
/// Why template-based, not WSDL-based: WSDL imports discard the source WSDL — only the
/// rendered envelope is persisted on the request. The envelope already carries the
/// namespace bindings the WSDL author chose, so re-fetching the WSDL is unnecessary as
/// long as the original envelope structure (with its full namespace tree) is intact.
///
/// JSON-shape handling:
///   - <c>{ "Header": ..., "Body": { "&lt;OpName&gt;": { params } } }</c> — full-envelope
///     JSON; the converter unwraps both layers to land on the operation params.
///   - <c>{ "&lt;OpName&gt;": { params } }</c> — operation-wrapped JSON; one layer of unwrap.
///   - <c>{ params }</c> — bare params; used as-is.
/// In all three cases the final XML keeps the original <c>soap:Body</c>'s child element
/// name + namespace; only its content is rewritten.
///
/// Limitations:
///   - JSON properties not present in the template fall back to the parent element's
///     namespace. Usually correct; can be wrong if the WSDL puts an optional element in a
///     different namespace.
///   - Attributes are not supported (no JSON convention for them).
/// </summary>
public static class JsonToSoapBodyConverter
{
    private static readonly XNamespace Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Soap12 = "http://www.w3.org/2003/05/soap-envelope";

    /// <summary>Returns the converted XML, or <c>null</c> if either input fails to parse,
    /// the template has no recognizable operation root, or the JSON shape can't be mapped.
    /// Callers should treat <c>null</c> as "no conversion" and fall back to default paste
    /// behavior.</summary>
    public static string? Convert(string templateXml, string json)
    {
        if (string.IsNullOrWhiteSpace(templateXml) || string.IsNullOrWhiteSpace(json))
            return null;

        XDocument template;
        try
        {
            // LoadOptions.None drops insignificant whitespace so the resulting XML is clean
            // when serialized — the surrounding workspace runs FormatXml on the converter's
            // output anyway, so any preserved whitespace would be discarded downstream.
            template = XDocument.Parse(templateXml, LoadOptions.None);
        }
        catch (XmlException) { return null; }

        JsonDocument jsonDoc;
        try { jsonDoc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }

        using (jsonDoc)
        {
            var root = template.Root;
            if (root is null) return null;

            // Two template shapes we recognize:
            //   1. Full envelope:  <soap:Envelope><soap:Body><OpName>…</OpName></soap:Body></soap:Envelope>
            //   2. Bare body:      <OpName>…</OpName>
            // Anything else, we bail (return null) so the caller falls back to a normal paste.
            var soapBody = FindSoapBody(root);
            var operationEl = soapBody is not null
                ? soapBody.Elements().FirstOrDefault()
                : root;
            if (operationEl is null) return null;

            // Index every element under the operation root by its local-name path so we can
            // resolve per-element namespaces during conversion.
            var nsMap = new Dictionary<string, XNamespace>(StringComparer.OrdinalIgnoreCase);
            LearnNamespaces(operationEl, parentPath: string.Empty, nsMap);

            var jsonRoot = jsonDoc.RootElement;
            jsonRoot = UnwrapForOperation(jsonRoot, operationEl.Name.LocalName);

            var newOp = BuildElement(
                name: operationEl.Name,
                value: jsonRoot,
                path: operationEl.Name.LocalName,
                nsMap);

            // Lift any namespace prefix declarations from the original operation root onto
            // the new one so children using those prefixes (e.g. ns8 for IEC headers) inherit
            // them at the operation root level rather than being re-declared per element. Not
            // strictly necessary — XDocument auto-declares missing prefixes inline — but it
            // matches the prefix layout SOAP tooling produces and keeps the output compact.
            foreach (var attr in operationEl.Attributes())
            {
                if (attr.IsNamespaceDeclaration && attr.Name.LocalName != "xmlns")
                    newOp.SetAttributeValue(attr.Name, attr.Value);
            }

            if (soapBody is not null)
            {
                soapBody.RemoveNodes();
                soapBody.Add(newOp);
                return template.ToString(SaveOptions.None);
            }

            return newOp.ToString(SaveOptions.None);
        }
    }

    private static XElement? FindSoapBody(XElement root)
    {
        foreach (var el in root.DescendantsAndSelf())
        {
            if (el.Name.LocalName != "Body") continue;
            var ns = el.Name.Namespace;
            if (ns == Soap11 || ns == Soap12) return el;
        }
        return null;
    }

    /// <summary>Records the namespace of every element under the operation root, keyed by
    /// the slash-joined chain of local names (e.g. <c>"GetAccountsReqMsg/Header/Verb"</c>).
    /// The first occurrence wins — if the same path appears more than once with different
    /// namespaces (rare, but possible with xs:choice or sloppy WSDLs), we keep what came
    /// first rather than oscillating between them.</summary>
    private static void LearnNamespaces(
        XElement el, string parentPath, Dictionary<string, XNamespace> map)
    {
        var path = parentPath.Length == 0
            ? el.Name.LocalName
            : parentPath + "/" + el.Name.LocalName;
        map.TryAdd(path, el.Name.Namespace);
        foreach (var child in el.Elements())
            LearnNamespaces(child, path, map);
    }

    /// <summary>Walks down the JSON tree to find the value that corresponds to the
    /// operation element's content. Handles full-envelope JSON ({ Header, Body: { Op: … } })
    /// and operation-wrapped JSON ({ Op: … }); bare-params JSON is returned unchanged.</summary>
    private static JsonElement UnwrapForOperation(JsonElement jsonRoot, string operationLocalName)
    {
        if (jsonRoot.ValueKind != JsonValueKind.Object) return jsonRoot;

        if (TryGetPropertyCi(jsonRoot, "Body", out var bodyValue) && bodyValue.ValueKind == JsonValueKind.Object)
            jsonRoot = bodyValue;

        if (jsonRoot.ValueKind != JsonValueKind.Object) return jsonRoot;

        var propertyCount = 0;
        JsonProperty single = default;
        foreach (var prop in jsonRoot.EnumerateObject())
        {
            propertyCount++;
            if (propertyCount == 1) single = prop;
            if (propertyCount > 1) break;
        }
        if (propertyCount == 1 &&
            string.Equals(single.Name, operationLocalName, StringComparison.OrdinalIgnoreCase))
        {
            return single.Value;
        }
        return jsonRoot;
    }

    private static bool TryGetPropertyCi(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    /// <summary>Builds an XElement tree from a JSON value. Each child's namespace is looked
    /// up by extending <paramref name="path"/> with the JSON property name and querying
    /// <paramref name="nsMap"/>; on miss, the child inherits the current element's namespace
    /// — the safe default since nested elements usually share the parent's namespace.</summary>
    private static XElement BuildElement(
        XName name, JsonElement value, string path,
        Dictionary<string, XNamespace> nsMap)
    {
        var el = new XElement(name);
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                break;
            case JsonValueKind.String:
                el.Value = value.GetString() ?? string.Empty;
                break;
            case JsonValueKind.True:
                el.Value = "true";
                break;
            case JsonValueKind.False:
                el.Value = "false";
                break;
            case JsonValueKind.Number:
                el.Value = value.GetRawText();
                break;
            case JsonValueKind.Object:
                foreach (var prop in value.EnumerateObject())
                {
                    var childPath = path + "/" + prop.Name;
                    var childNs = nsMap.TryGetValue(childPath, out var mapped)
                        ? mapped
                        : name.Namespace;
                    AppendChild(el, childNs + prop.Name, prop.Value, childPath, nsMap);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    AppendChild(el, name.Namespace + "item", item, path + "/item", nsMap);
                break;
            case JsonValueKind.Undefined:
            default:
                break;
        }
        return el;
    }

    private static void AppendChild(
        XElement parent, XName name, JsonElement value, string path,
        Dictionary<string, XNamespace> nsMap)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            // Repeated sibling elements with the same name — the standard maxOccurs="unbounded"
            // mapping. Each sibling shares the same path, so they all resolve to the same
            // namespace from the map.
            foreach (var item in value.EnumerateArray())
                parent.Add(BuildElement(name, item, path, nsMap));
        }
        else
        {
            parent.Add(BuildElement(name, value, path, nsMap));
        }
    }
}
