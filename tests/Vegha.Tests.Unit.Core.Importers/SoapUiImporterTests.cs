using Vegha.Core.Domain;
using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

public class SoapUiImporterTests
{
    /// <summary>A project exercising both protocols: a SOAP interface, a REST interface, and a
    /// test suite with one SOAP request step (+ a skipped groovy step) and one REST request step.</summary>
    private const string MixedProject = """
        <con:soapui-project xmlns:con="http://eviware.com/soapui/config"
                            xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                            name="AcmeProject">
          <con:settings/>
          <con:interface xsi:type="con:WsdlInterface" name="WeatherSoap">
            <con:endpoints>
              <con:endpoint>http://acme.test/weather</con:endpoint>
            </con:endpoints>
            <con:operation name="GetWeather" action="http://acme.test/weather/GetWeather"/>
          </con:interface>
          <con:interface xsi:type="con:RestService" name="AcmeRest">
            <con:endpoints>
              <con:endpoint>http://acme.test/api</con:endpoint>
            </con:endpoints>
            <con:resource name="Users" path="/users">
              <con:method name="ListUsers" method="GET"/>
            </con:resource>
          </con:interface>
          <con:testSuite name="SmokeSuite">
            <con:testCase name="WeatherCase">
              <con:testStep type="request" name="GetWeather Step">
                <con:config>
                  <con:interface>WeatherSoap</con:interface>
                  <con:operation>GetWeather</con:operation>
                  <con:request name="GetWeather Step">
                    <con:endpoint>http://acme.test/weather</con:endpoint>
                    <con:request><![CDATA[<soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"><soapenv:Body><GetWeather><City>${#Project#defaultCity}</City></GetWeather></soapenv:Body></soapenv:Envelope>]]></con:request>
                    <con:assertion type="SOAP Response" name="SOAP Response"/>
                    <con:assertion type="Valid HTTP Status Codes" name="Status OK">
                      <con:configuration>
                        <codes>200, 201</codes>
                      </con:configuration>
                    </con:assertion>
                    <con:credentials>
                      <con:username>acme</con:username>
                      <con:password>secret</con:password>
                    </con:credentials>
                  </con:request>
                </con:config>
              </con:testStep>
              <con:testStep type="groovy" name="Setup Script"/>
            </con:testCase>
            <con:testCase name="UsersCase">
              <con:testStep type="restrequest" name="ListUsers Step">
                <con:config service="AcmeRest" methodName="ListUsers" resourcePath="/users">
                  <con:restRequest name="ListUsers Step" mediaType="application/json">
                    <con:endpoint>http://acme.test/api</con:endpoint>
                    <con:request/>
                    <con:method>GET</con:method>
                    <con:parameters>
                      <con:entry key="limit" value="25"/>
                    </con:parameters>
                  </con:restRequest>
                </con:config>
              </con:testStep>
            </con:testCase>
          </con:testSuite>
          <con:properties>
            <con:property>
              <con:name>defaultCity</con:name>
              <con:value>Seattle</con:value>
            </con:property>
          </con:properties>
        </con:soapui-project>
        """;

    /// <summary>A catalog-only project: SOAP interface, no test suites. One operation has a
    /// sample call, the other does not.</summary>
    private const string CatalogOnlyProject = """
        <con:soapui-project xmlns:con="http://eviware.com/soapui/config"
                            xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                            name="CatalogProj">
          <con:interface xsi:type="con:WsdlInterface" name="WeatherSoap">
            <con:endpoints>
              <con:endpoint>http://acme.test/weather</con:endpoint>
            </con:endpoints>
            <con:operation name="GetWeather" action="urn:GetWeather">
              <con:call name="Request 1">
                <con:endpoint>http://acme.test/weather</con:endpoint>
                <con:request><![CDATA[<soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"><soapenv:Body><GetWeather/></soapenv:Body></soapenv:Envelope>]]></con:request>
              </con:call>
            </con:operation>
            <con:operation name="GetForecast" action="urn:GetForecast"/>
          </con:interface>
        </con:soapui-project>
        """;

    [Fact]
    public void Import_NamesCollectionAfterProject()
    {
        var c = SoapUiImporter.ImportFromString(MixedProject);
        c.Name.Should().Be("AcmeProject");
    }

    [Fact]
    public void Import_ProjectPropertiesBecomeVariables()
    {
        var c = SoapUiImporter.ImportFromString(MixedProject);
        c.Variables.Should().ContainSingle();
        c.Variables[0].Name.Should().Be("defaultCity");
        c.Variables[0].Value.Should().Be("Seattle");
    }

