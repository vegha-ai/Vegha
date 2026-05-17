using Vegha.Integrations.Wsdl;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Integrations.Wsdl;

public class WsdlSampleEnvelopeGeneratorTests
{
    /// <summary>WSDL with one schema, one inline complexType, two simple-typed children.
    /// This is the minimum shape that exercises element-resolution + sequence walking.</summary>
    private const string SimpleWsdl = """
        <wsdl:definitions
            xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
            xmlns:tns="http://acme.test/wx"
            xmlns:msg="http://acme.test/wx/msg"
            xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
            targetNamespace="http://acme.test/wx" name="WxService">
          <wsdl:types>
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                       targetNamespace="http://acme.test/wx/msg"
                       elementFormDefault="qualified">
              <xs:element name="GetWeatherReq">
                <xs:complexType>
                  <xs:sequence>
                    <xs:element name="city" type="xs:string"/>
                    <xs:element name="units" type="xs:string" minOccurs="0"/>
                  </xs:sequence>
                </xs:complexType>
              </xs:element>
            </xs:schema>
          </wsdl:types>
          <wsdl:message name="GetWeatherRequest">
            <wsdl:part name="body" element="msg:GetWeatherReq"/>
          </wsdl:message>
          <wsdl:portType name="WxPort">
            <wsdl:operation name="GetWeather">
              <wsdl:input message="tns:GetWeatherRequest"/>
            </wsdl:operation>
          </wsdl:portType>
        </wsdl:definitions>
        """;

    [Fact]
    public void Generate_SimpleType_LeafEmitsQuestionMark()
    {
        var gen = new WsdlSampleEnvelopeGenerator(SimpleWsdl);
        var body = gen.GenerateForOperation("GetWeather");
        body.Should().NotBeNull();
        body!.Should().Contain(":city>?</");
    }

    [Fact]
    public void Generate_OptionalElement_PrependsOptionalComment()
    {
        var gen = new WsdlSampleEnvelopeGenerator(SimpleWsdl);
        var body = gen.GenerateForOperation("GetWeather")!;
        // The optional comment must appear before the <units> element, not before <city>.
        var unitsIdx = body.IndexOf(":units>");
        var optionalIdx = body.IndexOf("<!--Optional:-->");
        optionalIdx.Should().BeGreaterThan(0);
        optionalIdx.Should().BeLessThan(unitsIdx);
    }

    [Fact]
    public void Generate_RootElement_DeclaresNamespacePrefix()
    {
        var gen = new WsdlSampleEnvelopeGenerator(SimpleWsdl);
        var body = gen.GenerateForOperation("GetWeather")!;
        // Some prefix declared; the WSDL seeds "msg" → http://acme.test/wx/msg, so prefer it.
        body.Should().Contain("xmlns:msg=\"http://acme.test/wx/msg\"");
        body.Should().Contain("<msg:GetWeatherReq");
    }

    [Fact]
    public void Generate_NestedComplexType_ProducesIndentedTree()
    {
        const string nested = """
            <wsdl:definitions
                xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                xmlns:tns="http://acme.test/n"
                xmlns:msg="http://acme.test/n/m"
                targetNamespace="http://acme.test/n" name="N">
              <wsdl:types>
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                           targetNamespace="http://acme.test/n/m" elementFormDefault="qualified">
                  <xs:element name="Outer">
                    <xs:complexType>
                      <xs:sequence>
                        <xs:element name="Inner">
                          <xs:complexType>
                            <xs:sequence>
                              <xs:element name="leaf" type="xs:int"/>
                            </xs:sequence>
                          </xs:complexType>
                        </xs:element>
                      </xs:sequence>
                    </xs:complexType>
                  </xs:element>
                </xs:schema>
              </wsdl:types>
              <wsdl:message name="OuterReq"><wsdl:part name="body" element="msg:Outer"/></wsdl:message>
              <wsdl:portType name="P"><wsdl:operation name="Op"><wsdl:input message="tns:OuterReq"/></wsdl:operation></wsdl:portType>
            </wsdl:definitions>
            """;
        var gen = new WsdlSampleEnvelopeGenerator(nested);
        var body = gen.GenerateForOperation("Op")!;
        body.Should().Contain(":Outer");
        body.Should().Contain(":Inner");
        body.Should().Contain(":leaf>?</");
    }

