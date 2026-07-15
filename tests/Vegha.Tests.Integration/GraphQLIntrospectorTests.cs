using Vegha.Core.Requests;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>Tests for the introspection-response parser. We feed a hand-crafted
/// __schema payload (matching the standard introspection query shape) and verify
/// the parser pulls out types, fields, args, type-ref formatting, and skips
/// __ meta types.</summary>
public class GraphQLIntrospectorTests
{
    private const string SampleResponse = """
        {
          "data": {
            "__schema": {
              "queryType": { "name": "Query" },
              "mutationType": { "name": "Mutation" },
              "subscriptionType": null,
              "types": [
                {
                  "kind": "OBJECT",
                  "name": "Query",
                  "description": "Root query.",
                  "fields": [
                    {
                      "name": "user",
                      "description": "Lookup a user.",
                      "type": {
                        "kind": "NON_NULL", "name": null,
                        "ofType": { "kind": "OBJECT", "name": "User", "ofType": null }
                      },
                      "args": [
                        {
                          "name": "id",
                          "type": {
                            "kind": "NON_NULL", "name": null,
                            "ofType": { "kind": "SCALAR", "name": "ID" }
                          }
                        }
                      ]
                    }
                  ],
                  "enumValues": null
                },
                {
                  "kind": "OBJECT",
                  "name": "User",
                  "fields": [
                    {
                      "name": "tags",
                      "type": {
                        "kind": "LIST", "name": null,
                        "ofType": {
                          "kind": "NON_NULL", "name": null,
                          "ofType": { "kind": "SCALAR", "name": "String", "ofType": null }
                        }
                      },
                      "args": []
                    }
                  ]
                },
                {
                  "kind": "ENUM",
                  "name": "Color",
                  "fields": null,
                  "enumValues": [{ "name": "RED" }, { "name": "BLUE" }]
                },
                {
                  "kind": "OBJECT",
                  "name": "__Hidden",
                  "fields": [],
                  "enumValues": null
                }
              ]
            }
          }
        }
        """;

    [Fact]
    public void ParseSchema_ExtractsRootTypeNames()
    {
        var s = GraphQLIntrospector.ParseSchema(SampleResponse);
        s.QueryType.Should().Be("Query");
        s.MutationType.Should().Be("Mutation");
        s.SubscriptionType.Should().BeNull();
    }

    [Fact]
    public void ParseSchema_SkipsMetaTypes_StartingWithDoubleUnderscore()
    {
        var s = GraphQLIntrospector.ParseSchema(SampleResponse);
        s.Types.Should().NotContain(t => t.Name.StartsWith("__"));
    }

    [Fact]
    public void ParseSchema_FormatsNonNullAndListTypeRefs()
    {
        var s = GraphQLIntrospector.ParseSchema(SampleResponse);
        var query = s.Types.Single(t => t.Name == "Query");
        var user = query.Fields.Single(f => f.Name == "user");
        user.TypeRef.Should().Be("User!");
        user.Args.Single().TypeRef.Should().Be("ID!");

        var userType = s.Types.Single(t => t.Name == "User");
        var tags = userType.Fields.Single(f => f.Name == "tags");
        tags.TypeRef.Should().Be("[String!]");
    }

    [Fact]
    public void ParseSchema_ExtractsEnumValues()
    {
        var s = GraphQLIntrospector.ParseSchema(SampleResponse);
        var color = s.Types.Single(t => t.Name == "Color");
        color.Kind.Should().Be("ENUM");
        color.EnumValues.Should().BeEquivalentTo(new[] { "RED", "BLUE" });
    }

    [Fact]
    public void IntrospectionQuery_HasSchemaShape()
    {
        // Sanity: the query shape we ship must select __schema with the four fields the
        // parser expects (queryType, mutationType, subscriptionType, types).
        GraphQLIntrospector.IntrospectionQuery.Should().Contain("__schema");
        GraphQLIntrospector.IntrospectionQuery.Should().Contain("queryType");
        GraphQLIntrospector.IntrospectionQuery.Should().Contain("types");
        GraphQLIntrospector.IntrospectionQuery.Should().Contain("fields");
    }

    [Fact]
    public void IntrospectionQueryChain_AllVariants_AreValidGraphQL()
    {
        foreach (var query in Vegha.Core.GraphQL.Schema.IntrospectionQueries.Chain)
        {
            var act = () => GraphQLParser.Parser.Parse(query);
            act.Should().NotThrow("every fallback introspection query must parse");
        }
    }
}

