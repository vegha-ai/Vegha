using System.IO;
using System.IO.Compression;
using System.Text;
using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

/// <summary>Format-detection tests for the unified Import pipeline (File / URL / Git / GitHub
/// tabs all funnel through this). The pipeline's job is to look at raw bytes, decide what
/// they are, and stage either a Collection or an Environment.</summary>
public class ImportPipelineTests
{
    [Fact]
    public void Wsdl_DetectedFromBytes()
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
            </wsdl:definitions>
            """;
        var r = ImportPipeline.DetectAndImport(Encoding.UTF8.GetBytes(wsdl), "x.wsdl");

        r.Success.Should().BeTrue();
        r.FormatLabel.Should().Be("SOAP WSDL 1.1");
        r.Collection.Should().NotBeNull();
    }

    [Fact]
    public void SoapUiProject_DetectedFromBytes()
    {
        const string project = """
            <con:soapui-project xmlns:con="http://eviware.com/soapui/config" name="AcmeProject">
              <con:testSuite name="Smoke">
                <con:testCase name="Case">
                  <con:testStep type="restrequest" name="Ping">
                    <con:config>
                      <con:restRequest name="Ping">
                        <con:endpoint>http://acme.test</con:endpoint>
                        <con:method>GET</con:method>
                      </con:restRequest>
                    </con:config>
                  </con:testStep>
                </con:testCase>
              </con:testSuite>
            </con:soapui-project>
            """;
        var r = ImportPipeline.DetectAndImport(Encoding.UTF8.GetBytes(project), "acme-soapui-project.xml");

        r.Success.Should().BeTrue();
        r.FormatLabel.Should().Be("SoapUI project");
        r.Collection.Should().NotBeNull();
        r.Collection!.Name.Should().Be("AcmeProject");
    }

    [Fact]
    public void SoapUiProject_WithCachedWsdl_NotMisdetectedAsWsdl()
    {
        // SoapUI embeds cached WSDL inside <con:definitionCache>, so this document contains
        // the WSDL namespace + "definitions" markers. The SoapUI sniff must still win.
        const string project = """
            <con:soapui-project xmlns:con="http://eviware.com/soapui/config" name="WsdlBackedProject">
              <con:interface name="WeatherSoap">
                <con:definitionCache>
                  <con:part>
                    <con:content><![CDATA[<definitions xmlns="http://schemas.xmlsoap.org/wsdl/"/>]]></con:content>
                  </con:part>
                </con:definitionCache>
              </con:interface>
              <con:testSuite name="Smoke">
                <con:testCase name="Case">
                  <con:testStep type="restrequest" name="Ping">
                    <con:config>
                      <con:restRequest name="Ping">
                        <con:endpoint>http://acme.test</con:endpoint>
                        <con:method>GET</con:method>
                      </con:restRequest>
                    </con:config>
                  </con:testStep>
                </con:testCase>
              </con:testSuite>
            </con:soapui-project>
            """;
        var r = ImportPipeline.DetectAndImport(Encoding.UTF8.GetBytes(project), "project.xml");

        r.Success.Should().BeTrue();
        r.FormatLabel.Should().Be("SoapUI project");
        r.FormatLabel.Should().NotBe("SOAP WSDL 1.1");
    }

    [Fact]
    public void PostmanV2_Detected()
    {
        const string json = """
            {
              "info": { "name": "X", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [
                { "name": "GetUsers", "request": { "method": "GET", "url": "https://api.example.com/users" } }
              ]
            }
            """;
        var r = ImportPipeline.DetectAndImport(Encoding.UTF8.GetBytes(json), "x.json");
        r.FormatLabel.Should().Be("Postman v2.1 collection");
        r.Collection.Should().NotBeNull();
    }

    [Fact]
    public void PostmanEnvironment_Detected()
    {
        const string json = """
            {
              "name": "Dev",
              "values": [{ "key": "host", "value": "https://dev.api" }]
            }
            """;
        var r = ImportPipeline.DetectAndImport(Encoding.UTF8.GetBytes(json), "dev.json");
        r.FormatLabel.Should().Be("Postman environment");
        r.Environment.Should().NotBeNull();
        r.Collection.Should().BeNull();
    }

    [Fact]
    public void OpenApi_Detected()
    {
        const string json = """
            {
              "openapi": "3.0.0",
              "info": { "title": "X", "version": "1" },
              "paths": {}
            }
            """;
        var r = ImportPipeline.DetectAndImport(Encoding.UTF8.GetBytes(json), "x.json");
        r.FormatLabel.Should().Be("OpenAPI / Swagger");
        r.Collection.Should().NotBeNull();
    }

    [Fact]
    public void OpenApiYaml_DetectedFromFilenameHint()
    {
        const string yaml = """
            openapi: 3.0.3
            info:
              title: Yaml Petstore
              version: 1.0.0
            servers:
              - url: https://api.petstore.test
            paths:
              /pets:
                get:
                  operationId: listPets
                  responses:
                    '200':
                      description: ok
            """;
        var r = ImportPipeline.DetectAndImport(Encoding.UTF8.GetBytes(yaml), "petstore.yml");
        r.Success.Should().BeTrue();
        r.FormatLabel.Should().Be("OpenAPI / Swagger (YAML)");
        r.Collection.Should().NotBeNull();
        r.Collection!.Name.Should().Be("Yaml Petstore");
    }

    [Fact]
    public void OpenApiYaml_DetectedFromContentSniff_WhenNoFilenameHint()
    {
        const string yaml = """
            openapi: "3.0.3"
            info:
              title: Sniffed
              version: 1.0.0
            paths:
              /ping:
                get:
                  operationId: ping
                  responses:
                    '200':
                      description: ok
            """;
        var r = ImportPipeline.DetectAndImport(Encoding.UTF8.GetBytes(yaml));
        r.Success.Should().BeTrue();
        r.FormatLabel.Should().Be("OpenAPI / Swagger (YAML)");
        r.Collection!.Name.Should().Be("Sniffed");
    }

    [Fact]
    public void SwaggerYaml_AlsoDetected()
    {
        const string yaml = """
            swagger: "2.0"
            info:
              title: Legacy
              version: "1"
            host: legacy.test
            basePath: /v1
            schemes: [https]
            paths:
              /ping:
                get:
                  operationId: ping
                  responses:
                    '200':
                      description: ok
            """;
        var r = ImportPipeline.DetectAndImport(Encoding.UTF8.GetBytes(yaml), "legacy.yaml");
        r.Success.Should().BeTrue();
        r.FormatLabel.Should().Be("OpenAPI / Swagger (YAML)");
        r.Collection!.Name.Should().Be("Legacy");
    }

    [Fact]
    public void NonOpenApiYaml_Rejected()
    {
        // YAML-looking content that isn't an OpenAPI spec — pipeline should still fall through.
        const string yaml = """
            name: My Workspace
            version: 1
            collections:
              - foo
              - bar
            """;
        var r = ImportPipeline.DetectAndImport(Encoding.UTF8.GetBytes(yaml), "workspace.yml");
        r.Success.Should().BeFalse();
    }

    [Fact]
    public void EmptyPayload_Fails()
    {
        var r = ImportPipeline.DetectAndImport(Array.Empty<byte>());
        r.Success.Should().BeFalse();
        r.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void UnknownText_Fails()
    {
        var r = ImportPipeline.DetectAndImport(Encoding.UTF8.GetBytes("not a known format"));
        r.Success.Should().BeFalse();
    }

    [Fact]
    public void Zip_ContainingBruTree_DetectedAsBrunoFolder()
    {
        // Build a tiny zip in-memory containing a minimal Bruno collection.
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = z.CreateEntry("MyCol/collection.bru");
            using var s = entry.Open();
            using var w = new StreamWriter(s);
            w.Write("meta {\n  name: MyCol\n  type: collection\n}\n");
        }
        var bytes = ms.ToArray();

        var r = ImportPipeline.DetectAndImport(bytes, "MyCol.zip");

        r.Success.Should().BeTrue();
        r.FormatLabel.Should().Be("Bruno collection (zip)");
        r.FolderPath.Should().NotBeNull();
        Directory.EnumerateFiles(r.FolderPath!, "*.bru", SearchOption.AllDirectories).Should().NotBeEmpty();
    }

    [Fact]
    public void DirectoryPath_WithBruFiles_DetectedAsBrunoFolder()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "Vegha-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "collection.bru"),
                "meta {\n  name: D\n  type: collection\n}\n");
            File.WriteAllText(Path.Combine(tmp, "GetUsers.bru"),
                "meta {\n  name: GetUsers\n  type: http\n}\nget {\n  url: https://x\n}\n");

            var r = ImportPipeline.DetectAndImportPath(tmp);
            r.Success.Should().BeTrue();
            r.FormatLabel.Should().Be("Bruno collection (folder)");
            r.FolderPath.Should().Be(tmp);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }
}
