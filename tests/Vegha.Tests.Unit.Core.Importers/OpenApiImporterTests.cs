using Vegha.Core.Domain;
using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

public class OpenApiImporterTests
{
    [Fact]
    public void OpenApi3_BasicSpec_PopulatesNameAndBaseUrl()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "Petstore", "version": "1.0.0" },
              "servers": [{ "url": "https://api.petstore.test" }],
              "paths": {
                "/pets": {
                  "get": {
                    "operationId": "listPets",
                    "tags": ["pets"],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var c = OpenApiImporter.ImportFromString(spec);
        c.Name.Should().Be("Petstore");
        c.Version.Should().Be("1.0.0");
        c.Variables.Should().ContainSingle();
        c.Variables[0].Name.Should().Be("baseUrl");
        c.Variables[0].Value.Should().Be("https://api.petstore.test");
    }

    [Fact]
    public void OpenApi3_TaggedOperations_GroupIntoFolders()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "servers": [{ "url": "https://x.test" }],
              "paths": {
                "/users": { "get": { "tags": ["users"], "operationId": "listUsers", "responses": { "200": {} } } },
                "/orders": { "get": { "tags": ["orders"], "operationId": "listOrders", "responses": { "200": {} } } },
                "/health": { "get": { "operationId": "health", "responses": { "200": {} } } }
              }
            }
            """;

        var c = OpenApiImporter.ImportFromString(spec);
        c.Folders.Should().HaveCount(2);
        c.Folders.Select(f => f.Name).Should().BeEquivalentTo(new[] { "users", "orders" });
        c.Requests.Should().ContainSingle();
        c.Requests[0].Name.Should().Be("health");
    }

    [Fact]
    public void OpenApi3_PathParams_AreCapturedAsPathParams()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "X", "version": "1" },
              "servers": [{ "url": "https://x.test" }],
              "paths": {
                "/users/{id}": {
                  "get": {
                    "operationId": "getUser",
                    "parameters": [
                      { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } },
                      { "name": "verbose", "in": "query", "schema": { "type": "boolean" } }
                    ],
                    "responses": { "200": {} }
                  }
                }
              }
            }
            """;

        var c = OpenApiImporter.ImportFromString(spec);
        var req = c.Requests[0];
        req.Url.Should().Be("{{baseUrl}}/users/{id}");
        req.PathParams.Should().ContainSingle().Which.Name.Should().Be("id");
        req.Params.Should().ContainSingle().Which.Name.Should().Be("verbose");
    }

    [Fact]
    public void OpenApi3_JsonRequestBody_BuildsBodyAndContentTypeHeader()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/users": {
                  "post": {
                    "operationId": "createUser",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": {
                            "type": "object",
                            "properties": {
                              "name": { "type": "string" },
                              "age": { "type": "integer" }
                            }
                          }
                        }
                      }
                    },
                    "responses": { "201": {} }
                  }
                }
              }
            }
            """;

        var c = OpenApiImporter.ImportFromString(spec);
        var req = c.Requests[0];
        req.Method.Should().Be("POST");
        req.Body.Mode.Should().Be(BodyMode.Json);
        req.Body.Content.Should().Contain("\"name\":");
        req.Headers.Should().Contain(h => h.Name == "Content-Type" && h.Value == "application/json");
    }

    [Fact]
    public void Swagger2_IsAlsoAccepted()
    {
        const string spec = """
            {
              "swagger": "2.0",
              "info": { "title": "Legacy", "version": "1" },
              "host": "legacy.test",
              "basePath": "/v1",
              "schemes": ["https"],
              "paths": {
                "/ping": { "get": { "operationId": "ping", "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;

        var c = OpenApiImporter.ImportFromString(spec);
        c.Name.Should().Be("Legacy");
        c.Requests.Should().ContainSingle();
        c.Requests[0].Method.Should().Be("GET");
    }

    [Fact]
    public void InvalidSpec_Throws()
    {
        var act = () => OpenApiImporter.ImportFromString("this is not openapi");
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void OpenApi3_YamlInput_IsAccepted()
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
                  tags: [pets]
                  responses:
                    '200':
                      description: ok
            """;

        var c = OpenApiImporter.ImportFromString(yaml);
        c.Name.Should().Be("Yaml Petstore");
        c.Variables.Should().Contain(v => v.Name == "baseUrl" && v.Value == "https://api.petstore.test");
        c.Folders.Should().ContainSingle().Which.Name.Should().Be("pets");
    }

    [Fact]
    public void OpenApi3_ServerVariables_AreExpandedAndExposed()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "X", "version": "1" },
              "servers": [{
                "url": "https://{opco}.api.example.com:{port}/{env}",
                "variables": {
                  "opco": { "default": "acme", "enum": ["acme", "globex"], "description": "operating company" },
                  "port": { "default": "443" },
                  "env":  { "default": "prod" }
                }
              }],
              "paths": { "/x": { "get": { "operationId": "x", "responses": { "200": {} } } } }
            }
            """;

        var c = OpenApiImporter.ImportFromString(spec);
        c.Variables.Should().Contain(v => v.Name == "baseUrl" && v.Value == "https://acme.api.example.com:443/prod");
        c.Variables.Should().Contain(v => v.Name == "opco" && v.Value == "acme");
        c.Variables.Should().Contain(v => v.Name == "port" && v.Value == "443");
        c.Variables.Should().Contain(v => v.Name == "env" && v.Value == "prod");
        var opcoVar = c.Variables.First(v => v.Name == "opco");
        opcoVar.Description.Should().Contain("one of: acme, globex");
    }

    [Fact]
    public void OpenApi3_BearerSecurity_MapsToBearerAuth()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "X", "version": "1" },
              "components": {
                "securitySchemes": {
                  "bearerAuth": { "type": "http", "scheme": "bearer" }
                }
              },
              "security": [{ "bearerAuth": [] }],
              "paths": { "/x": { "get": { "operationId": "x", "responses": { "200": {} } } } }
            }
            """;

        var c = OpenApiImporter.ImportFromString(spec);
        c.Auth.Should().NotBeNull();
        c.Auth!.Type.Should().Be(AuthType.Bearer);
        c.Auth.Parameters["token"].Should().Be("{{token}}");
        c.Variables.Should().Contain(v => v.Name == "token");
    }

    [Fact]
    public void OpenApi3_ApiKeySecurity_MapsToApiKeyAuthWithPlacement()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "X", "version": "1" },
              "components": {
                "securitySchemes": {
                  "k": { "type": "apiKey", "name": "X-Custom-Key", "in": "header" }
                }
              },
              "security": [{ "k": [] }],
              "paths": { "/x": { "get": { "operationId": "x", "responses": { "200": {} } } } }
            }
            """;

        var c = OpenApiImporter.ImportFromString(spec);
        c.Auth!.Type.Should().Be(AuthType.ApiKey);
        c.Auth.Parameters["key"].Should().Be("X-Custom-Key");
        c.Auth.Parameters["value"].Should().Be("{{apiKey}}");
        c.Auth.Parameters["placement"].Should().Be("header");
        c.Variables.Should().Contain(v => v.Name == "apiKey");
    }

    [Fact]
    public void OpenApi3_BasicSecurity_MapsToBasicAuth()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "X", "version": "1" },
              "components": {
                "securitySchemes": { "b": { "type": "http", "scheme": "basic" } }
              },
              "security": [{ "b": [] }],
              "paths": { "/x": { "get": { "operationId": "x", "responses": { "200": {} } } } }
            }
            """;

        var c = OpenApiImporter.ImportFromString(spec);
        c.Auth!.Type.Should().Be(AuthType.Basic);
        c.Auth.Parameters["username"].Should().Be("{{username}}");
        c.Auth.Parameters["password"].Should().Be("{{password}}");
        c.Variables.Select(v => v.Name).Should().Contain(new[] { "username", "password" });
    }

    [Fact]
    public void OpenApi3_OAuth2PasswordFlow_MapsToOAuth2Auth()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "X", "version": "1" },
              "components": {
                "securitySchemes": {
                  "oauth": {
                    "type": "oauth2",
                    "flows": {
                      "password": {
                        "tokenUrl": "https://idp.test/oauth/token",
                        "refreshUrl": "https://idp.test/oauth/refresh",
                        "scopes": { "read": "read", "write": "write" }
                      }
                    }
                  }
                }
              },
              "security": [{ "oauth": ["read"] }],
              "paths": { "/x": { "get": { "operationId": "x", "responses": { "200": {} } } } }
            }
            """;

        var c = OpenApiImporter.ImportFromString(spec);
        c.Auth!.Type.Should().Be(AuthType.OAuth2);
        c.Auth.Parameters["grant_type"].Should().Be("password");
        c.Auth.Parameters["access_token_url"].Should().Be("https://idp.test/oauth/token");
        c.Auth.Parameters["refresh_token_url"].Should().Be("https://idp.test/oauth/refresh");
        c.Auth.Parameters["scope"].Should().Be("read write");
        c.Variables.Select(v => v.Name).Should().Contain(
            new[] { "client_id", "client_secret", "username", "password" });
    }

    [Fact]
    public void OpenApi3_OperationLevelSecurity_OverridesCollectionAuth()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "X", "version": "1" },
              "components": {
                "securitySchemes": {
                  "bearerAuth": { "type": "http", "scheme": "bearer" },
                  "apiKey":     { "type": "apiKey", "name": "X-Api-Key", "in": "header" }
                }
              },
              "security": [{ "bearerAuth": [] }],
              "paths": {
                "/inherit":  { "get": { "operationId": "inherits",  "responses": { "200": {} } } },
                "/override": { "get": {
                  "operationId": "overrides",
                  "security": [{ "apiKey": [] }],
                  "responses": { "200": {} }
                } }
              }
            }
            """;

        var c = OpenApiImporter.ImportFromString(spec);
        var inherits  = c.Requests.First(r => r.Name == "inherits");
        var overrides = c.Requests.First(r => r.Name == "overrides");
        inherits.Auth.Should().BeNull();             // inherits collection-level Bearer
        overrides.Auth!.Type.Should().Be(AuthType.ApiKey);
    }

    [Fact]
    public void OpenApi3_AllOfSchema_MergesPropertiesInBody()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/u": {
                  "post": {
                    "operationId": "createU",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": {
                            "allOf": [
                              { "type": "object", "properties": { "name": { "type": "string" } } },
                              { "type": "object", "properties": { "age":  { "type": "integer" } } }
                            ]
                          }
                        }
                      }
                    },
                    "responses": { "201": {} }
                  }
                }
              }
            }
            """;

        var c = OpenApiImporter.ImportFromString(spec);
        var body = c.Requests[0].Body.Content;
        body.Should().Contain("\"name\":");
        body.Should().Contain("\"age\":");
    }

    [Fact]
    public void OpenApi3_OneOfSchema_PicksFirstVariantAndAnnotatesDocs()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "X", "version": "1" },
              "paths": {
                "/u": {
                  "post": {
                    "operationId": "createU",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": {
                            "oneOf": [
                              { "type": "object", "properties": { "kind": { "type": "string" }, "tag": { "type": "string" } } },
                              { "type": "object", "properties": { "kind": { "type": "string" }, "id":  { "type": "integer" } } }
                            ]
                          }
                        }
                      }
                    },
                    "responses": { "201": {} }
                  }
                }
              }
            }
            """;

        var c = OpenApiImporter.ImportFromString(spec);
        var req = c.Requests[0];
        req.Body.Content.Should().Contain("\"tag\":");
        req.Body.Content.Should().NotContain("\"id\":");
        req.Docs.Should().NotBeNullOrEmpty();
        req.Docs!.Should().Contain("oneOf");
    }

    [Fact]
    public void OpenApi3_RefRequestBody_IsResolvedAndSampled()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "X", "version": "1" },
              "components": {
                "requestBodies": {
                  "UserBody": {
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": {
                            "name": { "type": "string" },
                            "age":  { "type": "integer" }
                          }
                        }
                      }
                    }
                  }
                }
              },
              "paths": {
                "/u": {
                  "post": {
                    "operationId": "createU",
                    "requestBody": { "$ref": "#/components/requestBodies/UserBody" },
                    "responses": { "201": {} }
                  }
                }
              }
            }
            """;

        var c = OpenApiImporter.ImportFromString(spec);
        var body = c.Requests[0].Body;
        body.Mode.Should().Be(BodyMode.Json);
        body.Content.Should().Contain("\"name\":");
        body.Content.Should().Contain("\"age\":");
    }

    [Fact]
    public void OpenApi3_RecursiveSchema_DoesNotInfiniteLoop()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "X", "version": "1" },
              "components": {
                "schemas": {
                  "Node": {
                    "type": "object",
                    "properties": {
                      "name":  { "type": "string" },
                      "child": { "$ref": "#/components/schemas/Node" }
                    }
                  }
                }
              },
              "paths": {
                "/n": {
                  "post": {
                    "operationId": "createN",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": { "$ref": "#/components/schemas/Node" }
                        }
                      }
                    },
                    "responses": { "201": {} }
                  }
                }
              }
            }
            """;

        var act = () => OpenApiImporter.ImportFromString(spec);
        act.Should().NotThrow();
        var c = act();
        c.Requests[0].Body.Content.Should().Contain("\"name\":");
    }
}
