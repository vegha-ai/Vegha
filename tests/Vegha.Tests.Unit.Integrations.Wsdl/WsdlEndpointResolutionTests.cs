using Vegha.Integrations.Wsdl;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Integrations.Wsdl;

/// <summary>Endpoint URL resolution across the WSDL flavors we promise to support.
/// Each test fixes the binding+address shape and asserts the operation lands with the
/// correct URL — no <c>{{endpoint}}</c> placeholders, no first-port-wins shortcuts.</summary>
public class WsdlEndpointResolutionTests
{
    [Fact]
    public void Wsdl11_Soap11Address_PerOperationUrl()
    {
        const string wsdl = """
            <wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                              xmlns:tns="urn:t"
                              xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                              targetNamespace="urn:t" name="S">
              <wsdl:portType name="P">
                <wsdl:operation name="Op"><wsdl:input message="tns:R"/></wsdl:operation>
              </wsdl:portType>
              <wsdl:binding name="B" type="tns:P">
                <soap:binding transport="http://schemas.xmlsoap.org/soap/http"/>
                <wsdl:operation name="Op"><soap:operation soapAction="urn:t:Op"/></wsdl:operation>
              </wsdl:binding>
              <wsdl:service name="S">
                <wsdl:port name="Port" binding="tns:B">
                  <soap:address location="http://example.org/svc11"/>
                </wsdl:port>
              </wsdl:service>
            </wsdl:definitions>
            """;
        var d = WsdlParser.Parse(wsdl);
        d.Operations.Should().ContainSingle();
        d.Operations[0].EndpointUrl.Should().Be("http://example.org/svc11");
    }

    [Fact]
    public void Wsdl11_Soap12Address_Resolves()
    {
        const string wsdl = """
            <wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                              xmlns:tns="urn:t"
                              xmlns:soap12="http://schemas.xmlsoap.org/wsdl/soap12/"
                              targetNamespace="urn:t" name="S">
              <wsdl:portType name="P">
                <wsdl:operation name="Op"><wsdl:input message="tns:R"/></wsdl:operation>
              </wsdl:portType>
              <wsdl:binding name="B" type="tns:P">
                <soap12:binding transport="http://schemas.xmlsoap.org/soap/http"/>
                <wsdl:operation name="Op"><soap12:operation soapAction="urn:t:Op"/></wsdl:operation>
              </wsdl:binding>
              <wsdl:service name="S">
                <wsdl:port name="Port" binding="tns:B">
                  <soap12:address location="http://example.org/svc12"/>
                </wsdl:port>
              </wsdl:service>
            </wsdl:definitions>
            """;
        var d = WsdlParser.Parse(wsdl);
        d.Operations[0].EndpointUrl.Should().Be("http://example.org/svc12");
    }

    [Fact]
    public void Wsdl11_HttpAddress_Resolves()
    {
        // Plain HTTP binding — uncommon but legal under WSDL 1.1 §4.
        const string wsdl = """
            <wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                              xmlns:tns="urn:t"
                              xmlns:http="http://schemas.xmlsoap.org/wsdl/http/"
                              targetNamespace="urn:t" name="S">
              <wsdl:portType name="P">
                <wsdl:operation name="Op"><wsdl:input message="tns:R"/></wsdl:operation>
              </wsdl:portType>
              <wsdl:binding name="B" type="tns:P">
                <http:binding verb="POST"/>
                <wsdl:operation name="Op"><http:operation location="/op"/></wsdl:operation>
              </wsdl:binding>
              <wsdl:service name="S">
                <wsdl:port name="Port" binding="tns:B">
                  <http:address location="http://example.org/http"/>
                </wsdl:port>
              </wsdl:service>
            </wsdl:definitions>
            """;
        var d = WsdlParser.Parse(wsdl);
        d.Operations[0].EndpointUrl.Should().Be("http://example.org/http");
    }

    [Fact]
    public void Wsdl11_TwoPortsTwoBindings_RoutesEachOperationToOwnUrl()
    {
        // Two portTypes (PA, PB), each backed by its own binding+port with distinct URL.
        // Without per-binding routing, both ops would collapse to the same URL.
        const string wsdl = """
            <wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                              xmlns:tns="urn:t"
                              xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                              targetNamespace="urn:t" name="S">
              <wsdl:portType name="PA"><wsdl:operation name="OpA"><wsdl:input message="tns:RA"/></wsdl:operation></wsdl:portType>
              <wsdl:portType name="PB"><wsdl:operation name="OpB"><wsdl:input message="tns:RB"/></wsdl:operation></wsdl:portType>
              <wsdl:binding name="BA" type="tns:PA">
                <soap:binding transport="http://schemas.xmlsoap.org/soap/http"/>
                <wsdl:operation name="OpA"><soap:operation soapAction="A"/></wsdl:operation>
              </wsdl:binding>
              <wsdl:binding name="BB" type="tns:PB">
                <soap:binding transport="http://schemas.xmlsoap.org/soap/http"/>
                <wsdl:operation name="OpB"><soap:operation soapAction="B"/></wsdl:operation>
              </wsdl:binding>
              <wsdl:service name="S">
                <wsdl:port name="PortA" binding="tns:BA"><soap:address location="http://a.example"/></wsdl:port>
                <wsdl:port name="PortB" binding="tns:BB"><soap:address location="http://b.example"/></wsdl:port>
              </wsdl:service>
            </wsdl:definitions>
            """;
        var d = WsdlParser.Parse(wsdl);
        d.Operations.First(o => o.Name == "OpA").EndpointUrl.Should().Be("http://a.example");
        d.Operations.First(o => o.Name == "OpB").EndpointUrl.Should().Be("http://b.example");
    }

    [Fact]
    public void Wsdl20_EndpointAddress_Resolves()
    {
        // WSDL 2.0 — different root, different vocabulary (interface/operation, endpoint@address).
        const string wsdl = """
            <description xmlns="http://www.w3.org/ns/wsdl"
                         xmlns:tns="urn:t" targetNamespace="urn:t">
              <interface name="I">
                <operation name="Op"/>
              </interface>
              <binding name="B" interface="tns:I" type="http://www.w3.org/ns/wsdl/http"/>
              <service name="S" interface="tns:I">
                <endpoint name="E" binding="tns:B" address="http://example.org/v2"/>
              </service>
            </description>
            """;
        var d = WsdlParser.Parse(wsdl);
        d.Operations.Should().ContainSingle();
        d.Operations[0].Name.Should().Be("Op");
        d.Operations[0].EndpointUrl.Should().Be("http://example.org/v2");
    }
}
