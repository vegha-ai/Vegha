using System.Text.RegularExpressions;
using System.Xml.Linq;
using Vegha.Core.Domain;
using Vegha.Integrations.Wsdl;

namespace Vegha.Core.Importers;

/// <summary>
/// Imports a SoapUI / ReadyAPI project <c>.xml</c> file into a <see cref="Collection"/>.
///
/// Two layouts are mapped:
///  • <b>Test suites</b> — <c>&lt;con:testSuite&gt;</c> → top-level <see cref="Folder"/>,
///    <c>&lt;con:testCase&gt;</c> → nested folder, and each <c>request</c> / <c>restrequest</c>
///    test step → a <see cref="RequestItem"/>. Non-request steps (groovy, properties, transfer…)
///    are skipped and noted in the case folder's docs.
///  • <b>Interface catalog</b> — SOAP <c>&lt;con:interface&gt;</c> operations and REST
///    resources/methods → one folder per interface.
///
/// When the project has any request test steps the test suites are authoritative and the
/// interface catalog is NOT separately emitted (avoids duplicate-looking requests). The
/// catalog is the fallback only for catalog-only projects.
///
/// SoapUI property-expansion tokens (<c>${#Project#p}</c>, <c>${#TestCase#p}</c>, <c>${p}</c>…)
/// are rewritten to Vegha <c>{{p}}</c> interpolation. SOAP request test steps already carry a
/// complete <c>&lt;soapenv:Envelope&gt;</c> — it is stored verbatim; <see cref="SoapEnvelopeBuilder"/>
/// is used only to synthesize a stub envelope for catalog operations with no sample request.
/// </summary>
public static class SoapUiImporter
{
    private static readonly XNamespace Con = "http://eviware.com/soapui/config";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public static Collection ImportFromFile(string path) =>
        ImportFromString(File.ReadAllText(path));

    public static Collection ImportFromString(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException("SoapUI: " + ex.Message, ex);
        }

        var root = doc.Root;
        if (root is null ||
            !root.Name.LocalName.Equals("soapui-project", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("SoapUI: root element is not <soapui-project>.");
        }

        var ctx = BuildContext(root);

        // Test steps win: if the project defines any request test step, the suites are
        // authoritative; otherwise fall back to the interface catalog.
        var suites = root.Elements(Con + "testSuite").ToList();
        var hasRequestSteps = suites
            .SelectMany(s => s.Descendants(Con + "testStep"))
            .Any(IsRequestStep);

        var folders = hasRequestSteps
            ? suites.Select(s => BuildSuiteFolder(s, ctx)).ToList()
            : BuildCatalogFolders(root, ctx);

        var name = (string?)root.Attribute("name");
        return new Collection
        {
            Name = string.IsNullOrWhiteSpace(name) ? "SoapUI Project" : name!,
            Variables = ReadProperties(root),
            Folders = folders,
        };
    }

    // ----------------------------- context -----------------------------

    /// <summary>Lookups resolved once up front so test steps (which only reference an
    /// interface/operation/method by name) can recover the SOAPAction and REST verb/path.</summary>
    private sealed record SoapUiContext(
        Dictionary<string, string> SoapActions,                 // "interface|operation" -> action
        Dictionary<string, (string Verb, string Path)> RestMethods); // "service|methodName" -> verb,path

    private static SoapUiContext BuildContext(XElement root)
    {
        var soapActions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var restMethods = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        foreach (var iface in root.Elements(Con + "interface"))
        {
            var ifaceName = (string?)iface.Attribute("name") ?? string.Empty;
            if (IsRestInterface(iface))
            {
                foreach (var resource in iface.Elements(Con + "resource"))
                    IndexRestResource(resource, string.Empty, ifaceName, restMethods);
            }
            else
            {
                foreach (var op in iface.Elements(Con + "operation"))
                {
                    var opName = (string?)op.Attribute("name") ?? string.Empty;
                    soapActions[ifaceName + "|" + opName] = (string?)op.Attribute("action") ?? string.Empty;
                }
            }
        }
        return new SoapUiContext(soapActions, restMethods);
    }

