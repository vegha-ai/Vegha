using System.Text;
using System.Xml.Linq;

namespace Vegha.Integrations.Wsdl;

/// <summary>
/// Generates a SoapUI-style sample request body for a WSDL operation by walking the
/// embedded XSD schemas. Resolves <c>portType/operation → message → part/element →
/// xs:element → xs:complexType</c> recursively, emitting <c>?</c> for simple-typed
/// leaves, an indented child tree for complex types, and <c>&lt;!--Optional:--&gt;</c>
/// comments before <c>minOccurs="0"</c> elements. Cycles in the type graph short-circuit
/// to a single <c>?</c> placeholder so we don't recurse forever.
///
/// This is best-effort, not a conformant XSD processor: attributes, xs:any, xs:choice
/// alternation, default/fixed values, and xs:restriction enumerations are skipped. The
/// goal is "the user gets a tree they can edit", matching what SoapUI does.
/// </summary>
public sealed class WsdlSampleEnvelopeGenerator
{
    private static readonly XNamespace Wsdl = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    // We cap nested recursion. Real WSDLs occasionally have self-referential types
    // (Tree → Tree[]); without a bound, we'd blow the stack.
    private const int MaxDepth = 12;

    private readonly XElement _definitions;
    private readonly Dictionary<string, XName> _messageElement = new(StringComparer.Ordinal);
    private readonly Dictionary<XName, XElement> _elements = new();
    private readonly Dictionary<XName, XElement> _types = new();
    private readonly Dictionary<XName, XElement> _attributeGroups = new();
    private readonly Dictionary<XName, XElement> _groups = new();

    public WsdlSampleEnvelopeGenerator(string wsdlXml)
    {
        var doc = XDocument.Parse(wsdlXml);
        _definitions = doc.Root ?? throw new InvalidDataException("WSDL: missing <definitions>");
        IndexMessages();
        IndexSchemas();
    }

    /// <summary>Returns the inner body XML (the operation's request element + its tree),
    /// or <c>null</c> if the operation/element/type can't be resolved. Caller wraps it
    /// in a SOAP envelope.</summary>
    public string? GenerateForOperation(string operationName)
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

        // Walk + emit body. Collect namespaces along the way so we can declare prefixes
        // on the root element only — child elements reference the prefixes.
        var nsToPrefix = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefixToNs = new Dictionary<string, string>(StringComparer.Ordinal);
        SeedPrefixesFromWsdl(nsToPrefix, prefixToNs);

        var rootPrefix = ResolvePrefix(elementQName.NamespaceName, nsToPrefix, prefixToNs);

        var inner = new StringBuilder();
        EmitElement(inner, elementDef, elementQName, rootPrefix,
            nsToPrefix, prefixToNs, depth: 0, indent: 0,
            visitedTypes: new HashSet<XName>(), wrappingMinOccurs: 1);

