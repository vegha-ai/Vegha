using System.Xml.Linq;
using Vegha.Integrations.Wsdl;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Integrations.Wsdl;

public class JsonToSoapBodyConverterTests
{
    /// <summary>The shape WsdlImporter produces: full SOAP 1.1 envelope, soap:Body wraps
    /// an operation request element in the WSDL's target namespace with prefix "msg".</summary>
    private const string EnvelopeTemplate = """
        <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/" xmlns:msg="http://acme.test/wx/msg">
          <soapenv:Header />
          <soapenv:Body>
            <msg:GetWeatherReq>
              <msg:city>?</msg:city>
            </msg:GetWeatherReq>
          </soapenv:Body>
        </soapenv:Envelope>
        """;

    [Fact]
    public void Convert_BareParamsJson_ReplacesBodyContentsWithMatchingNamespace()
    {
        var result = JsonToSoapBodyConverter.Convert(EnvelopeTemplate, """{"city":"Seattle","units":"metric"}""");

        result.Should().NotBeNull();
        var doc = XDocument.Parse(result!);
        var msgNs = (XNamespace)"http://acme.test/wx/msg";
        var soapNs = (XNamespace)"http://schemas.xmlsoap.org/soap/envelope/";

        var body = doc.Descendants(soapNs + "Body").Single();
        var op = body.Elements().Single();
        op.Name.Should().Be(msgNs + "GetWeatherReq");
        op.Element(msgNs + "city")!.Value.Should().Be("Seattle");
        op.Element(msgNs + "units")!.Value.Should().Be("metric");
    }

    [Fact]
    public void Convert_OperationWrappedJson_UnwrapsOneLayer()
    {
        var result = JsonToSoapBodyConverter.Convert(EnvelopeTemplate, """{"GetWeatherReq":{"city":"NYC"}}""")!;
        var doc = XDocument.Parse(result);
        var msgNs = (XNamespace)"http://acme.test/wx/msg";

        var op = doc.Descendants(msgNs + "GetWeatherReq").Single();
        op.Element(msgNs + "city")!.Value.Should().Be("NYC");
        // No double-nesting.
        op.Elements(msgNs + "GetWeatherReq").Should().BeEmpty();
    }

    [Fact]
    public void Convert_FullEnvelopeJson_UnwrapsBodyAndOperation()
    {
        // Mirrors what the user pastes when copying a captured response that's been JSON-ified
        // by their tooling: { Header: ..., Body: { OpReq: { ...params } } }.
        const string envelopeJson = """
            {
              "Header": null,
              "Body": {
                "GetWeatherReq": {
                  "city": "Boston",
                  "units": "imperial"
                }
              }
            }
            """;
        var result = JsonToSoapBodyConverter.Convert(EnvelopeTemplate, envelopeJson)!;
        var doc = XDocument.Parse(result);
        var msgNs = (XNamespace)"http://acme.test/wx/msg";

        var op = doc.Descendants(msgNs + "GetWeatherReq").Single();
        op.Element(msgNs + "city")!.Value.Should().Be("Boston");
        op.Element(msgNs + "units")!.Value.Should().Be("imperial");
    }

    [Fact]
    public void Convert_PreservesEnvelopePrefixes()
    {
        var result = JsonToSoapBodyConverter.Convert(EnvelopeTemplate, """{"city":"X"}""")!;
        // Both the soapenv: prefix on the envelope and the msg: prefix on the operation
        // should round-trip — those came from the WSDL author and should stay.
        result.Should().Contain("soapenv:Envelope");
        result.Should().Contain("msg:GetWeatherReq");
        result.Should().Contain("msg:city");
    }

    [Fact]
    public void Convert_ArrayProperty_EmitsRepeatedSiblingElements()
    {
        var result = JsonToSoapBodyConverter.Convert(EnvelopeTemplate, """{"city":["NYC","LA","SF"]}""")!;
        var doc = XDocument.Parse(result);
        var msgNs = (XNamespace)"http://acme.test/wx/msg";

        var cities = doc.Descendants(msgNs + "city").ToList();
        cities.Should().HaveCount(3);
        cities.Select(c => c.Value).Should().BeEquivalentTo(new[] { "NYC", "LA", "SF" });
    }