    private static void IndexRestResource(
        XElement resource, string parentPath, string service,
        Dictionary<string, (string, string)> restMethods)
    {
        var path = parentPath + ((string?)resource.Attribute("path") ?? string.Empty);
        foreach (var method in resource.Elements(Con + "method"))
        {
            var mName = (string?)method.Attribute("name") ?? string.Empty;
            var verb = (string?)method.Attribute("method") ?? "GET";
            restMethods[service + "|" + mName] = (verb, path);
        }
        foreach (var child in resource.Elements(Con + "resource"))
            IndexRestResource(child, path, service, restMethods);
    }

    // -------------------------- test suites ----------------------------

    private static Folder BuildSuiteFolder(XElement suite, SoapUiContext ctx)
    {
        var cases = suite.Elements(Con + "testCase")
            .Select(c => BuildCaseFolder(c, ctx))
            .ToList();
        return new Folder
        {
            Name = (string?)suite.Attribute("name") ?? "TestSuite",
            Folders = cases,
            Variables = ReadProperties(suite),
        };
    }

    private static Folder BuildCaseFolder(XElement testCase, SoapUiContext ctx)
    {
        var requests = new List<RequestItem>();
        var skipped = new List<string>();
        var seq = 0;

        foreach (var step in testCase.Elements(Con + "testStep"))
        {
            var type = ((string?)step.Attribute("type") ?? string.Empty).ToLowerInvariant();
            switch (type)
            {
                case "request":
                    requests.Add(BuildSoapStep(step, ctx, ++seq));
                    break;
                case "restrequest":
                    requests.Add(BuildRestStep(step, ctx, ++seq));
                    break;
                default:
                    skipped.Add(string.IsNullOrEmpty(type) ? "(unknown)" : type);
                    break;
            }
        }

        string? docs = null;
        if (skipped.Count > 0)
        {
            var grouped = skipped
                .GroupBy(s => s, StringComparer.Ordinal)
                .Select(g => $"{g.Key}({g.Count()})");
            docs = "Skipped non-request SoapUI steps: " + string.Join(", ", grouped) + ".";
        }

        return new Folder
        {
            Name = (string?)testCase.Attribute("name") ?? "TestCase",
            Requests = requests,
            Variables = ReadProperties(testCase),
            Docs = docs,
        };
    }

    private static RequestItem BuildSoapStep(XElement step, SoapUiContext ctx, int seq)
    {
        var name = (string?)step.Attribute("name") ?? "Request";
        var config = step.Element(Con + "config");
        var wrapper = config?.Elements(Con + "request").FirstOrDefault();

        var ifaceName = config?.Element(Con + "interface")?.Value ?? string.Empty;
        var opName = config?.Element(Con + "operation")?.Value ?? string.Empty;
        ctx.SoapActions.TryGetValue(ifaceName + "|" + opName, out var action);

        var endpoint = wrapper?.Element(Con + "endpoint")?.Value ?? string.Empty;
        var bodyXml = wrapper?.Element(Con + "request")?.Value;
        var assertions = wrapper?.Elements(Con + "assertion").ToList() ?? new List<XElement>();
        var creds = wrapper?.Element(Con + "credentials");

        return BuildSoapRequestItem(
            name, endpoint, action ?? string.Empty, bodyXml, assertions, creds, seq, settingsHost: wrapper);
    }

    private static RequestItem BuildRestStep(XElement step, SoapUiContext ctx, int seq)
    {
        var name = (string?)step.Attribute("name") ?? "Request";
        var config = step.Element(Con + "config");
        var reqEl = config?.Elements(Con + "restRequest").FirstOrDefault()
                 ?? config?.Elements(Con + "request").FirstOrDefault();

        var service = (string?)config?.Attribute("service") ?? string.Empty;
        var methodName = (string?)config?.Attribute("methodName") ?? string.Empty;
        var resourcePath = (string?)config?.Attribute("resourcePath") ?? string.Empty;

        var verb = string.Empty;
        if (ctx.RestMethods.TryGetValue(service + "|" + methodName, out var m))
        {
            verb = m.Verb;
            if (string.IsNullOrEmpty(resourcePath)) resourcePath = m.Path;
        }

        // An explicit <con:method> on the request element overrides the catalog lookup.
        var explicitVerb = reqEl?.Element(Con + "method")?.Value;
        if (!string.IsNullOrEmpty(explicitVerb)) verb = explicitVerb;
        if (string.IsNullOrEmpty(verb)) verb = "GET";

        if (reqEl is null)
        {
            return new RequestItem
            {
                Name = name,
                Kind = RequestKind.Http,
                Method = verb.ToUpperInvariant(),
                Url = ConvertVars(resourcePath),
                Sequence = seq,
            };
        }

        var endpoint = reqEl.Element(Con + "endpoint")?.Value ?? string.Empty;
        return BuildRestRequestItem(name, verb, endpoint, resourcePath, reqEl, seq);
    }

