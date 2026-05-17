using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Vegha.Integrations.Wsdl;

/// <summary>
/// Converts a JSON document into the inner SOAP body XML for a specific WSDL operation,
/// using the WSDL's embedded XSD schemas to resolve element namespaces. The output mirrors
/// the shape <see cref="WsdlSampleEnvelopeGenerator"/> would produce, except element values
/// come from the JSON instead of <c>?</c> placeholders, and only properties present in the
/// JSON are emitted (no optional-element scaffolding).
///
/// JSON-to-XSD matching:
///   - Property names are matched case-insensitively against <c>xs:element name</c> in the
///     operation's input complex type (and any complexContent extension chain).
///   - Each matched element inherits its namespace from the surrounding schema's
///     <c>targetNamespace</c> (when <c>elementFormDefault="qualified"</c>).
///   - Properties with no schema match are emitted under the parent element's namespace —
///     better than dropping them and lets users layer custom extensions on top.
///   - JSON arrays expand to repeated sibling elements with the same name.
///   - Primitive JSON values become element text (numbers/booleans use their raw form;
///     strings are XML-escaped; null becomes a self-closing empty element).
///
/// Caller wraps the returned XML in a SOAP envelope (via <see cref="SoapEnvelopeBuilder"/>).
/// </summary>
public sealed class JsonToWsdlBodyConverter
{
    private static readonly XNamespace Wsdl = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    private const int MaxDepth = 12;

    private readonly XElement _definitions;
    private readonly Dictionary<string, XName> _messageElement = new(StringComparer.Ordinal);
    private readonly Dictionary<XName, XElement> _elements = new();
    private readonly Dictionary<XName, XElement> _types = new();
    private readonly Dictionary<XName, XElement> _groups = new();

    public JsonToWsdlBodyConverter(string wsdlXml)
    {
        var doc = XDocument.Parse(wsdlXml);
        _definitions = doc.Root ?? throw new InvalidDataException("WSDL: missing <definitions>");
        IndexMessages();
        IndexSchemas();
    }

    /// <summary>Returns the inner body XML for <paramref name="operationName"/> filled in
    /// from <paramref name="json"/>, or <c>null</c> if the operation can't be resolved or the
    /// JSON is invalid. The caller is expected to wrap it in a SOAP envelope.</summary>
    public string? Convert(string operationName, string json)
    {
        var op = _definitions
            .Descendants(Wsdl + "portType")
            .Elements(Wsdl + "operation")
            .FirstOrDefault(e => (string?)e.Attribute("name") == operationName);
        if (op is null) return null;

        var inputAttr = (string?)op.Element(Wsdl + "input")?.Attribute("message");
        if (string.IsNullOrEmpty(inputAttr)) return null;
        var messageName = LocalPart(inputAttr);
        if (!_messageElement.TryGetValue(messageName, out var elementQName)) return null;
        if (!_elements.TryGetValue(elementQName, out var elementDef)) return null;

        JsonDocument jsonDoc;
        try { jsonDoc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }

        using (jsonDoc)
        {
            var root = jsonDoc.RootElement;

            // Common shape: caller wraps params in a single { "OperationName": { ... } } key
            // (matches what the response JSON typically looks like). Unwrap so the user can
            // round-trip without reorganizing.
            if (root.ValueKind == JsonValueKind.Object)
            {
                using var enumerator = root.EnumerateObject();
                if (enumerator.MoveNext())
                {
                    var first = enumerator.Current;
                    var hasMore = enumerator.MoveNext();
                    if (!hasMore && string.Equals(first.Name, elementQName.LocalName, StringComparison.OrdinalIgnoreCase))
                        root = first.Value;
                }
            }

            var nsToPrefix = new Dictionary<string, string>(StringComparer.Ordinal);
            var prefixToNs = new Dictionary<string, string>(StringComparer.Ordinal);
            SeedPrefixesFromWsdl(nsToPrefix, prefixToNs);

            var rootPrefix = ResolvePrefix(elementQName.NamespaceName, nsToPrefix, prefixToNs);

            var sb = new StringBuilder();
            EmitElement(sb, elementDef, elementQName, rootPrefix, root,
                nsToPrefix, prefixToNs, depth: 0, indent: 0,
                visitedTypes: new HashSet<XName>(),
                parentNamespace: elementQName.NamespaceName);

            return InjectRootNamespaces(sb.ToString(), nsToPrefix);
        }
    }

    // ============================== Indexing ==============================

    private void IndexMessages()
    {
        foreach (var msg in _definitions.Elements(Wsdl + "message"))
        {
            var name = (string?)msg.Attribute("name");
            if (string.IsNullOrEmpty(name)) continue;
            var part = msg.Elements(Wsdl + "part").FirstOrDefault();
            if (part is null) continue;
            var elemAttr = (string?)part.Attribute("element");
            if (string.IsNullOrEmpty(elemAttr)) continue;
            _messageElement[name] = ResolveQName(elemAttr, part);
        }
    }