    [Fact]
    public void Import_TestSuitesBecomeTopLevelFolders()
    {
        var c = SoapUiImporter.ImportFromString(MixedProject);
        c.Folders.Should().ContainSingle();
        c.Folders[0].Name.Should().Be("SmokeSuite");
        // Test suites win — the interface catalog is NOT also emitted as folders.
        c.Folders.Should().HaveCount(1);
    }

    [Fact]
    public void Import_TestCasesBecomeNestedFolders()
    {
        var c = SoapUiImporter.ImportFromString(MixedProject);
        var suite = c.Folders[0];
        suite.Folders.Select(f => f.Name).Should().BeEquivalentTo(new[] { "WeatherCase", "UsersCase" });
    }

    [Fact]
    public void Import_RequestStep_IsSoapPostWithXmlBody()
    {
        var c = SoapUiImporter.ImportFromString(MixedProject);
        var weatherCase = c.Folders[0].Folders.First(f => f.Name == "WeatherCase");
        weatherCase.Requests.Should().ContainSingle();

        var r = weatherCase.Requests[0];
        r.Name.Should().Be("GetWeather Step");
        r.Kind.Should().Be(RequestKind.Soap);
        r.MetaType.Should().Be("soap");
        r.Method.Should().Be("POST");
        r.Url.Should().Be("http://acme.test/weather");
        r.Body.Mode.Should().Be(BodyMode.Xml);

        r.Headers.Should().Contain(h => h.Name == "Content-Type" && h.Value.StartsWith("text/xml"));
        r.Headers.Should().Contain(h => h.Name == "SOAPAction"
            && h.Value.Contains("http://acme.test/weather/GetWeather"));
    }

    [Fact]
    public void Import_RequestStep_PreservesEnvelopeVerbatim_AndConvertsVars()
    {
        var c = SoapUiImporter.ImportFromString(MixedProject);
        var r = c.Folders[0].Folders.First(f => f.Name == "WeatherCase").Requests[0];

        // Stored verbatim — not re-wrapped through SoapEnvelopeBuilder (which would prepend
        // an <?xml?> declaration and a fresh <s:Envelope>).
        r.Body.Content.Should().StartWith("<soapenv:Envelope");
        r.Body.Content.Should().Contain("<GetWeather>");
        // ${#Project#defaultCity} → {{defaultCity}}
        r.Body.Content.Should().Contain("{{defaultCity}}");
        r.Body.Content.Should().NotContain("${");
    }

    [Fact]
    public void Import_RequestStep_CredentialsBecomeBasicAuth()
    {
        var c = SoapUiImporter.ImportFromString(MixedProject);
        var r = c.Folders[0].Folders.First(f => f.Name == "WeatherCase").Requests[0];

        r.Auth.Should().NotBeNull();
        r.Auth!.Type.Should().Be(AuthType.Basic);
        r.Auth.Parameters["username"].Should().Be("acme");
        r.Auth.Parameters["password"].Should().Be("secret");
    }

    [Fact]
    public void Import_NonRequestStep_IsSkippedAndNotedInDocs()
    {
        var c = SoapUiImporter.ImportFromString(MixedProject);
        var weatherCase = c.Folders[0].Folders.First(f => f.Name == "WeatherCase");

        // Only the request step is imported; the groovy step is skipped.
        weatherCase.Requests.Should().ContainSingle();
        weatherCase.Docs.Should().NotBeNull();
        weatherCase.Docs!.Should().Contain("groovy");
    }

    [Fact]
    public void Import_RestRequestStep_IsHttpWithMethodAndQueryParam()
    {
        var c = SoapUiImporter.ImportFromString(MixedProject);
        var usersCase = c.Folders[0].Folders.First(f => f.Name == "UsersCase");
        usersCase.Requests.Should().ContainSingle();

        var r = usersCase.Requests[0];
        r.Kind.Should().Be(RequestKind.Http);
        r.Method.Should().Be("GET");
        r.Url.Should().Be("http://acme.test/api/users");
        r.Params.Should().ContainSingle();
        r.Params[0].Name.Should().Be("limit");
        r.Params[0].Value.Should().Be("25");
    }