    // ------------------------ interface catalog ------------------------

    private static List<Folder> BuildCatalogFolders(XElement root, SoapUiContext ctx)
    {
        _ = ctx; // catalog requests resolve everything locally
        var folders = new List<Folder>();
        foreach (var iface in root.Elements(Con + "interface"))
        {
            folders.Add(IsRestInterface(iface)
                ? BuildRestInterfaceFolder(iface)
                : BuildSoapInterfaceFolder(iface));
        }
        return folders;
    }

    private static Folder BuildSoapInterfaceFolder(XElement iface)
    {
        var defaultEndpoint = FirstEndpoint(iface);
        var opFolders = new List<Folder>();

        // SoapUI: interface → operation → one or more sample <con:call> requests. Mirror that
        // hierarchy — operation becomes a subfolder, each call a request inside it.
        foreach (var op in iface.Elements(Con + "operation"))
        {
            var opName = (string?)op.Attribute("name") ?? "Operation";
            var action = (string?)op.Attribute("action") ?? string.Empty;
            var calls = op.Elements(Con + "call").ToList();

            var requests = new List<RequestItem>();
            var seq = 0;

            if (calls.Count == 0)
            {
                // Operation with no sample request — synthesize a single stub.
                requests.Add(BuildSoapRequestItem(
                    opName, defaultEndpoint, action, bodyXml: null,
                    Array.Empty<XElement>(), credentials: null, ++seq));
            }
            else
            {
                foreach (var call in calls)
                {
                    var callName = (string?)call.Attribute("name") ?? opName;
                    var bodyXml = call.Element(Con + "request")?.Value;
                    var endpoint = call.Element(Con + "endpoint")?.Value ?? defaultEndpoint;
                    var assertions = call.Elements(Con + "assertion").ToList();
                    var creds = call.Element(Con + "credentials");
                    requests.Add(BuildSoapRequestItem(
                        callName, endpoint, action, bodyXml, assertions, creds, ++seq, settingsHost: call));
                }
            }

            opFolders.Add(new Folder { Name = opName, Requests = requests });
        }

        return new Folder
        {
            Name = (string?)iface.Attribute("name") ?? "Interface",
            Folders = opFolders,
        };
    }

    private static Folder BuildRestInterfaceFolder(XElement iface)
    {
        var endpoint = FirstEndpoint(iface);
        var requests = new List<RequestItem>();
        var seq = 0;
        foreach (var resource in iface.Elements(Con + "resource"))
            CollectRestCatalog(resource, string.Empty, endpoint, requests, ref seq);

        return new Folder
        {
            Name = (string?)iface.Attribute("name") ?? "Interface",
            Requests = requests,
        };
    }

    private static void CollectRestCatalog(
        XElement resource, string parentPath, string endpoint,
        List<RequestItem> requests, ref int seq)
    {
        var path = parentPath + ((string?)resource.Attribute("path") ?? string.Empty);
        foreach (var method in resource.Elements(Con + "method"))
        {
            var verb = (string?)method.Attribute("method") ?? "GET";
            var mName = (string?)method.Attribute("name");
            var sampleRequests = method.Elements(Con + "request").ToList();

            if (sampleRequests.Count == 0)
            {
                seq++;
                requests.Add(new RequestItem
                {
                    Name = mName ?? verb,
                    Kind = RequestKind.Http,
                    Method = verb.ToUpperInvariant(),
                    Url = ConvertVars(CombineUrl(endpoint, path)),
                    Sequence = seq,
                });
                continue;
            }

            foreach (var req in sampleRequests)
            {
                seq++;
                var rName = (string?)req.Attribute("name") ?? mName ?? verb;
                var ep = req.Element(Con + "endpoint")?.Value ?? endpoint;
                requests.Add(BuildRestRequestItem(rName, verb, ep, path, req, seq));
            }
        }
        foreach (var child in resource.Elements(Con + "resource"))
            CollectRestCatalog(child, path, endpoint, requests, ref seq);
    }

