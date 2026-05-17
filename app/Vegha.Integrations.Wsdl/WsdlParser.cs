using System.Xml.Linq;

namespace Vegha.Integrations.Wsdl;

/// <summary>Lightweight WSDL parser. Pulls out the data the SOAP UI needs to drive
/// a request: service name, list of operations + their soap action + input/output message
/// element names + per-operation endpoint URL. Supports WSDL 1.1 (the common case —
/// SOAP 1.1 / SOAP 1.2 / plain HTTP bindings) and WSDL 2.0's <c>endpoint @address</c>.
///
/// Endpoint resolution rules (WSDL 1.1):
///   1. Each <c>&lt;wsdl:port&gt;</c> declares a binding by qname and an address (one of
///      <c>soap:address</c>, <c>soap12:address</c>, or <c>http:address</c>).
///   2. Each <c>&lt;wsdl:binding&gt;</c> declares the portType it implements via <c>type=qname</c>.
///   3. We thread operation → portType → binding → port → address so multi-port WSDLs
///      route each operation to its own service URL instead of crashing all five into
///      whichever <c>&lt;port&gt;</c> happens to appear first.
///   4. The top-level <c>EndpointUrl</c> is kept as a fallback (first non-empty address)
///      for callers that don't care about per-operation routing.</summary>
public static class WsdlParser
{
    private static readonly XNamespace Wsdl = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/wsdl/soap/";
    private static readonly XNamespace Soap12 = "http://schemas.xmlsoap.org/wsdl/soap12/";
    private static readonly XNamespace Http = "http://schemas.xmlsoap.org/wsdl/http/";
    private static readonly XNamespace Wsdl20 = "http://www.w3.org/ns/wsdl";

    public static WsdlDocument Parse(string wsdlXml)
    {
        var doc = XDocument.Parse(wsdlXml);
        var root = doc.Root ?? throw new InvalidDataException("WSDL: missing root element");

        // WSDL 2.0 uses a <description xmlns="http://www.w3.org/ns/wsdl"> root with
        // <interface>, <binding>, <service>/<endpoint> elements. Different enough from
        // 1.1 that we branch.
        if (root.Name.Namespace == Wsdl20) return Parse20(root);

        return Parse11(root);
    }

    // ============================== WSDL 1.1 ==============================

    private static WsdlDocument Parse11(XElement definitions)
    {
        var serviceName = (string?)definitions.Attribute("name") ?? "Service";
        var targetNamespace = (string?)definitions.Attribute("targetNamespace") ?? string.Empty;

        // 1) binding name → endpoint URL (from <service>/<port>/<*:address>).
        var bindingUrl = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var port in definitions.Descendants(Wsdl + "service").Descendants(Wsdl + "port"))
        {
            var bindingAttr = (string?)port.Attribute("binding");
            if (string.IsNullOrEmpty(bindingAttr)) continue;
            var bindingLocal = LocalName(bindingAttr);
            var addr = port.Element(Soap + "address")
                    ?? port.Element(Soap12 + "address")
                    ?? port.Element(Http + "address");
            var location = (string?)addr?.Attribute("location");
            if (!string.IsNullOrEmpty(location) && !bindingUrl.ContainsKey(bindingLocal))
                bindingUrl[bindingLocal] = location;
        }

