using System.Xml.Linq;
using Vegha.Integrations.Wsdl;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Integrations.Wsdl;

public class JsonToWsdlBodyConverterTests
{
    /// <summary>Same minimal WSDL the sample-envelope tests use: one operation, one
    /// schema, two simple-typed children.</summary>
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
    public void Convert_SimpleObject_ProducesElementsWithSchemaNamespace()
    {
        var conv = new JsonToWsdlBodyConverter(SimpleWsdl);
        var body = conv.Convert("GetWeather", """{"city":"Seattle","units":"metric"}""");

        body.Should().NotBeNull();
        body!.Should().Contain("<msg:GetWeatherReq");
        body.Should().Contain("xmlns:msg=\"http://acme.test/wx/msg\"");
        body.Should().Contain("<msg:city>Seattle</msg:city>");
        body.Should().Contain("<msg:units>metric</msg:units>");
    }

    [Fact]
    public void Convert_RoundTripsAsValidXml()
    {
        var conv = new JsonToWsdlBodyConverter(SimpleWsdl);
        var body = conv.Convert("GetWeather", """{"city":"Seattle"}""")!;

        // The result should parse cleanly — namespaces declared, tags balanced.
        var act = () => XDocument.Parse(body);
        act.Should().NotThrow();
    }

    [Fact]
    public void Convert_WrappedRoot_UnwrapsOperationKey()
    {
        // Common shape from response-style JSON: { "GetWeatherReq": { ... params ... } }.
        // We unwrap so the user's params land in the right place.
        var conv = new JsonToWsdlBodyConverter(SimpleWsdl);
        var body = conv.Convert("GetWeather", """{"GetWeatherReq":{"city":"NYC"}}""")!;

        body.Should().Contain("<msg:city>NYC</msg:city>");
        // No double-nesting.
        body.Should().NotContain("<msg:GetWeatherReq><msg:GetWeatherReq>");
    }

    [Fact]
    public void Convert_PropertyNameCaseInsensitive_StillMatchesSchema()
    {
        var conv = new JsonToWsdlBodyConverter(SimpleWsdl);
        // JSON uses PascalCase; schema uses lowercase. Should still bind.
        var body = conv.Convert("GetWeather", """{"City":"Boston"}""")!;
        body.Should().Contain("<msg:city>Boston</msg:city>");
    }

    [Fact]
    public void Convert_NestedComplexType_NamespacesPropagateToChildren()
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

        var conv = new JsonToWsdlBodyConverter(nested);
        var body = conv.Convert("Op", """{"Inner":{"leaf":42}}""")!;

        body.Should().Contain("<msg:Inner>");
        body.Should().Contain("<msg:leaf>42</msg:leaf>");
    }

    [Fact]
    public void Convert_JsonArray_EmitsRepeatedSiblingElements()
    {
        const string repeating = """
            <wsdl:definitions
                xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                xmlns:tns="http://acme.test/r"
                xmlns:msg="http://acme.test/r/m"
                targetNamespace="http://acme.test/r" name="R">
              <wsdl:types>
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                           targetNamespace="http://acme.test/r/m" elementFormDefault="qualified">
                  <xs:element name="ListReq">
                    <xs:complexType>
                      <xs:sequence>
                        <xs:element name="id" type="xs:string" maxOccurs="unbounded"/>
                      </xs:sequence>
                    </xs:complexType>
                  </xs:element>
                </xs:schema>
              </wsdl:types>
              <wsdl:message name="ListReqMsg"><wsdl:part name="body" element="msg:ListReq"/></wsdl:message>
              <wsdl:portType name="P"><wsdl:operation name="List"><wsdl:input message="tns:ListReqMsg"/></wsdl:operation></wsdl:portType>
            </wsdl:definitions>
            """;

        var conv = new JsonToWsdlBodyConverter(repeating);
        var body = conv.Convert("List", """{"id":["a","b","c"]}""")!;

        // Three id sibling elements with values a/b/c.
        body.Should().Contain("<msg:id>a</msg:id>");
        body.Should().Contain("<msg:id>b</msg:id>");
        body.Should().Contain("<msg:id>c</msg:id>");
    }

    [Fact]
    public void Convert_UnknownProperty_FallsBackToParentNamespace()
    {
        var conv = new JsonToWsdlBodyConverter(SimpleWsdl);
        // "extra" isn't in the schema. We still emit it under msg: so the SOAP server
        // either accepts it (lenient processing) or rejects it with a clear xpath.
        var body = conv.Convert("GetWeather", """{"city":"X","extra":"value"}""")!;

        body.Should().Contain("<msg:city>X</msg:city>");
        body.Should().Contain("<msg:extra>value</msg:extra>");
    }

    [Fact]
    public void Convert_NumbersAndBools_EmittedAsRawText()
    {
        const string typed = """
            <wsdl:definitions
                xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                xmlns:tns="http://acme.test/t"
                xmlns:msg="http://acme.test/t/m"
                targetNamespace="http://acme.test/t" name="T">
              <wsdl:types>
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                           targetNamespace="http://acme.test/t/m" elementFormDefault="qualified">
                  <xs:element name="Req">
                    <xs:complexType>
                      <xs:sequence>
                        <xs:element name="count" type="xs:int"/>
                        <xs:element name="active" type="xs:boolean"/>
                      </xs:sequence>
                    </xs:complexType>
                  </xs:element>
                </xs:schema>
              </wsdl:types>
              <wsdl:message name="ReqMsg"><wsdl:part name="body" element="msg:Req"/></wsdl:message>
              <wsdl:portType name="P"><wsdl:operation name="Do"><wsdl:input message="tns:ReqMsg"/></wsdl:operation></wsdl:portType>
            </wsdl:definitions>
            """;

        var conv = new JsonToWsdlBodyConverter(typed);
        var body = conv.Convert("Do", """{"count":17,"active":true}""")!;

        body.Should().Contain("<msg:count>17</msg:count>");
        body.Should().Contain("<msg:active>true</msg:active>");
    }

    [Fact]
    public void Convert_StringsContainingMarkup_AreXmlEscaped()
    {
        var conv = new JsonToWsdlBodyConverter(SimpleWsdl);
        var body = conv.Convert("GetWeather", """{"city":"<bad>&\"weird\""}""")!;

        body.Should().Contain("&lt;bad&gt;");
        body.Should().Contain("&amp;");
        body.Should().NotContain("<bad>");
    }

    [Fact]
    public void Convert_InvalidJson_ReturnsNull()
    {
        var conv = new JsonToWsdlBodyConverter(SimpleWsdl);
        conv.Convert("GetWeather", "not json").Should().BeNull();
    }

    [Fact]
    public void Convert_UnknownOperation_ReturnsNull()
    {
        var conv = new JsonToWsdlBodyConverter(SimpleWsdl);
        conv.Convert("DoesNotExist", """{"city":"X"}""").Should().BeNull();
    }
}