    [Fact]
    public void Import_Assertions_ProduceTestsScriptAndDocs()
    {
        var c = SoapUiImporter.ImportFromString(MixedProject);
        var r = c.Folders[0].Folders.First(f => f.Name == "WeatherCase").Requests[0];

        r.Tests.Should().NotBeNullOrEmpty();
        r.Tests!.Should().Contain("expect([200, 201]).toContain(res.status)");
        r.Tests.Should().Contain("expect(res.status).toBe(200)");

        r.Docs.Should().NotBeNull();
        r.Docs!.Should().Contain("Imported from SoapUI");
        r.Docs.Should().Contain("Valid HTTP Status Codes: 200, 201");
    }

    [Fact]
    public void Import_UnknownAssertion_PreservedInDocsOnly()
    {
        const string xml = """
            <con:soapui-project xmlns:con="http://eviware.com/soapui/config" name="P">
              <con:testSuite name="S">
                <con:testCase name="C">
                  <con:testStep type="restrequest" name="Step">
                    <con:config>
                      <con:restRequest name="Step">
                        <con:endpoint>http://x.test</con:endpoint>
                        <con:method>GET</con:method>
                        <con:assertion type="Schema Compliance" name="Schema check"/>
                      </con:restRequest>
                    </con:config>
                  </con:testStep>
                </con:testCase>
              </con:testSuite>
            </con:soapui-project>
            """;

        var c = SoapUiImporter.ImportFromString(xml);
        var r = c.Folders[0].Folders[0].Requests[0];
        r.Tests.Should().BeNull();
        r.Docs.Should().Contain("Schema Compliance");
        r.Docs.Should().Contain("not auto-translated");
    }

    [Fact]
    public void Import_CatalogOnly_EmitsOperationSubfoldersWithRequests()
    {
        var c = SoapUiImporter.ImportFromString(CatalogOnlyProject);

        c.Folders.Should().ContainSingle();
        c.Folders[0].Name.Should().Be("WeatherSoap");
        c.Folders[0].Folders.Select(f => f.Name)
            .Should().BeEquivalentTo(new[] { "GetWeather", "GetForecast" });

        // The operation with a sample call keeps its envelope verbatim; the request takes
        // the call's name.
        var getWeather = c.Folders[0].Folders.First(f => f.Name == "GetWeather");
        getWeather.Requests.Should().ContainSingle();
        getWeather.Requests[0].Name.Should().Be("Request 1");
        getWeather.Requests[0].Kind.Should().Be(RequestKind.Soap);
        getWeather.Requests[0].Method.Should().Be("POST");
        getWeather.Requests[0].Body.Content.Should().StartWith("<soapenv:Envelope");

        // The operation with no sample call gets a synthesized stub envelope.
        var getForecast = c.Folders[0].Folders.First(f => f.Name == "GetForecast");
        getForecast.Requests.Should().ContainSingle();
        getForecast.Requests[0].Body.Mode.Should().Be(BodyMode.Xml);
        getForecast.Requests[0].Body.Content.Should().Contain("Envelope");
        getForecast.Requests[0].Body.Content.Should().Contain("TODO");
    }