    // -------------------------- request builders -----------------------

    private static RequestItem BuildSoapRequestItem(
        string name, string endpoint, string soapAction, string? bodyXml,
        IReadOnlyList<XElement> assertions, XElement? credentials, int seq,
        XElement? settingsHost = null)
    {
        var headers = new List<KvPair>();
        string content;

        if (!string.IsNullOrWhiteSpace(bodyXml))
        {
            // SoapUI stores a complete envelope on the request — keep it verbatim, but
            // normalize line endings: SoapUI often encodes carriage returns as &#xD;
            // entities, which would otherwise survive as stray \r glyphs in the editor.
            content = ConvertVars(NormalizeNewlines(bodyXml!).Trim());
            headers.Add(new KvPair("Content-Type", "text/xml; charset=utf-8"));
            headers.Add(new KvPair("SOAPAction", "\"" + soapAction + "\""));
        }
        else
        {
            // Catalog operation with no sample request — synthesize a stub envelope.
            var elem = XmlName(name);
            var built = SoapEnvelopeBuilder.Build(
                $"<{elem}>\n  <!-- TODO: parameters -->\n</{elem}>",
                soapAction, SoapEnvelopeBuilder.Version.Soap11);
            content = built.Body;
            headers.Add(new KvPair("Content-Type", built.ContentType));
            foreach (var h in built.Headers)
                headers.Add(new KvPair(h.Key, h.Value));
        }

        var (tests, docs) = BuildAssertions(assertions);
        return new RequestItem
        {
            Name = name,
            Kind = RequestKind.Soap,
            MetaType = "soap",
            Method = "POST",
            Url = ConvertVars(endpoint),
            Sequence = seq,
            Headers = headers,
            Body = new BodyConfig { Mode = BodyMode.Xml, Content = content },
            Auth = ReadCredentials(credentials),
            Tests = tests,
            Docs = docs,
            Soap = ReadSoapConfig(settingsHost),
        };
    }

    /// <summary>Translates a SoapUI request's <c>&lt;con:settings&gt;</c> into a <see cref="SoapConfig"/>.
    /// The WS-Security timestamp is driven by <c>...WsdlRequest@wss-time-to-live</c> — when present
    /// with a positive value, the migrated request emits a <c>&lt;wsu:Timestamp&gt;</c> on send.</summary>
    private static SoapConfig? ReadSoapConfig(XElement? settingsHost)
    {
        var settings = settingsHost?.Element(Con + "settings");
        if (settings is null) return null;

        WssTimestampConfig? timestamp = null;
        foreach (var setting in settings.Elements(Con + "setting"))
        {
            var id = (string?)setting.Attribute("id") ?? string.Empty;
            if (!id.EndsWith("@wss-time-to-live", StringComparison.OrdinalIgnoreCase)) continue;

            if (int.TryParse((setting.Value ?? string.Empty).Trim(), out var raw) && raw > 0)
            {
                // SoapUI stores the TTL in milliseconds (e.g. 60000). Smaller values are
                // already seconds — treat anything under 1000 as a literal second count.
                var seconds = raw >= 1000 ? raw / 1000 : raw;
                timestamp = new WssTimestampConfig { TimeToLiveSeconds = Math.Max(1, seconds) };
            }
        }

        return timestamp is null ? null : new SoapConfig { Timestamp = timestamp };
    }