    [Fact]
    public void Convert_NestedObjects_InheritOperationNamespace()
    {
        const string nested = """
            {
              "filter": {
                "min": 10,
                "max": 100
              },
              "active": true
            }
            """;
        var result = JsonToSoapBodyConverter.Convert(EnvelopeTemplate, nested)!;
        var doc = XDocument.Parse(result);
        var msgNs = (XNamespace)"http://acme.test/wx/msg";

        var op = doc.Descendants(msgNs + "GetWeatherReq").Single();
        var filter = op.Element(msgNs + "filter")!;
        filter.Element(msgNs + "min")!.Value.Should().Be("10");
        filter.Element(msgNs + "max")!.Value.Should().Be("100");
        op.Element(msgNs + "active")!.Value.Should().Be("true");
    }

    [Fact]
    public void Convert_NullJsonValue_EmitsEmptyElement()
    {
        var result = JsonToSoapBodyConverter.Convert(EnvelopeTemplate, """{"city":null}""")!;
        var doc = XDocument.Parse(result);
        var msgNs = (XNamespace)"http://acme.test/wx/msg";

        var city = doc.Descendants(msgNs + "city").Single();
        city.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Convert_StringWithMarkup_IsXmlEscaped()
    {
        var result = JsonToSoapBodyConverter.Convert(EnvelopeTemplate, """{"city":"<bad>&\"weird\""}""")!;
        result.Should().Contain("&lt;bad&gt;");
        result.Should().Contain("&amp;");
        // No literal markup leaked in.
        result.Should().NotContain("<bad>");
    }

    [Fact]
    public void Convert_BareOperationTemplate_AlsoWorks()
    {
        // SoapWorkspace's editor doesn't wrap in an envelope; it's just the inner body.
        const string bareTemplate = """<msg:GetWeatherReq xmlns:msg="http://acme.test/wx/msg"><msg:city>?</msg:city></msg:GetWeatherReq>""";
        var result = JsonToSoapBodyConverter.Convert(bareTemplate, """{"city":"Tokyo"}""")!;

        var doc = XDocument.Parse(result);
        var msgNs = (XNamespace)"http://acme.test/wx/msg";
        var op = doc.Root!;
        op.Name.Should().Be(msgNs + "GetWeatherReq");
        op.Element(msgNs + "city")!.Value.Should().Be("Tokyo");
    }

    [Fact]
    public void Convert_NonXmlTemplate_ReturnsNull()
    {
        // If the user is in JSON-mode body and pastes JSON, the existing text isn't XML
        // and we should bail so default paste happens.
        var result = JsonToSoapBodyConverter.Convert("{ already json }", """{"x":1}""");
        result.Should().BeNull();
    }

    [Fact]
    public void Convert_EnvelopeWithEmptyBody_ReturnsNull()
    {
        // No operation element in the body — we have no name to use.
        const string emptyBody = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body />
            </soap:Envelope>
            """;
        var result = JsonToSoapBodyConverter.Convert(emptyBody, """{"x":1}""");
        result.Should().BeNull();
    }

    [Fact]
    public void Convert_InvalidJson_ReturnsNull()
    {
        var result = JsonToSoapBodyConverter.Convert(EnvelopeTemplate, "not json at all");
        result.Should().BeNull();
    }

    [Fact]
    public void Convert_MultiNamespaceTemplate_AssignsPerElementNamespaces()
    {
        // Mirrors real-world IEC TC57-style WSDLs where the
        // operation root and payload live in one namespace (ns7) and the standardized
        // Header children live in another (ns8). Without per-path namespace learning, every
        // child would inherit ns7 — which is exactly the bug this test guards against.
        const string multiNs = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <ns7:GetAccountsReqMsg xmlns:ns7="http://acme.test/Message/GetAccounts" xmlns:ns8="http://iec.ch/TC57/2011/schema/message">
                  <ns7:Header>
                    <ns8:Verb>?</ns8:Verb>
                    <ns8:Noun>?</ns8:Noun>
                    <ns8:User>
                      <ns8:UserID>?</ns8:UserID>
                    </ns8:User>
                  </ns7:Header>
                  <ns7:Payload>
                    <ns7:Criteria>
                      <ns7:Customer>
                        <ns7:mRID>?</ns7:mRID>
                      </ns7:Customer>
                    </ns7:Criteria>
                  </ns7:Payload>
                </ns7:GetAccountsReqMsg>
              </soap:Body>
            </soap:Envelope>
            """;

        const string json = """
            {
              "Header": {
                "Verb": "get",
                "Noun": "accounts",
                "User": { "UserID": "WEB" }
              },
              "Payload": {
                "Criteria": {
                  "Customer": { "mRID": "9916371000" }
                }
              }
            }
            """;

        var result = JsonToSoapBodyConverter.Convert(multiNs, json)!;
        var doc = XDocument.Parse(result);
        var ns7 = (XNamespace)"http://acme.test/Message/GetAccounts";
        var ns8 = (XNamespace)"http://iec.ch/TC57/2011/schema/message";

        // Operation root + Header + Payload + Criteria + Customer + mRID all in ns7.
        doc.Descendants(ns7 + "GetAccountsReqMsg").Should().HaveCount(1);
        doc.Descendants(ns7 + "Header").Should().HaveCount(1);
        doc.Descendants(ns7 + "Payload").Should().HaveCount(1);
        doc.Descendants(ns7 + "mRID").Single().Value.Should().Be("9916371000");

        // Verb/Noun/User/UserID in ns8 — this is the multi-namespace property under test.
        doc.Descendants(ns8 + "Verb").Single().Value.Should().Be("get");
        doc.Descendants(ns8 + "Noun").Single().Value.Should().Be("accounts");
        doc.Descendants(ns8 + "UserID").Single().Value.Should().Be("WEB");

        // Negative checks: no Verb/UserID should land in ns7 (the inheritance bug).
        doc.Descendants(ns7 + "Verb").Should().BeEmpty();
        doc.Descendants(ns7 + "UserID").Should().BeEmpty();
    }

    [Fact]
    public void Convert_RepeatedElement_ChildrenStayInTheirNamespace()
    {
        // Property is in ns8 with Name/Value children in ns8. JSON has an array of properties.
        // Each repeated <Property>'s children must resolve to ns8, not ns7 (the operation ns).
        const string template = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <ns7:Req xmlns:ns7="http://acme.test/op" xmlns:ns8="http://acme.test/std">
                  <ns7:Header>
                    <ns8:Property>
                      <ns8:Name>?</ns8:Name>
                      <ns8:Value>?</ns8:Value>
                    </ns8:Property>
                  </ns7:Header>
                </ns7:Req>
              </soap:Body>
            </soap:Envelope>
            """;
        const string json = """
            {
              "Header": {
                "Property": [
                  { "Name": "userAgent", "Value": "Chrome" },
                  { "Name": "lang", "Value": "en" }
                ]
              }
            }
            """;

        var result = JsonToSoapBodyConverter.Convert(template, json)!;
        var doc = XDocument.Parse(result);
        var ns8 = (XNamespace)"http://acme.test/std";

        var props = doc.Descendants(ns8 + "Property").ToList();
        props.Should().HaveCount(2);
        props[0].Element(ns8 + "Name")!.Value.Should().Be("userAgent");
        props[1].Element(ns8 + "Value")!.Value.Should().Be("en");
    }

    [Fact]
    public void Convert_Soap12Envelope_AlsoRecognized()
    {
        const string soap12 = """
            <soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope" xmlns:msg="http://acme.test/wx/msg">
              <soap:Body>
                <msg:GetWeatherReq><msg:city>?</msg:city></msg:GetWeatherReq>
              </soap:Body>
            </soap:Envelope>
            """;
        var result = JsonToSoapBodyConverter.Convert(soap12, """{"city":"Paris"}""")!;
        var doc = XDocument.Parse(result);
        var msgNs = (XNamespace)"http://acme.test/wx/msg";
        doc.Descendants(msgNs + "city").Single().Value.Should().Be("Paris");
    }
}