/// <summary>WireMock coverage for <see cref="GraphQLIntrospector.IntrospectRawAsync"/> and the
/// full→reduced→minimal fallback chain the editor drives.</summary>
public class GraphQLIntrospectRawTests : IAsyncLifetime
{
    private WireMock.Server.WireMockServer _server = null!;
    private HttpClient _client = null!;
    private HttpExecutor _executor = null!;

    public Task InitializeAsync()
    {
        _server = WireMock.Server.WireMockServer.Start();
        _client = new HttpClient();
        _executor = new HttpExecutor(_client);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server.Stop(); _server.Dispose(); _client.Dispose();
        return Task.CompletedTask;
    }

    private const string MinimalSchemaResponse = """
        { "data": { "__schema": {
          "queryType": { "name": "Query" },
          "types": [ { "kind": "OBJECT", "name": "Query",
            "fields": [ { "name": "ping", "type": { "kind": "SCALAR", "name": "String" }, "args": [] } ] } ]
        } } }
        """;

    [Fact]
    public async Task IntrospectRaw_ReturnsBody_EvenOn400()
    {
        // GraphQL validation rejections commonly come back as HTTP 400 + errors JSON;
        // the raw API must hand that body to the reader instead of throwing.
        _server.Given(WireMock.RequestBuilders.Request.Create().WithPath("/graphql").UsingPost())
            .RespondWith(WireMock.ResponseBuilders.Response.Create()
                .WithStatusCode(400)
                .WithBody("""{ "errors": [ { "message": "Unknown field \"isRepeatable\"" } ] }"""));

        var body = await GraphQLIntrospector.IntrospectRawAsync(
            _executor, new Uri(_server.Urls[0] + "/graphql"),
            Vegha.Core.GraphQL.Schema.IntrospectionQueries.Full);

        body.Should().Contain("Unknown field");
    }

    [Fact]
    public async Task FallbackChain_FullRejected_MinimalSucceeds()
    {
        // Reject anything selecting directives (the Full query), accept the rest.
        _server.Given(WireMock.RequestBuilders.Request.Create().WithPath("/graphql").UsingPost()
                .WithBody(b => b!.Contains("directives")))
            .RespondWith(WireMock.ResponseBuilders.Response.Create()
                .WithStatusCode(200)
                .WithBody("""{ "errors": [ { "message": "Cannot query directives" } ] }"""));
        _server.Given(WireMock.RequestBuilders.Request.Create().WithPath("/graphql").UsingPost())
            .RespondWith(WireMock.ResponseBuilders.Response.Create()
                .WithStatusCode(200)
                .WithBody(MinimalSchemaResponse));

        // Drive the same chain logic the editor uses.
        Vegha.Core.GraphQL.Schema.GraphQLSchemaModel? model = null;
        foreach (var query in Vegha.Core.GraphQL.Schema.IntrospectionQueries.Chain)
        {
            var raw = await GraphQLIntrospector.IntrospectRawAsync(
                _executor, new Uri(_server.Urls[0] + "/graphql"), query);
            try
            {
                model = Vegha.Core.GraphQL.Schema.IntrospectionJsonReader.Parse(raw);
                break;
            }
            catch (Vegha.Core.GraphQL.Schema.GraphQLIntrospectionException ex) when (ex.ServerRejected)
            {
                // try next variant
            }
        }

        model.Should().NotBeNull("the minimal fallback should have succeeded");
        model!.QueryTypeName.Should().Be("Query");
    }

    [Fact]
    public async Task IntrospectionDisabled_AllVariantsRejected_SurfacesServerMessage()
    {
        _server.Given(WireMock.RequestBuilders.Request.Create().WithPath("/graphql").UsingPost())
            .RespondWith(WireMock.ResponseBuilders.Response.Create()
                .WithStatusCode(200)
                .WithBody("""{ "errors": [ { "message": "introspection is disabled" } ] }"""));

        Vegha.Core.GraphQL.Schema.GraphQLIntrospectionException? last = null;
        foreach (var query in Vegha.Core.GraphQL.Schema.IntrospectionQueries.Chain)
        {
            var raw = await GraphQLIntrospector.IntrospectRawAsync(
                _executor, new Uri(_server.Urls[0] + "/graphql"), query);
            try
            {
                Vegha.Core.GraphQL.Schema.IntrospectionJsonReader.Parse(raw);
                Assert.Fail("should have been rejected");
            }
            catch (Vegha.Core.GraphQL.Schema.GraphQLIntrospectionException ex) when (ex.ServerRejected)
            {
                last = ex;
            }
        }
        last.Should().NotBeNull();
        last!.Message.Should().Contain("introspection is disabled");
    }
}
