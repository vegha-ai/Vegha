using Vegha.Core.Domain;
using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

public class BruCollectionWriterTests : IDisposable
{
    private readonly string _tempDir;

    public BruCollectionWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BruWriterTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Write_EmitsCollectionBruAndPerRequestBru()
    {
        var c = new Collection
        {
            Name = "WSDL",
            Requests = new List<RequestItem>
            {
                new() { Name = "GetWeather", Method = "POST", Url = "http://x", Kind = RequestKind.Soap },
                new() { Name = "GetForecast", Method = "POST", Url = "http://y", Kind = RequestKind.Soap },
            },
        };

        BruCollectionWriter.Write(_tempDir, c);

        File.Exists(Path.Combine(_tempDir, "collection.bru")).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, "GetWeather.bru")).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, "GetForecast.bru")).Should().BeTrue();
    }

    [Fact]
    public void Write_RoundTrips_CollectionLoaderReadsItBack()
    {
        var original = new Collection
        {
            Name = "Imported",
            Variables = new List<KvPair> { new("endpoint", "http://example") },
            Requests = new List<RequestItem>
            {
                new() { Name = "ChangeAccount", Method = "POST", Url = "{{endpoint}}", Kind = RequestKind.Soap,
                        Headers = new List<KvPair> { new("SOAPAction", "\"x\"") },
                        Body = new BodyConfig { Mode = BodyMode.Xml, Content = "<root/>" }, Sequence = 1 },
                new() { Name = "GetAccounts", Method = "POST", Url = "{{endpoint}}", Kind = RequestKind.Soap, Sequence = 2 },
            },
        };

        BruCollectionWriter.Write(_tempDir, original);
        var reloaded = CollectionLoader.Load(_tempDir);

        reloaded.Name.Should().Be("Imported");
        reloaded.Requests.Should().HaveCount(2);
        reloaded.Requests.Select(r => r.Name).Should().BeEquivalentTo(new[] { "ChangeAccount", "GetAccounts" });

        var change = reloaded.Requests.First(r => r.Name == "ChangeAccount");
        change.Method.Should().Be("POST");
        change.Url.Should().Be("{{endpoint}}");
        change.Body.Mode.Should().Be(BodyMode.Xml);
        change.Body.Content.Should().Contain("<root/>");
    }

    [Fact]
    public void Write_DuplicateRequestNames_Disambiguates()
    {
        // Two operations with the same name: the writer must not stomp the first file.
        var c = new Collection
        {
            Name = "Dups",
            Requests = new List<RequestItem>
            {
                new() { Name = "Op", Url = "http://a", Method = "POST" },
                new() { Name = "Op", Url = "http://b", Method = "POST" },
            },
        };

        BruCollectionWriter.Write(_tempDir, c);

        File.Exists(Path.Combine(_tempDir, "Op.bru")).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, "Op-2.bru")).Should().BeTrue();
    }

    [Fact]
    public void Write_NestedFolders_RecursesAndEmitsFolderBru()
    {
        var c = new Collection
        {
            Name = "Nested",
            Folders = new List<Folder>
            {
                new() { Name = "Inner", Requests = new List<RequestItem>
                {
                    new() { Name = "Deep", Url = "http://d", Method = "GET" },
                }},
            },
        };

        BruCollectionWriter.Write(_tempDir, c);

        File.Exists(Path.Combine(_tempDir, "Inner", "folder.bru")).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, "Inner", "Deep.bru")).Should().BeTrue();

        var reloaded = CollectionLoader.Load(_tempDir);
        reloaded.Folders.Should().ContainSingle();
        reloaded.Folders[0].Requests.Should().ContainSingle().Which.Name.Should().Be("Deep");
    }

    [Fact]
    public void Write_WsdlImportedCollection_RoundTripsAllOperations()
    {
        const string wsdl = """
            <wsdl:definitions
                xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                xmlns:tns="http://acme.test/wx"
                xmlns:msg="http://acme.test/wx/msg"
                xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                targetNamespace="http://acme.test/wx" name="WxService">
              <wsdl:types>
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                           targetNamespace="http://acme.test/wx/msg" elementFormDefault="qualified">
                  <xs:element name="GetWeatherReq">
                    <xs:complexType>
                      <xs:sequence>
                        <xs:element name="city" type="xs:string"/>
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
              <wsdl:service name="WxService">
                <wsdl:port name="P" binding="tns:WxBinding">
                  <soap:address location="http://example.org/wx"/>
                </wsdl:port>
              </wsdl:service>
            </wsdl:definitions>
            """;

        var imported = WsdlImporter.ImportFromString(wsdl);
        BruCollectionWriter.Write(_tempDir, imported);
        var reloaded = CollectionLoader.Load(_tempDir);

        reloaded.Requests.Should().ContainSingle();
        var r = reloaded.Requests[0];
        r.Name.Should().Be("GetWeather");
        r.Body.Mode.Should().Be(BodyMode.Xml);
        r.Body.Content.Should().Contain(":GetWeatherReq");
        r.Body.Content.Should().Contain(":city>?</");
    }
}