    [Fact]
    public void Import_CatalogOperationWithMultipleCalls_EmitsOneRequestPerCall()
    {
        const string xml = """
            <con:soapui-project xmlns:con="http://eviware.com/soapui/config"
                                xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                                name="MultiCallProj">
              <con:interface xsi:type="con:WsdlInterface" name="AccountsSoap">
                <con:endpoints>
                  <con:endpoint>http://acme.test/accounts</con:endpoint>
                </con:endpoints>
                <con:operation name="GetAccounts" action="urn:GetAccounts">
                  <con:call name="AccountDetails">
                    <con:request><![CDATA[<soapenv:Envelope><soapenv:Body><Details/></soapenv:Body></soapenv:Envelope>]]></con:request>
                  </con:call>
                  <con:call name="AccountList">
                    <con:request><![CDATA[<soapenv:Envelope><soapenv:Body><List/></soapenv:Body></soapenv:Envelope>]]></con:request>
                  </con:call>
                  <con:call name="Request 14">
                    <con:request><![CDATA[<soapenv:Envelope><soapenv:Body><R14/></soapenv:Body></soapenv:Envelope>]]></con:request>
                  </con:call>
                </con:operation>
              </con:interface>
            </con:soapui-project>
            """;

        var c = SoapUiImporter.ImportFromString(xml);
        var opFolder = c.Folders[0].Folders.First(f => f.Name == "GetAccounts");

        opFolder.Requests.Select(r => r.Name)
            .Should().BeEquivalentTo(new[] { "AccountDetails", "AccountList", "Request 14" });
        opFolder.Requests.Should().OnlyContain(r => r.Kind == RequestKind.Soap && r.Method == "POST");
        opFolder.Requests.Select(r => r.Sequence).Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    [Fact]
    public void Import_RestOnlyCatalog_WalksResourcesAndMethods()
    {
        const string xml = """
            <con:soapui-project xmlns:con="http://eviware.com/soapui/config"
                                xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                                name="RestProj">
              <con:interface xsi:type="con:RestService" name="AcmeRest">
                <con:endpoints>
                  <con:endpoint>http://acme.test/api</con:endpoint>
                </con:endpoints>
                <con:resource name="Users" path="/users">
                  <con:method name="ListUsers" method="GET">
                    <con:request name="List" mediaType="application/json"/>
                  </con:method>
                  <con:resource name="User" path="/{id}">
                    <con:method name="GetUser" method="GET"/>
                  </con:resource>
                </con:resource>
              </con:interface>
            </con:soapui-project>
            """;

        var c = SoapUiImporter.ImportFromString(xml);
        c.Folders.Should().ContainSingle();

        var requests = c.Folders[0].Requests;
        requests.Should().HaveCount(2);
        requests.Should().OnlyContain(r => r.Kind == RequestKind.Http);
        requests.Select(r => r.Url).Should().Contain("http://acme.test/api/users");
        requests.Select(r => r.Url).Should().Contain("http://acme.test/api/users/{id}");
    }

    [Fact]
    public void Import_EmptySuiteAndEmptyCase_StillEmitFolders()
    {
        const string xml = """
            <con:soapui-project xmlns:con="http://eviware.com/soapui/config" name="P">
              <con:testSuite name="Filled">
                <con:testCase name="HasStep">
                  <con:testStep type="restrequest" name="Step">
                    <con:config>
                      <con:restRequest name="Step">
                        <con:endpoint>http://x.test</con:endpoint>
                        <con:method>GET</con:method>
                      </con:restRequest>
                    </con:config>
                  </con:testStep>
                </con:testCase>
                <con:testCase name="EmptyCase"/>
              </con:testSuite>
              <con:testSuite name="EmptySuite"/>
            </con:soapui-project>
            """;

        var c = SoapUiImporter.ImportFromString(xml);
        c.Folders.Select(f => f.Name).Should().BeEquivalentTo(new[] { "Filled", "EmptySuite" });

        var empty = c.Folders.First(f => f.Name == "EmptySuite");
        empty.Folders.Should().BeEmpty();

        var emptyCase = c.Folders.First(f => f.Name == "Filled").Folders.First(f => f.Name == "EmptyCase");
        emptyCase.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Import_MissingEndpoint_LeavesUrlEmpty_DoesNotThrow()
    {
        const string xml = """
            <con:soapui-project xmlns:con="http://eviware.com/soapui/config" name="P">
              <con:testSuite name="S">
                <con:testCase name="C">
                  <con:testStep type="request" name="Step">
                    <con:config>
                      <con:interface>I</con:interface>
                      <con:operation>Op</con:operation>
                      <con:request name="Step">
                        <con:request><![CDATA[<soapenv:Envelope/>]]></con:request>
                      </con:request>
                    </con:config>
                  </con:testStep>
                </con:testCase>
              </con:testSuite>
            </con:soapui-project>
            """;

        var c = SoapUiImporter.ImportFromString(xml);
        c.Folders[0].Folders[0].Requests[0].Url.Should().BeEmpty();
    }

    [Fact]
    public void Import_PropertyExpansion_AllScopesCollapseToInterpolation()
    {
        const string xml = """
            <con:soapui-project xmlns:con="http://eviware.com/soapui/config" name="P">
              <con:testSuite name="S">
                <con:testCase name="C">
                  <con:testStep type="restrequest" name="Step">
                    <con:config>
                      <con:restRequest name="Step">
                        <con:endpoint>${#Project#host}/${#TestCase#path}?t=${#Global#tok}&amp;u=${plain}</con:endpoint>
                        <con:method>GET</con:method>
                      </con:restRequest>
                    </con:config>
                  </con:testStep>
                </con:testCase>
              </con:testSuite>
            </con:soapui-project>
            """;

        var c = SoapUiImporter.ImportFromString(xml);
        var url = c.Folders[0].Folders[0].Requests[0].Url;
        url.Should().Be("{{host}}/{{path}}?t={{tok}}&u={{plain}}");
    }

    [Fact]
    public void Import_WssTimeToLiveSetting_BecomesTimestampConfig()
    {
        const string xml = """
            <con:soapui-project xmlns:con="http://eviware.com/soapui/config"
                                xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                                name="WssProj">
              <con:interface xsi:type="con:WsdlInterface" name="WeatherSoap">
                <con:operation name="GetWeather" action="urn:GetWeather">
                  <con:call name="Request 1">
                    <con:settings>
                      <con:setting id="com.eviware.soapui.impl.wsdl.WsdlRequest@wss-time-to-live">60000</con:setting>
                    </con:settings>
                    <con:request><![CDATA[<soapenv:Envelope/>]]></con:request>
                  </con:call>
                </con:operation>
              </con:interface>
            </con:soapui-project>
            """;

        var c = SoapUiImporter.ImportFromString(xml);
        var request = c.Folders[0].Folders.First(f => f.Name == "GetWeather").Requests[0];

        request.Soap.Should().NotBeNull();
        request.Soap!.Timestamp.Should().NotBeNull();
        // SoapUI stores 60000 ms — migrated as 60 seconds.
        request.Soap.Timestamp!.TimeToLiveSeconds.Should().Be(60);
    }

    [Fact]
    public void Import_NoWssSetting_LeavesSoapConfigNull()
    {
        var c = SoapUiImporter.ImportFromString(CatalogOnlyProject);
        var request = c.Folders[0].Folders.First(f => f.Name == "GetWeather").Requests[0];
        request.Soap.Should().BeNull();
    }

    [Fact]
    public void Import_BodyWithEncodedCarriageReturns_IsNormalizedToLineFeeds()
    {
        // SoapUI encodes CRs as &#xD; / &#13; entities — they decode to literal \r, which
        // must be normalized so the editor doesn't show stray \r glyphs.
        const string xml = """
            <con:soapui-project xmlns:con="http://eviware.com/soapui/config"
                                xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                                name="CrProj">
              <con:interface xsi:type="con:WsdlInterface" name="I">
                <con:operation name="Op" action="urn:Op">
                  <con:call name="Call">
                    <con:request>&lt;soapenv:Envelope&gt;&#13;&lt;soapenv:Body/&gt;&#13;&lt;/soapenv:Envelope&gt;</con:request>
                  </con:call>
                </con:operation>
              </con:interface>
            </con:soapui-project>
            """;

        var c = SoapUiImporter.ImportFromString(xml);
        var content = c.Folders[0].Folders[0].Requests[0].Body.Content;

        content.Should().NotBeNull();
        content!.Should().NotContain("\r");
        content.Should().Contain("\n");
    }

    [Fact]
    public void Import_BodyWithLiteralBackslashR_IsStripped()
    {
        // Real SoapUI projects store request bodies with the carriage return literalized as
        // the two-character text "\r" at each line end. It must not survive into the body.
        // Built as a plain string (not a raw literal) so the test is line-ending agnostic.
        var xml =
            "<con:soapui-project xmlns:con=\"http://eviware.com/soapui/config\" " +
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" name=\"P\">" +
            "<con:interface xsi:type=\"con:WsdlInterface\" name=\"I\">" +
            "<con:operation name=\"Op\" action=\"urn:Op\">" +
            "<con:call name=\"Call\"><con:request><![CDATA[" +
            "<soapenv:Envelope>\\r\n   <soapenv:Body/>\\r\n</soapenv:Envelope>" +
            "]]></con:request></con:call>" +
            "</con:operation></con:interface></con:soapui-project>";

        var c = SoapUiImporter.ImportFromString(xml);
        var content = c.Folders[0].Folders[0].Requests[0].Body.Content;

        content.Should().NotBeNull();
        content!.Should().NotContain("\\r");  // literal backslash-r text
        content.Should().NotContain("\r");    // real carriage return
        content.Should().Contain("<soapenv:Body/>");
    }

    [Fact]
    public void Import_MalformedXml_ThrowsInvalidData()
    {
        Action act = () => SoapUiImporter.ImportFromString("not really xml <");
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Import_WrongRootElement_ThrowsInvalidData()
    {
        Action act = () => SoapUiImporter.ImportFromString("<something-else/>");
        act.Should().Throw<InvalidDataException>();
    }
}