    private void IndexSchemas()
    {
        foreach (var schema in _definitions.Descendants(Xsd + "schema"))
        {
            var targetNs = (string?)schema.Attribute("targetNamespace") ?? string.Empty;
            foreach (var el in schema.Elements(Xsd + "element"))
            {
                var name = (string?)el.Attribute("name");
                if (!string.IsNullOrEmpty(name))
                    _elements[XName.Get(name, targetNs)] = el;
            }
            foreach (var t in schema.Elements(Xsd + "complexType"))
            {
                var name = (string?)t.Attribute("name");
                if (!string.IsNullOrEmpty(name))
                    _types[XName.Get(name, targetNs)] = t;
            }
            foreach (var t in schema.Elements(Xsd + "simpleType"))
            {
                var name = (string?)t.Attribute("name");
                if (!string.IsNullOrEmpty(name))
                    _types[XName.Get(name, targetNs)] = t;
            }
            foreach (var g in schema.Elements(Xsd + "group"))
            {
                var name = (string?)g.Attribute("name");
                if (!string.IsNullOrEmpty(name))
                    _groups[XName.Get(name, targetNs)] = g;
            }
        }
    }

    // ============================== Emit ==============================

    private void EmitElement(
        StringBuilder sb, XElement elementDef, XName elementName, string prefix,
        JsonElement value,
        Dictionary<string, string> nsToPrefix, Dictionary<string, string> prefixToNs,
        int depth, int indent, HashSet<XName> visitedTypes, string parentNamespace)
    {
        // <xs:element ref="qname"> indirection — substitute the referenced element.
        var refAttr = (string?)elementDef.Attribute("ref");
        if (!string.IsNullOrEmpty(refAttr))
        {
            var refName = ResolveQName(refAttr, elementDef);
            if (_elements.TryGetValue(refName, out var refDef))
            {
                var refPrefix = ResolvePrefix(refName.NamespaceName, nsToPrefix, prefixToNs);
                EmitElement(sb, refDef, refName, refPrefix, value,
                    nsToPrefix, prefixToNs, depth, indent, visitedTypes, parentNamespace);
                return;
            }
        }

        var openTag = string.IsNullOrEmpty(prefix) ? elementName.LocalName : $"{prefix}:{elementName.LocalName}";

        var typeAttr = (string?)elementDef.Attribute("type");
        XElement? typeDef = null;
        XName? typeName = null;
        if (!string.IsNullOrEmpty(typeAttr))
        {
            typeName = ResolveQName(typeAttr, elementDef);
            if (typeName.NamespaceName == Xsd.NamespaceName)
            {
                EmitSimpleLeaf(sb, indent, openTag, value);
                return;
            }
            _types.TryGetValue(typeName, out typeDef);
        }
        else
        {
            typeDef = elementDef.Element(Xsd + "complexType") ?? elementDef.Element(Xsd + "simpleType");
        }

        if (typeDef is null || typeDef.Name == Xsd + "simpleType")
        {
            EmitSimpleLeaf(sb, indent, openTag, value);
            return;
        }

        // complexType — only descend if JSON value is an object; otherwise dump as a leaf so
        // the user's value isn't lost.
        if (value.ValueKind != JsonValueKind.Object)
        {
            EmitSimpleLeaf(sb, indent, openTag, value);
            return;
        }

        if (depth >= MaxDepth || (typeName is not null && !visitedTypes.Add(typeName)))
        {
            EmitSimpleLeaf(sb, indent, openTag, value);
            return;
        }

        var schemaChildren = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        var typeNamespace = ResolveTypeNamespace(typeDef, elementName.NamespaceName);
        CollectChildElements(typeDef, schemaChildren, visitedBaseTypes: new HashSet<XName>());

        sb.Append(' ', indent).Append('<').Append(openTag).Append(">\n");
        foreach (var prop in value.EnumerateObject())
        {
            EmitProperty(sb, prop.Name, prop.Value, schemaChildren,
                nsToPrefix, prefixToNs, depth + 1, indent + 3, visitedTypes, typeNamespace);
        }
        sb.Append(' ', indent).Append("</").Append(openTag).Append(">\n");

        if (typeName is not null) visitedTypes.Remove(typeName);
    }