        // 2) portType local name → list of binding names that implement it.
        var portTypeBindings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // 3) operation name → soapAction (from each binding's operation entry). When the
        //    same operation name appears under multiple bindings, last one wins; that
        //    matches Bruno + SoapUI behavior on quirky multi-binding WSDLs.
        var soapActions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var binding in definitions.Elements(Wsdl + "binding"))
        {
            var bindingName = (string?)binding.Attribute("name");
            var typeAttr = (string?)binding.Attribute("type");
            if (!string.IsNullOrEmpty(bindingName) && !string.IsNullOrEmpty(typeAttr))
            {
                var ptLocal = LocalName(typeAttr);
                if (!portTypeBindings.TryGetValue(ptLocal, out var list))
                    portTypeBindings[ptLocal] = list = new List<string>();
                list.Add(bindingName);
            }

            foreach (var op in binding.Elements(Wsdl + "operation"))
            {
                var name = (string?)op.Attribute("name");
                if (string.IsNullOrEmpty(name)) continue;
                var soapOp = op.Element(Soap + "operation") ?? op.Element(Soap12 + "operation");
                if (soapOp is null) continue;
                soapActions[name] = (string?)soapOp.Attribute("soapAction") ?? string.Empty;
            }
        }

        // 4) Walk portTypes. Each operation gets a URL by following its portType through
        //    bindings to ports.
        var operations = new List<WsdlOperation>();
        foreach (var portType in definitions.Elements(Wsdl + "portType"))
        {
            var ptName = (string?)portType.Attribute("name") ?? string.Empty;
            var bindingNames = portTypeBindings.TryGetValue(ptName, out var b) ? b : new List<string>();
            var portTypeUrl = bindingNames
                .Select(n => bindingUrl.TryGetValue(n, out var u) ? u : string.Empty)
                .FirstOrDefault(u => !string.IsNullOrEmpty(u)) ?? string.Empty;

            foreach (var op in portType.Elements(Wsdl + "operation"))
            {
                var name = (string?)op.Attribute("name");
                if (string.IsNullOrEmpty(name)) continue;
                var inputMsg = (string?)op.Element(Wsdl + "input")?.Attribute("message") ?? string.Empty;
                var outputMsg = (string?)op.Element(Wsdl + "output")?.Attribute("message") ?? string.Empty;
                soapActions.TryGetValue(name, out var action);
                operations.Add(new WsdlOperation(
                    Name: name,
                    SoapAction: action ?? string.Empty,
                    InputMessage: inputMsg,
                    OutputMessage: outputMsg,
                    EndpointUrl: portTypeUrl));
            }
        }

        var globalEndpoint = bindingUrl.Values.FirstOrDefault(u => !string.IsNullOrEmpty(u))
                          ?? string.Empty;

        return new WsdlDocument(
            ServiceName: serviceName,
            TargetNamespace: targetNamespace,
            EndpointUrl: globalEndpoint,
            Operations: operations);
    }

    // ============================== WSDL 2.0 ==============================

    /// <summary>WSDL 2.0 layout: <c>&lt;description&gt;</c> root, <c>&lt;interface&gt;</c>
    /// (replaces portType) with <c>&lt;operation&gt;</c>, <c>&lt;binding&gt;</c> referring
    /// to interface by qname, <c>&lt;service&gt;</c>/<c>&lt;endpoint @binding @address&gt;</c>
    /// for the URL. Same routing logic as 1.1, but the elements have different names and
    /// the address is an attribute, not a child element.</summary>
    private static WsdlDocument Parse20(XElement description)
    {
        var serviceName = (string?)description.Element(Wsdl20 + "service")?.Attribute("name")
                       ?? (string?)description.Attribute("name")
                       ?? "Service";
        var targetNamespace = (string?)description.Attribute("targetNamespace") ?? string.Empty;

        // binding-name → URL
        var bindingUrl = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var endpoint in description.Descendants(Wsdl20 + "endpoint"))
        {
            var bindingAttr = (string?)endpoint.Attribute("binding");
            var address = (string?)endpoint.Attribute("address");
            if (string.IsNullOrEmpty(bindingAttr) || string.IsNullOrEmpty(address)) continue;
            var bindingLocal = LocalName(bindingAttr);
            if (!bindingUrl.ContainsKey(bindingLocal)) bindingUrl[bindingLocal] = address;
        }

        // interface-name → bindings
        var interfaceBindings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var binding in description.Elements(Wsdl20 + "binding"))
        {
            var bindingName = (string?)binding.Attribute("name");
            var ifaceAttr = (string?)binding.Attribute("interface");
            if (string.IsNullOrEmpty(bindingName) || string.IsNullOrEmpty(ifaceAttr)) continue;
            var ifaceLocal = LocalName(ifaceAttr);
            if (!interfaceBindings.TryGetValue(ifaceLocal, out var list))
                interfaceBindings[ifaceLocal] = list = new List<string>();
            list.Add(bindingName);
        }

        var operations = new List<WsdlOperation>();
        foreach (var iface in description.Elements(Wsdl20 + "interface"))
        {
            var ifaceName = (string?)iface.Attribute("name") ?? string.Empty;
            var bindingNames = interfaceBindings.TryGetValue(ifaceName, out var b) ? b : new List<string>();
            var ifaceUrl = bindingNames
                .Select(n => bindingUrl.TryGetValue(n, out var u) ? u : string.Empty)
                .FirstOrDefault(u => !string.IsNullOrEmpty(u)) ?? string.Empty;

            foreach (var op in iface.Elements(Wsdl20 + "operation"))
            {
                var name = (string?)op.Attribute("name");
                if (string.IsNullOrEmpty(name)) continue;
                var inputMsg = (string?)op.Element(Wsdl20 + "input")?.Attribute("messageLabel")
                            ?? (string?)op.Element(Wsdl20 + "input")?.Attribute("element")
                            ?? string.Empty;
                var outputMsg = (string?)op.Element(Wsdl20 + "output")?.Attribute("messageLabel")
                             ?? (string?)op.Element(Wsdl20 + "output")?.Attribute("element")
                             ?? string.Empty;
                operations.Add(new WsdlOperation(
                    Name: name,
                    SoapAction: string.Empty,
                    InputMessage: inputMsg,
                    OutputMessage: outputMsg,
                    EndpointUrl: ifaceUrl));
            }
        }

        var globalEndpoint = bindingUrl.Values.FirstOrDefault(u => !string.IsNullOrEmpty(u))
                          ?? string.Empty;

        return new WsdlDocument(
            ServiceName: serviceName,
            TargetNamespace: targetNamespace,
            EndpointUrl: globalEndpoint,
            Operations: operations);
    }

    private static string LocalName(string qname) =>
        qname.Contains(':') ? qname[(qname.IndexOf(':') + 1)..] : qname;
}

/// <summary>One <c>&lt;portType&gt;/&lt;operation&gt;</c> with its binding-derived
/// SOAPAction, the qname references for input/output messages, and the endpoint URL
/// resolved by following portType → binding → port → address.</summary>
public sealed record WsdlOperation(
    string Name,
    string SoapAction,
    string InputMessage,
    string OutputMessage,
    string EndpointUrl);

public sealed record WsdlDocument(
    string ServiceName,
    string TargetNamespace,
    string EndpointUrl,
    IReadOnlyList<WsdlOperation> Operations);