    private static RequestItem BuildRestRequestItem(
        string name, string method, string endpoint, string resourcePath,
        XElement reqEl, int seq)
    {
        var (query, pathParams, headers) = ReadRestParams(reqEl, resourcePath);

        var bodyText = NormalizeNewlines(reqEl.Element(Con + "request")?.Value ?? string.Empty);
        var mediaType = (string?)reqEl.Attribute("mediaType");
        var mode = MediaTypeToBodyMode(mediaType, bodyText);

        if (mode != BodyMode.None && !string.IsNullOrEmpty(mediaType) &&
            !headers.Any(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
        {
            headers.Add(new KvPair("Content-Type", mediaType!));
        }

        var assertions = reqEl.Elements(Con + "assertion").ToList();
        var (tests, docs) = BuildAssertions(assertions);

        return new RequestItem
        {
            Name = name,
            Kind = RequestKind.Http,
            Method = string.IsNullOrEmpty(method) ? "GET" : method.ToUpperInvariant(),
            Url = ConvertVars(CombineUrl(endpoint, resourcePath)),
            Sequence = seq,
            Params = query,
            PathParams = pathParams,
            Headers = headers,
            Body = mode == BodyMode.None
                ? new BodyConfig()
                : new BodyConfig { Mode = mode, Content = ConvertVars(bodyText) },
            Tests = tests,
            Docs = docs,
        };
    }

    // ------------------------------ helpers -----------------------------

    private static bool IsRequestStep(XElement testStep)
    {
        var type = (string?)testStep.Attribute("type") ?? string.Empty;
        return type.Equals("request", StringComparison.OrdinalIgnoreCase)
            || type.Equals("restrequest", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRestInterface(XElement iface)
    {
        var t = (string?)iface.Attribute(Xsi + "type") ?? string.Empty;
        return t.Contains("Rest", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstEndpoint(XElement iface) =>
        iface.Element(Con + "endpoints")?.Elements(Con + "endpoint").FirstOrDefault()?.Value
        ?? string.Empty;

    private static List<KvPair> ReadProperties(XElement container)
    {
        var result = new List<KvPair>();
        var props = container.Element(Con + "properties");
        if (props is null) return result;

        foreach (var p in props.Elements(Con + "property"))
        {
            var n = (string?)p.Element(Con + "name");
            if (string.IsNullOrEmpty(n)) continue;
            var v = (string?)p.Element(Con + "value") ?? string.Empty;
            result.Add(new KvPair(n, ConvertVars(v)));
        }
        return result;
    }

    private static AuthConfig? ReadCredentials(XElement? creds)
    {
        if (creds is null) return null;
        var user = creds.Element(Con + "username")?.Value;
        var pass = creds.Element(Con + "password")?.Value;
        if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(pass)) return null;

        var wssType = creds.Element(Con + "wssPasswordType")?.Value;
        return new AuthConfig
        {
            Type = string.IsNullOrEmpty(wssType) ? AuthType.Basic : AuthType.Wsse,
            Parameters = new Dictionary<string, string>
            {
                ["username"] = ConvertVars(user ?? string.Empty),
                ["password"] = ConvertVars(pass ?? string.Empty),
            },
        };
    }

    private static (List<KvPair> Query, List<KvPair> Path, List<KvPair> Headers) ReadRestParams(
        XElement reqEl, string resourcePath)
    {
        var query = new List<KvPair>();
        var pathParams = new List<KvPair>();
        var headers = new List<KvPair>();

        var paramsEl = reqEl.Element(Con + "parameters");
        if (paramsEl is null) return (query, pathParams, headers);

        // Newer layout: <con:entry key=".." value=".."/> — no explicit style, so a token that
        // matches a {placeholder} in the resource path is a path param, everything else a query.
        foreach (var entry in paramsEl.Elements(Con + "entry"))
        {
            var key = (string?)entry.Attribute("key") ?? string.Empty;
            if (key.Length == 0) continue;
            var val = ConvertVars((string?)entry.Attribute("value") ?? string.Empty);
            if (resourcePath.Contains("{" + key + "}", StringComparison.Ordinal))
                pathParams.Add(new KvPair(key, val));
            else
                query.Add(new KvPair(key, val));
        }

        // Catalog layout: <con:parameter> with <con:name>/<con:value>/<con:style>.
        foreach (var p in paramsEl.Elements(Con + "parameter"))
        {
            var key = (string?)p.Element(Con + "name") ?? string.Empty;
            if (key.Length == 0) continue;
            var val = ConvertVars(
                (string?)p.Element(Con + "value")
                ?? (string?)p.Element(Con + "default")
                ?? string.Empty);
            var style = ((string?)p.Element(Con + "style") ?? "QUERY").ToUpperInvariant();
            switch (style)
            {
                case "TEMPLATE":
                case "MATRIX":
                    pathParams.Add(new KvPair(key, val));
                    break;
                case "HEADER":
                    headers.Add(new KvPair(key, val));
                    break;
                default:
                    query.Add(new KvPair(key, val));
                    break;
            }
        }
        return (query, pathParams, headers);
    }

    private static (string? Tests, string? Docs) BuildAssertions(IReadOnlyList<XElement> assertions)
    {
        if (assertions.Count == 0) return (null, null);

        var testLines = new List<string>();
        var docLines = new List<string>();

        foreach (var a in assertions)
        {
            var type = ((string?)a.Attribute("type") ?? string.Empty).Trim();
            var aName = (string?)a.Attribute("name");
            var label = string.IsNullOrEmpty(aName) ? type : aName!;

            switch (type)
            {
                case "Valid HTTP Status Codes":
                {
                    var codes = ParseCodes(ConfigText(a, "codes"));
                    if (codes.Length > 0)
                    {
                        testLines.Add(TestLine(label,
                            $"expect([{string.Join(", ", codes)}]).toContain(res.status);"));
                        docLines.Add($"- {type}: {string.Join(", ", codes)}");
                    }
                    else docLines.Add($"- {type}");
                    break;
                }
                case "Invalid HTTP Status Codes":
                {
                    var codes = ParseCodes(ConfigText(a, "codes"));
                    if (codes.Length > 0)
                    {
                        testLines.Add(TestLine(label,
                            $"expect([{string.Join(", ", codes)}]).not.toContain(res.status);"));
                        docLines.Add($"- {type}: {string.Join(", ", codes)}");
                    }
                    else docLines.Add($"- {type}");
                    break;
                }
                case "Simple Contains":
                case "Contains":
                {
                    var token = ConfigText(a, "token");
                    if (!string.IsNullOrEmpty(token))
                    {
                        testLines.Add(TestLine(label,
                            $"expect(res.getBodyAsText()).toContain({Js(token!)});"));
                        docLines.Add($"- {type}: contains \"{token}\"");
                    }
                    else docLines.Add($"- {type}");
                    break;
                }
                case "Simple NotContains":
                case "NotContains":
                {
                    var token = ConfigText(a, "token");
                    if (!string.IsNullOrEmpty(token))
                    {
                        testLines.Add(TestLine(label,
                            $"expect(res.getBodyAsText()).not.toContain({Js(token!)});"));
                        docLines.Add($"- {type}: does not contain \"{token}\"");
                    }
                    else docLines.Add($"- {type}");
                    break;
                }
                case "SOAP Response":
                case "Not SOAP Fault":
                    testLines.Add(TestLine(label, "expect(res.status).toBe(200);"));
                    docLines.Add($"- {type}");
                    break;
                case "SOAP Fault":
                    docLines.Add($"- {type} (asserts a SOAP fault — not auto-translated)");
                    break;
                case "Response SLA":
                {
                    var sla = (ConfigText(a, "SLA") ?? string.Empty).Trim();
                    if (long.TryParse(sla, out var ms) && ms > 0)
                    {
                        testLines.Add(TestLine(label,
                            $"expect(res.responseTime).toBeLessThan({ms});"));
                        docLines.Add($"- {type}: {ms} ms");
                    }
                    else docLines.Add($"- {type}");
                    break;
                }
                default:
                    docLines.Add(string.IsNullOrEmpty(type)
                        ? $"- {label} (not auto-translated)"
                        : $"- {type}: {label} (not auto-translated)");
                    break;
            }
        }

        var tests = testLines.Count > 0 ? string.Join("\n", testLines) : null;
        var docs = "Imported from SoapUI. Assertions:\n" + string.Join("\n", docLines);
        return (tests, docs);
    }

    private static string TestLine(string name, string body) =>
        $"test({Js(name)}, () => {{ {body} }});";

    /// <summary>Reads a value out of an <c>&lt;con:assertion&gt;</c>'s <c>&lt;con:configuration&gt;</c>
    /// block. The inner config elements are typically un-namespaced, so we match by local name.</summary>
    private static string? ConfigText(XElement assertion, string localName)
    {
        var cfg = assertion.Element(Con + "configuration");
        var el = cfg?.Descendants().FirstOrDefault(e =>
            e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
        return el?.Value;
    }

    private static int[] ParseCodes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<int>();
        return raw
            .Split(new[] { ',', ' ', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), out var n) ? n : -1)
            .Where(n => n >= 0)
            .ToArray();
    }

    private static BodyMode MediaTypeToBodyMode(string? mediaType, string content)
    {
        // No payload — no body, regardless of the declared media type (common on GETs).
        if (string.IsNullOrWhiteSpace(content)) return BodyMode.None;

        var mt = (mediaType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        if (mt is "application/json" || mt.EndsWith("+json", StringComparison.Ordinal))
            return BodyMode.Json;
        if (mt is "application/xml" or "text/xml" || mt.EndsWith("+xml", StringComparison.Ordinal))
            return BodyMode.Xml;
        if (mt.Contains("json", StringComparison.Ordinal)) return BodyMode.Json;
        if (mt.Contains("xml", StringComparison.Ordinal)) return BodyMode.Xml;
        return BodyMode.Text;
    }

    private static string CombineUrl(string? endpoint, string? path)
    {
        var e = (endpoint ?? string.Empty).TrimEnd('/');
        var p = path ?? string.Empty;
        if (p.Length == 0) return e;
        if (!p.StartsWith('/')) p = "/" + p;
        return e + p;
    }

    /// <summary>SoapUI property expansion → Vegha interpolation. <c>${#Project#baseUrl}</c>,
    /// <c>${#TestCase#id}</c>, <c>${StepName#token}</c> and plain <c>${name}</c> all collapse to
    /// <c>{{name}}</c> — the segment after the last <c>#</c>, whitespace stripped.</summary>
    private static string ConvertVars(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        return VarPattern.Replace(value, m =>
        {
            var raw = m.Groups[1].Value;
            var hash = raw.LastIndexOf('#');
            var name = hash >= 0 ? raw[(hash + 1)..] : raw;
            name = WhitespacePattern.Replace(name.Trim(), string.Empty);
            return name.Length == 0 ? m.Value : "{{" + name + "}}";
        });
    }

    /// <summary>Normalizes line endings in an imported body to plain <c>\n</c>. Beyond real
    /// CRLF / CR, SoapUI request bodies are frequently stored with the carriage return
    /// <em>literalized</em> as the two-character text <c>\r</c> (backslash + r) at each line
    /// end — an artifact of how the body was pasted/generated. Those literal sequences would
    /// otherwise surface as stray <c>\r</c> glyphs in the request editor, so they are stripped
    /// when they sit immediately before a newline.</summary>
    private static string NormalizeNewlines(string s) =>
        s.Replace("\\r\\n", "\n")  // fully-literalized "\r\n" text
         .Replace("\\r\n", "\n")   // literal "\r" text immediately before a real newline
         .Replace("\r\n", "\n")    // real CRLF
         .Replace("\r", "\n");     // real lone CR

    /// <summary>Coerces an arbitrary label into a valid XML element name for stub envelopes.</summary>
    private static string XmlName(string? s)
    {
        var cleaned = new string((s ?? string.Empty)
            .Where(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.')
            .ToArray());
        if (cleaned.Length == 0 || !(char.IsLetter(cleaned[0]) || cleaned[0] == '_'))
            cleaned = "Request" + cleaned;
        return cleaned;
    }

    private static string Js(string s) =>
        "'" + s
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\r", string.Empty)
            .Replace("\n", " ")
        + "'";

    private static readonly Regex VarPattern =
        new(@"\$\{([^}]*)\}", RegexOptions.Compiled);

    private static readonly Regex WhitespacePattern =
        new(@"\s+", RegexOptions.Compiled);
}