        // The first emitted element is our root — splice xmlns declarations into its
        // open tag. Format: "<prefix:Name>" (or "<Name>" if prefix empty). We injected
        // a marker so we can find the exact spot.
        return InjectRootNamespaces(inner.ToString(), nsToPrefix);
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
            IndexSchemaContent(schema, targetNs);
        }
    }

    private void IndexSchemaContent(XElement schema, string targetNs)
    {
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
        foreach (var ag in schema.Elements(Xsd + "attributeGroup"))
        {
            var name = (string?)ag.Attribute("name");
            if (!string.IsNullOrEmpty(name))
                _attributeGroups[XName.Get(name, targetNs)] = ag;
        }
    }

    // ============================== Emit ==============================

    private void EmitElement(
        StringBuilder sb, XElement elementDef, XName elementName, string prefix,
        Dictionary<string, string> nsToPrefix, Dictionary<string, string> prefixToNs,
        int depth, int indent, HashSet<XName> visitedTypes, int wrappingMinOccurs)
    {
        // <xs:element ref="qname"> — substitute the referenced element + skip our own name
        // (the ref's name wins).
        var refAttr = (string?)elementDef.Attribute("ref");
        if (!string.IsNullOrEmpty(refAttr))
        {
            var refName = ResolveQName(refAttr, elementDef);
            if (_elements.TryGetValue(refName, out var refDef))
            {
                var refPrefix = ResolvePrefix(refName.NamespaceName, nsToPrefix, prefixToNs);
                EmitElement(sb, refDef, refName, refPrefix,
                    nsToPrefix, prefixToNs, depth, indent, visitedTypes, wrappingMinOccurs);
                return;
            }
            // Unresolved ref — emit an empty placeholder so the user notices.
            EmitOptionalComment(sb, indent, wrappingMinOccurs);
            sb.Append(' ', indent).Append('<').Append(QualifyName(refName, nsToPrefix, prefixToNs)).Append("/>\n");
            return;
        }

        EmitOptionalComment(sb, indent, wrappingMinOccurs);

        var openTag = string.IsNullOrEmpty(prefix) ? elementName.LocalName : $"{prefix}:{elementName.LocalName}";

        // Resolve type: explicit type=qname, inline <xs:complexType>, inline <xs:simpleType>, or anyType.
        var typeAttr = (string?)elementDef.Attribute("type");
        XElement? typeDef = null;
        XName? typeName = null;
        if (!string.IsNullOrEmpty(typeAttr))
        {
            typeName = ResolveQName(typeAttr, elementDef);
            if (typeName.NamespaceName == Xsd.NamespaceName)
            {
                // Built-in simple type — emit "?" leaf, nothing more.
                EmitSimpleLeaf(sb, indent, openTag);
                return;
            }
            _types.TryGetValue(typeName, out typeDef);
        }
        else
        {
            typeDef = elementDef.Element(Xsd + "complexType") ?? elementDef.Element(Xsd + "simpleType");
        }

        if (typeDef is null)
        {
            // No type info → emit "?" leaf.
            EmitSimpleLeaf(sb, indent, openTag);
            return;
        }

        if (typeDef.Name == Xsd + "simpleType")
        {
            EmitSimpleLeaf(sb, indent, openTag);
            return;
        }

        // complexType: descend.
        if (depth >= MaxDepth || (typeName is not null && !visitedTypes.Add(typeName)))
        {
            EmitSimpleLeaf(sb, indent, openTag);
            return;
        }

        sb.Append(' ', indent).Append('<').Append(openTag).Append(">\n");

        EmitComplexTypeContent(sb, typeDef, nsToPrefix, prefixToNs, depth + 1, indent + 3, visitedTypes);

        sb.Append(' ', indent).Append("</").Append(openTag).Append(">\n");

        if (typeName is not null) visitedTypes.Remove(typeName);
    }

    private void EmitComplexTypeContent(
        StringBuilder sb, XElement complexType,
        Dictionary<string, string> nsToPrefix, Dictionary<string, string> prefixToNs,
        int depth, int indent, HashSet<XName> visitedTypes)
    {
        // Handle complexContent/simpleContent extensions: pull base type's content first,
        // then this type's additions.
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
                    baseTypeDef.Name == Xsd + "complexType")
                {
                    if (visitedTypes.Add(baseName))
                    {
                        EmitComplexTypeContent(sb, baseTypeDef, nsToPrefix, prefixToNs, depth, indent, visitedTypes);
                        visitedTypes.Remove(baseName);
                    }
                }
            }
        }

        // sequence / all / choice — treat them all the same (emit one of each child).
        EmitParticles(sb, carrier, nsToPrefix, prefixToNs, depth, indent, visitedTypes);
    }

    private void EmitParticles(
        StringBuilder sb, XElement carrier,
        Dictionary<string, string> nsToPrefix, Dictionary<string, string> prefixToNs,
        int depth, int indent, HashSet<XName> visitedTypes)
    {
        foreach (var node in carrier.Elements())
        {
            if (node.Name == Xsd + "sequence" || node.Name == Xsd + "all" || node.Name == Xsd + "choice")
            {
                EmitParticles(sb, node, nsToPrefix, prefixToNs, depth, indent, visitedTypes);
            }
            else if (node.Name == Xsd + "element")
            {
                var minOccurs = (string?)node.Attribute("minOccurs") == "0" ? 0 : 1;
                var (childName, childPrefix) = NameAndPrefix(node, nsToPrefix, prefixToNs);
                EmitElement(sb, node, childName, childPrefix,
                    nsToPrefix, prefixToNs, depth, indent, visitedTypes, minOccurs);
            }
            else if (node.Name == Xsd + "group")
            {
                var refAttr = (string?)node.Attribute("ref");
                if (!string.IsNullOrEmpty(refAttr))
                {
                    var qname = ResolveQName(refAttr, node);
                    if (_groups.TryGetValue(qname, out var groupDef))
                        EmitParticles(sb, groupDef, nsToPrefix, prefixToNs, depth, indent, visitedTypes);
                }
            }
        }
    }

    private (XName Name, string Prefix) NameAndPrefix(
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
            // Local name's namespace = the surrounding schema's targetNamespace
            // (only when elementFormDefault="qualified"; else no namespace).
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

    private static void EmitSimpleLeaf(StringBuilder sb, int indent, string openTag)
    {
        sb.Append(' ', indent).Append('<').Append(openTag).Append(">?</").Append(openTag).Append(">\n");
    }

    private static void EmitOptionalComment(StringBuilder sb, int indent, int minOccurs)
    {
        if (minOccurs == 0)
            sb.Append(' ', indent).Append("<!--Optional:-->\n");
    }

    // ============================== Namespace prefix mapping ==============================

    /// <summary>Pre-load prefix-to-namespace mappings from the WSDL root so we keep the
    /// service author's chosen prefixes (e.g. "tns", "ns3", "mes") instead of inventing
    /// our own — matches SoapUI's behavior and gives the user something familiar.</summary>
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
            // Don't seed both directions to empty — let the assigner pick.
            prefixToNs[prefix] = ns;
            // Don't claim the namespace yet; only do that when actually used so we don't
            // emit unused xmlns declarations on the root.
        }
    }

    private static string ResolvePrefix(
        string ns,
        Dictionary<string, string> nsToPrefix, Dictionary<string, string> prefixToNs)
    {
        if (string.IsNullOrEmpty(ns)) return string.Empty;
        if (nsToPrefix.TryGetValue(ns, out var existing)) return existing;

        // First try: any seeded prefix that maps to this ns.
        var seeded = prefixToNs.FirstOrDefault(kv => kv.Value == ns);
        if (!string.IsNullOrEmpty(seeded.Key) && !nsToPrefix.ContainsValue(seeded.Key))
        {
            nsToPrefix[ns] = seeded.Key;
            return seeded.Key;
        }

        // Fall back: generate "n1", "n2", ... avoiding collisions.
        for (var i = 1; i < 1000; i++)
        {
            var p = "n" + i;
            if (!nsToPrefix.ContainsValue(p) && !prefixToNs.ContainsKey(p))
            {
                nsToPrefix[ns] = p;
                return p;
            }
        }
        // Pathological — give up and use ns name hash. Should never happen.
        var fallback = "ns" + Math.Abs(ns.GetHashCode());
        nsToPrefix[ns] = fallback;
        return fallback;
    }

    private static string QualifyName(
        XName name,
        Dictionary<string, string> nsToPrefix, Dictionary<string, string> prefixToNs)
    {
        if (string.IsNullOrEmpty(name.NamespaceName)) return name.LocalName;
        var prefix = ResolvePrefix(name.NamespaceName, nsToPrefix, prefixToNs);
        return $"{prefix}:{name.LocalName}";
    }

    private static string InjectRootNamespaces(string body, Dictionary<string, string> nsToPrefix)
    {
        if (nsToPrefix.Count == 0) return body;

        // Find the first '>' that ends the root open tag and splice xmlns attrs in.
        var openIdx = body.IndexOf('<');
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
        var idx = qname.IndexOf(':');
        if (idx < 0) return XName.Get(qname);
        var prefix = qname[..idx];
        var local = qname[(idx + 1)..];
        var ns = context.GetNamespaceOfPrefix(prefix);
        return ns is null ? XName.Get(local) : ns + local;
    }

    private static string LocalPart(string qname) =>
        qname.Contains(':') ? qname[(qname.IndexOf(':') + 1)..] : qname;
}
