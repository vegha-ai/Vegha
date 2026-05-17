using Vegha.Core.Domain;
using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

public class WsdlImporterTests
{
    private const string WeatherWsdl = """
        <definitions name="WeatherService"
                     targetNamespace="http://acme.test/weather"
                     xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:tns="http://acme.test/weather"
                     xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/">
          <portType name="WeatherPortType">
            <operation name="GetWeather">
              <input message="tns:GetWeatherRequest"/>
              <output message="tns:GetWeatherResponse"/>
            </operation>
            <operation name="GetForecast">
              <input message="tns:GetForecastRequest"/>
              <output message="tns:GetForecastResponse"/>
            </operation>
          </portType>
          <binding name="WeatherBinding" type="tns:WeatherPortType">
            <soap:binding transport="http://schemas.xmlsoap.org/soap/http"/>
            <operation name="GetWeather">
              <soap:operation soapAction="http://acme.test/weather/GetWeather"/>
            </operation>
            <operation name="GetForecast">
              <soap:operation soapAction="http://acme.test/weather/GetForecast"/>
            </operation>
          </binding>
          <service name="WeatherService">
            <port name="WeatherPort" binding="tns:WeatherBinding">
              <soap:address location="https://api.acme.test/weather"/>
            </port>
          </service>
        </definitions>
        """;

    [Fact]
    public void Import_NamesCollectionAfterService()
    {
        var c = WsdlImporter.ImportFromString(WeatherWsdl);
        c.Name.Should().Be("WeatherService");
    }

    [Fact]
    public void Import_DoesNotEmitEndpointVariable()
    {
        // The literal URL goes on each request now — no indirection through a collection
        // variable. Users can still opt in to env-swap by editing the URL.
        var c = WsdlImporter.ImportFromString(WeatherWsdl);
        c.Variables.Should().BeEmpty();
    }

    [Fact]
    public void Import_OneRequestPerOperation()
    {
        var c = WsdlImporter.ImportFromString(WeatherWsdl);
        c.Requests.Should().HaveCount(2);
        c.Requests.Select(r => r.Name).Should().BeEquivalentTo(new[] { "GetWeather", "GetForecast" });
    }

    [Fact]
    public void Import_RequestIsSoapPostUsingLiteralUrlFromWsdl()
    {
        var c = WsdlImporter.ImportFromString(WeatherWsdl);
        var r = c.Requests.First(x => x.Name == "GetWeather");
        r.Kind.Should().Be(RequestKind.Soap);
        r.Method.Should().Be("POST");
        r.Url.Should().Be("https://api.acme.test/weather");
    }

    [Fact]
    public void Import_BodyIsFullSoapEnvelopeWithInputElement()
    {
        var c = WsdlImporter.ImportFromString(WeatherWsdl);
        var r = c.Requests.First(x => x.Name == "GetWeather");
        r.Body.Mode.Should().Be(BodyMode.Xml);
        r.Body.Content.Should().NotBeNullOrEmpty();
        r.Body.Content!.Should().Contain("Envelope");
        r.Body.Content!.Should().Contain("Body");
        r.Body.Content!.Should().Contain("GetWeatherRequest");
        r.Body.Content!.Should().Contain("xmlns=\"http://acme.test/weather\"");
    }

    [Fact]
    public void Import_HeadersIncludeSoapActionAndContentType()
    {
        var c = WsdlImporter.ImportFromString(WeatherWsdl);
        var r = c.Requests.First(x => x.Name == "GetWeather");

        var soapAction = r.Headers.FirstOrDefault(h => h.Name == "SOAPAction");
        soapAction.Should().NotBeNull();
        soapAction!.Value.Should().Contain("http://acme.test/weather/GetWeather");

        var contentType = r.Headers.FirstOrDefault(h => h.Name == "Content-Type");
        contentType.Should().NotBeNull();
        contentType!.Value.Should().StartWith("text/xml");
    }

    [Fact]
    public void Import_NoEndpoint_LeavesUrlEmpty()
    {
        // WSDL with no <service><port>: the importer should still produce requests, just
        // with an empty URL — user fills it in manually.
        const string wsdlNoService = """
            <definitions name="X" targetNamespace="urn:x"
                         xmlns="http://schemas.xmlsoap.org/wsdl/"
                         xmlns:tns="urn:x"
                         xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/">
              <portType name="P">
                <operation name="Op"><input message="tns:OpReq"/><output message="tns:OpResp"/></operation>
              </portType>
              <binding name="B" type="tns:P">
                <soap:binding transport="http://schemas.xmlsoap.org/soap/http"/>
                <operation name="Op"><soap:operation soapAction="urn:x:Op"/></operation>
              </binding>
            </definitions>
            """;

        var c = WsdlImporter.ImportFromString(wsdlNoService);
        c.Variables.Should().BeEmpty();
        c.Requests.Should().ContainSingle();
        c.Requests[0].Url.Should().BeEmpty();
    }

    [Fact]
    public void Import_MalformedXml_ThrowsInvalidData()
    {
        Action act = () => WsdlImporter.ImportFromString("not really xml <");
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Import_MultiPort_RoutesEachOperationToOwnUrl()
    {
        const string wsdl = """
            <wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                              xmlns:tns="urn:t"
                              xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                              targetNamespace="urn:t" name="MultiSvc">
              <wsdl:portType name="PA"><wsdl:operation name="OpA"><wsdl:input message="tns:RA"/></wsdl:operation></wsdl:portType>
              <wsdl:portType name="PB"><wsdl:operation name="OpB"><wsdl:input message="tns:RB"/></wsdl:operation></wsdl:portType>
              <wsdl:binding name="BA" type="tns:PA"><soap:binding transport="http://schemas.xmlsoap.org/soap/http"/><wsdl:operation name="OpA"><soap:operation soapAction="A"/></wsdl:operation></wsdl:binding>
              <wsdl:binding name="BB" type="tns:PB"><soap:binding transport="http://schemas.xmlsoap.org/soap/http"/><wsdl:operation name="OpB"><soap:operation soapAction="B"/></wsdl:operation></wsdl:binding>
              <wsdl:service name="MultiSvc">
                <wsdl:port name="PortA" binding="tns:BA"><soap:address location="http://a.example/op"/></wsdl:port>
                <wsdl:port name="PortB" binding="tns:BB"><soap:address location="http://b.example/op"/></wsdl:port>
              </wsdl:service>
            </wsdl:definitions>
            """;

        var c = WsdlImporter.ImportFromString(wsdl);
        c.Requests.First(r => r.Name == "OpA").Url.Should().Be("http://a.example/op");
        c.Requests.First(r => r.Name == "OpB").Url.Should().Be("http://b.example/op");
    }
}
