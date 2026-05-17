using Vegha.Integrations.Wsdl;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Integrations.Wsdl;

public class WsdlParserTests
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
    public void Parse_ExtractsServiceMetadata()
    {
        var w = WsdlParser.Parse(WeatherWsdl);
        w.ServiceName.Should().Be("WeatherService");
        w.TargetNamespace.Should().Be("http://acme.test/weather");
        w.EndpointUrl.Should().Be("https://api.acme.test/weather");
        w.Operations.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_ExtractsOperationsWithSoapActions()
    {
        var w = WsdlParser.Parse(WeatherWsdl);
        var get = w.Operations.First(o => o.Name == "GetWeather");
        get.SoapAction.Should().Be("http://acme.test/weather/GetWeather");
        get.InputMessage.Should().Be("tns:GetWeatherRequest");
    }

    [Fact]
    public void EnvelopeBuilder_WrapsBody_AndSetsContentType()
    {
        var built = SoapEnvelopeBuilder.Build(
            innerBodyXml: "<ns:GetWeather xmlns:ns=\"http://acme.test/weather\"><city>Seattle</city></ns:GetWeather>",
            soapAction: "http://acme.test/weather/GetWeather",
            version: SoapEnvelopeBuilder.Version.Soap11);

        built.ContentType.Should().StartWith("text/xml");
        built.Body.Should().Contain("<s:Envelope");
        built.Body.Should().Contain("<city>Seattle</city>");
        built.Headers.Should().Contain(h => h.Key == "SOAPAction");
    }

    [Fact]
    public void EnvelopeBuilder_Soap12_PutsActionInContentType_NoSoapActionHeader()
    {
        var built = SoapEnvelopeBuilder.Build(
            innerBodyXml: "<ns:Op xmlns:ns=\"u\"/>",
            soapAction: "u/Op",
            version: SoapEnvelopeBuilder.Version.Soap12);

        built.ContentType.Should().Contain("application/soap+xml");
        built.ContentType.Should().Contain("action=\"u/Op\"");
        built.Headers.Should().BeEmpty();
    }

    [Fact]
    public void ExtractBodyContents_ReturnsInnerOnly()
    {
        const string response = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body><GetWeatherResponse><temp>72</temp></GetWeatherResponse></s:Body>
            </s:Envelope>
            """;
        var inner = SoapEnvelopeBuilder.ExtractBodyContents(response);
        inner.Should().Contain("<temp>72</temp>");
        inner.Should().NotContain("Envelope");
    }
}