    [Fact]
    public void Generate_TypeReferenceAcrossSchemas_ResolvesAndExpands()
    {
        // Element in schema A references a complex type defined in schema B.
        const string crossSchema = """
            <wsdl:definitions
                xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                xmlns:tns="http://acme.test/x"
                xmlns:a="http://acme.test/x/a"
                xmlns:b="http://acme.test/x/b"
                targetNamespace="http://acme.test/x" name="X">
              <wsdl:types>
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                           xmlns:b="http://acme.test/x/b"
                           targetNamespace="http://acme.test/x/a" elementFormDefault="qualified">
                  <xs:element name="DoIt">
                    <xs:complexType>
                      <xs:sequence>
                        <xs:element name="payload" type="b:Payload"/>
                      </xs:sequence>
                    </xs:complexType>
                  </xs:element>
                </xs:schema>
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                           targetNamespace="http://acme.test/x/b" elementFormDefault="qualified">
                  <xs:complexType name="Payload">
                    <xs:sequence>
                      <xs:element name="id" type="xs:string"/>
                      <xs:element name="amount" type="xs:decimal"/>
                    </xs:sequence>
                  </xs:complexType>
                </xs:schema>
              </wsdl:types>
              <wsdl:message name="DoItReq"><wsdl:part name="body" element="a:DoIt"/></wsdl:message>
              <wsdl:portType name="P"><wsdl:operation name="DoIt"><wsdl:input message="tns:DoItReq"/></wsdl:operation></wsdl:portType>
            </wsdl:definitions>
            """;

        var gen = new WsdlSampleEnvelopeGenerator(crossSchema);
        var body = gen.GenerateForOperation("DoIt")!;
        body.Should().Contain(":id>?</");
        body.Should().Contain(":amount>?</");
    }

    [Fact]
    public void Generate_ElementRef_FollowsToReferencedElement()
    {
        const string elemRef = """
            <wsdl:definitions
                xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                xmlns:tns="http://acme.test/r"
                xmlns:a="http://acme.test/r/a"
                xmlns:b="http://acme.test/r/b"
                targetNamespace="http://acme.test/r" name="R">
              <wsdl:types>
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                           xmlns:b="http://acme.test/r/b"
                           targetNamespace="http://acme.test/r/a" elementFormDefault="qualified">
                  <xs:element name="Outer">
                    <xs:complexType>
                      <xs:sequence>
                        <xs:element ref="b:Inner"/>
                      </xs:sequence>
                    </xs:complexType>
                  </xs:element>
                </xs:schema>
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                           targetNamespace="http://acme.test/r/b" elementFormDefault="qualified">
                  <xs:element name="Inner" type="xs:string"/>
                </xs:schema>
              </wsdl:types>
              <wsdl:message name="OuterReq"><wsdl:part name="body" element="a:Outer"/></wsdl:message>
              <wsdl:portType name="P"><wsdl:operation name="Op"><wsdl:input message="tns:OuterReq"/></wsdl:operation></wsdl:portType>
            </wsdl:definitions>
            """;

        var gen = new WsdlSampleEnvelopeGenerator(elemRef);
        var body = gen.GenerateForOperation("Op")!;
        body.Should().Contain(":Inner>?</");
    }

    [Fact]
    public void Generate_RecursiveType_StopsAtMaxDepth()
    {
        // Tree → child Tree[] (cycle). Without the recursion guard this would never return.
        const string recursive = """
            <wsdl:definitions
                xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                xmlns:tns="http://acme.test/t"
                xmlns:m="http://acme.test/t/m"
                targetNamespace="http://acme.test/t" name="T">
              <wsdl:types>
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                           xmlns:m="http://acme.test/t/m"
                           targetNamespace="http://acme.test/t/m" elementFormDefault="qualified">
                  <xs:element name="Root" type="m:Tree"/>
                  <xs:complexType name="Tree">
                    <xs:sequence>
                      <xs:element name="value" type="xs:string"/>
                      <xs:element name="child" type="m:Tree" minOccurs="0"/>
                    </xs:sequence>
                  </xs:complexType>
                </xs:schema>
              </wsdl:types>
              <wsdl:message name="Req"><wsdl:part name="body" element="m:Root"/></wsdl:message>
              <wsdl:portType name="P"><wsdl:operation name="Op"><wsdl:input message="tns:Req"/></wsdl:operation></wsdl:portType>
            </wsdl:definitions>
            """;

        var gen = new WsdlSampleEnvelopeGenerator(recursive);
        var body = gen.GenerateForOperation("Op")!;
        // Should terminate. Exact depth doesn't matter; we just want a finite string.
        body.Length.Should().BeGreaterThan(0);
        body.Length.Should().BeLessThan(50_000);
    }

    [Fact]
    public void Generate_UnknownOperation_ReturnsNull()
    {
        var gen = new WsdlSampleEnvelopeGenerator(SimpleWsdl);
        gen.GenerateForOperation("DoesNotExist").Should().BeNull();
    }
}
