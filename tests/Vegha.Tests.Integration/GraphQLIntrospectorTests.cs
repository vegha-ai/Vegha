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
}