    private void EmitProperty(
        StringBuilder sb, string propertyName, JsonElement value,
        Dictionary<string, XElement> schemaChildren,
        Dictionary<string, string> nsToPrefix, Dictionary<string, string> prefixToNs,
        int depth, int indent, HashSet<XName> visitedTypes, string parentNamespace)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            // Repeated elements: emit one per array item.
            foreach (var item in value.EnumerateArray())
                EmitProperty(sb, propertyName, item, schemaChildren,
                    nsToPrefix, prefixToNs, depth, indent, visitedTypes, parentNamespace);
            return;
        }

        if (schemaChildren.TryGetValue(propertyName, out var childDef))
        {
            var (childName, childPrefix) = NameAndPrefix(childDef, nsToPrefix, prefixToNs);
            EmitElement(sb, childDef, childName, childPrefix, value,
                nsToPrefix, prefixToNs, depth, indent, visitedTypes, parentNamespace);
            return;
        }

        // No schema match — emit as a literal element under the parent's namespace so the
        // resulting XML still belongs to the right namespace tree (rather than silently
        // landing in no-namespace, which most SOAP servers reject).
        var fallbackName = string.IsNullOrEmpty(parentNamespace)
            ? XName.Get(propertyName)
            : XName.Get(propertyName, parentNamespace);
        var fallbackPrefix = string.IsNullOrEmpty(parentNamespace)
            ? string.Empty
            : ResolvePrefix(parentNamespace, nsToPrefix, prefixToNs);
        var openTag = string.IsNullOrEmpty(fallbackPrefix) ? fallbackName.LocalName : $"{fallbackPrefix}:{fallbackName.LocalName}";

        if (value.ValueKind == JsonValueKind.Object)
        {
            sb.Append(' ', indent).Append('<').Append(openTag).Append(">\n");
            foreach (var prop in value.EnumerateObject())
            {
                EmitProperty(sb, prop.Name, prop.Value, EmptyChildren,
                    nsToPrefix, prefixToNs, depth + 1, indent + 3, visitedTypes, parentNamespace);
            }
            sb.Append(' ', indent).Append("</").Append(openTag).Append(">\n");
        }
        else
        {
            EmitSimpleLeaf(sb, indent, openTag, value);
        }
    }

    private static readonly Dictionary<string, XElement> EmptyChildren =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Walks a complexType (including complexContent extension/restriction chains
    /// and group references) to gather every child <c>xs:element</c> declaration keyed by
    /// its local name. Cycles are guarded by <paramref name="visitedBaseTypes"/>.</summary>
    private void CollectChildElements(
        XElement complexType, Dictionary<string, XElement> children, HashSet<XName> visitedBaseTypes)
    {
        var complexContent = complexType.Element(Xsd + "complexContent");
        var simpleContent = complexType.Element(Xsd + "simpleContent");
        var ext = complexContent?.Element(Xsd + "extension") ?? simpleContent?.Element(Xsd + "extension");
        var restr = complexContent?.Element(Xsd + "restriction");
        var carrier = ext ?? restr ?? complexType;

        if (ext is not null)
        {
            var baseAttr = (string?)ext.Attribute("base");
            if (!string.IsNullOrEmpty(baseAttr))
            {
                var baseName = ResolveQName(baseAttr, ext);
                if (baseName.NamespaceName != Xsd.NamespaceName &&
                    _types.TryGetValue(baseName, out var baseTypeDef) &&
                    baseTypeDef.Name == Xsd + "complexType" &&
                    visitedBaseTypes.Add(baseName))
                {
                    CollectChildElements(baseTypeDef, children, visitedBaseTypes);
                }
            }
        }

        CollectFromParticles(carrier, children);
    }

    private void CollectFromParticles(XElement carrier, Dictionary<string, XElement> children)
    {
        foreach (var node in carrier.Elements())
        {
            if (node.Name == Xsd + "sequence" || node.Name == Xsd + "all" || node.Name == Xsd + "choice")
            {
                CollectFromParticles(node, children);
            }
            else if (node.Name == Xsd + "element")
            {
                var refAttr = (string?)node.Attribute("ref");
                string? localName;
                if (!string.IsNullOrEmpty(refAttr))
                    localName = LocalPart(refAttr);
                else
                    localName = (string?)node.Attribute("name");
                if (!string.IsNullOrEmpty(localName))
                    children[localName] = node;
            }
            else if (node.Name == Xsd + "group")
            {
                var refAttr = (string?)node.Attribute("ref");
                if (!string.IsNullOrEmpty(refAttr))
                {
                    var qname = ResolveQName(refAttr, node);
                    if (_groups.TryGetValue(qname, out var groupDef))
                        CollectFromParticles(groupDef, children);
                }
            }
        }
    }

    private static string ResolveTypeNamespace(XElement typeDef, string fallback)
    {
        var schema = typeDef.AncestorsAndSelf(Xsd + "schema").FirstOrDefault();
        var qualified = (string?)schema?.Attribute("elementFormDefault") == "qualified";
        if (!qualified) return fallback;
        return (string?)schema?.Attribute("targetNamespace") ?? fallback;
    }

    private static (XName Name, string Prefix) NameAndPrefix(
        XElement elementOrRef,
        Dictionary<string, string> nsToPrefix, Dictionary<string, string> prefixToNs)
    {
        var refAttr = (string?)elementOrRef.Attribute("ref");
        XName name;
        if (!string.IsNullOrEmpty(refAttr))
        {
            name = ResolveQName(refAttr, elementOrRef);
        }
        else
        {
            var local = (string?)elementOrRef.Attribute("name") ?? "elem";
            var schema = elementOrRef.AncestorsAndSelf(Xsd + "schema").FirstOrDefault();
            var qualified = (string?)schema?.Attribute("elementFormDefault") == "qualified";
            var ns = qualified ? (string?)schema?.Attribute("targetNamespace") ?? string.Empty : string.Empty;
            name = string.IsNullOrEmpty(ns) ? XName.Get(local) : XName.Get(local, ns);
        }
        var prefix = string.IsNullOrEmpty(name.NamespaceName)
            ? string.Empty
            : ResolvePrefix(name.NamespaceName, nsToPrefix, prefixToNs);
        return (name, prefix);
    }

    private static void EmitSimpleLeaf(StringBuilder sb, int indent, string openTag, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            sb.Append(' ', indent).Append('<').Append(openTag).Append("/>\n");
            return;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => XmlEscape(value.GetString() ?? string.Empty),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => XmlEscape(value.GetRawText()),
        };
        sb.Append(' ', indent).Append('<').Append(openTag).Append('>')
          .Append(text)
          .Append("</").Append(openTag).Append(">\n");
    }

    private static string XmlEscape(string s) =>
        new XText(s).ToString();

    // ============================== Namespace prefix mapping ==============================

    private void SeedPrefixesFromWsdl(
        Dictionary<string, string> nsToPrefix, Dictionary<string, string> prefixToNs)
    {
        foreach (var attr in _definitions.Attributes())
        {
            if (!attr.IsNamespaceDeclaration) continue;
            var prefix = attr.Name.LocalName == "xmlns" ? string.Empty : attr.Name.LocalName;
            var ns = attr.Value;
            if (string.IsNullOrEmpty(prefix)) continue;
            if (ns == Wsdl.NamespaceName || ns == Xsd.NamespaceName) continue;
            prefixToNs[prefix] = ns;
        }
    }

    private static string ResolvePrefix(
        string ns,
        Dictionary<string, string> nsToPrefix, Dictionary<string, string> prefixToNs)
    {
        if (string.IsNullOrEmpty(ns)) return string.Empty;
        if (nsToPrefix.TryGetValue(ns, out var existing)) return existing;

        var seeded = prefixToNs.FirstOrDefault(kv => kv.Value == ns);
        if (!string.IsNullOrEmpty(seeded.Key) && !nsToPrefix.ContainsValue(seeded.Key))
        {
            nsToPrefix[ns] = seeded.Key;
            return seeded.Key;
        }

        for (var i = 1; i < 1000; i++)
        {
            var p = "n" + i;
            if (!nsToPrefix.ContainsValue(p) && !prefixToNs.ContainsKey(p))
            {
                nsToPrefix[ns] = p;
                return p;
            }
        }
        var fallback = "ns" + Math.Abs(ns.GetHashCode());
        nsToPrefix[ns] = fallback;
        return fallback;
    }

    private static string InjectRootNamespaces(string body, Dictionary<string, string> nsToPrefix)
    {
        if (nsToPrefix.Count == 0) return body;

        var openIdx = body.IndexOf('<', StringComparison.Ordinal);
        if (openIdx < 0) return body;
        var closeIdx = body.IndexOf('>', openIdx);
        if (closeIdx < 0) return body;

        var sb = new StringBuilder();
        foreach (var (ns, prefix) in nsToPrefix)
            sb.Append(' ').Append("xmlns:").Append(prefix).Append("=\"").Append(ns).Append('"');

        return body[..closeIdx] + sb + body[closeIdx..];
    }

    private static XName ResolveQName(string qname, XElement context)
    {
        var idx = qname.IndexOf(':', StringComparison.Ordinal);
        if (idx < 0) return XName.Get(qname);
        var prefix = qname[..idx];
        var local = qname[(idx + 1)..];
        var ns = context.GetNamespaceOfPrefix(prefix);
        return ns is null ? XName.Get(local) : ns + local;
    }

    private static string LocalPart(string qname) =>
        qname.Contains(':', StringComparison.Ordinal)
            ? qname[(qname.IndexOf(':', StringComparison.Ordinal) + 1)..]
            : qname;
}
