namespace Vegha.Tests.Unit.Core.GraphQL.TestData;

/// <summary>Shared introspection-response fixtures (a compact but representative schema:
/// roots, object, interface, union, enum, input object, custom scalar, directive).</summary>
public static class IntrospectionFixtures
{
    public const string Small = """
    {
      "data": {
        "__schema": {
          "queryType": { "name": "Query" },
          "mutationType": { "name": "Mutation" },
          "subscriptionType": { "name": "Subscription" },
          "types": [
            {
              "kind": "OBJECT", "name": "Query", "description": "Root query",
              "fields": [
                {
                  "name": "user", "description": "Look up a user",
                  "args": [
                    { "name": "id", "description": "User id", "type": { "kind": "NON_NULL", "name": null, "ofType": { "kind": "SCALAR", "name": "ID" } }, "defaultValue": null }
                  ],
                  "type": { "kind": "OBJECT", "name": "User" },
                  "isDeprecated": false, "deprecationReason": null
                },
                {
                  "name": "search",
                  "args": [
                    { "name": "term", "type": { "kind": "SCALAR", "name": "String" }, "defaultValue": "\"*\"" }
                  ],
                  "type": { "kind": "LIST", "name": null, "ofType": { "kind": "UNION", "name": "SearchResult" } },
                  "isDeprecated": false
                }
              ],
              "inputFields": null, "interfaces": [], "enumValues": null, "possibleTypes": null
            },
            {
              "kind": "OBJECT", "name": "Mutation",
              "fields": [
                {
                  "name": "createUser",
                  "args": [
                    { "name": "input", "type": { "kind": "NON_NULL", "name": null, "ofType": { "kind": "INPUT_OBJECT", "name": "CreateUserInput" } } }
                  ],
                  "type": { "kind": "OBJECT", "name": "User" },
                  "isDeprecated": false
                }
              ],
              "interfaces": []
            },
            {
              "kind": "OBJECT", "name": "Subscription",
              "fields": [
                { "name": "userChanged", "args": [], "type": { "kind": "OBJECT", "name": "User" }, "isDeprecated": false }
              ],
              "interfaces": []
            },
            {
              "kind": "OBJECT", "name": "User", "description": "A person",
              "fields": [
                { "name": "id", "args": [], "type": { "kind": "NON_NULL", "name": null, "ofType": { "kind": "SCALAR", "name": "ID" } }, "isDeprecated": false },
                { "name": "email", "args": [], "type": { "kind": "SCALAR", "name": "String" }, "isDeprecated": false },
                { "name": "role", "args": [], "type": { "kind": "ENUM", "name": "Role" }, "isDeprecated": false },
                { "name": "friends", "args": [ { "name": "first", "type": { "kind": "SCALAR", "name": "Int" } } ], "type": { "kind": "LIST", "name": null, "ofType": { "kind": "NON_NULL", "name": null, "ofType": { "kind": "OBJECT", "name": "User" } } }, "isDeprecated": false },
                { "name": "legacyName", "args": [], "type": { "kind": "SCALAR", "name": "String" }, "isDeprecated": true, "deprecationReason": "Use email" }
              ],
              "interfaces": [ { "kind": "INTERFACE", "name": "Node" } ]
            },
            {
              "kind": "INTERFACE", "name": "Node",
              "fields": [
                { "name": "id", "args": [], "type": { "kind": "NON_NULL", "name": null, "ofType": { "kind": "SCALAR", "name": "ID" } }, "isDeprecated": false }
              ],
              "possibleTypes": [ { "kind": "OBJECT", "name": "User" } ]
            },
            {
              "kind": "UNION", "name": "SearchResult",
              "possibleTypes": [ { "kind": "OBJECT", "name": "User" } ]
            },
            {
              "kind": "ENUM", "name": "Role",
              "enumValues": [
                { "name": "ADMIN", "description": "Full access", "isDeprecated": false },
                { "name": "MEMBER", "isDeprecated": false }
              ]
            },
            {
              "kind": "INPUT_OBJECT", "name": "CreateUserInput",
              "inputFields": [
                { "name": "email", "type": { "kind": "NON_NULL", "name": null, "ofType": { "kind": "SCALAR", "name": "String" } }, "defaultValue": null },
                { "name": "role", "type": { "kind": "ENUM", "name": "Role" }, "defaultValue": "MEMBER" }
              ]
            },
            { "kind": "SCALAR", "name": "DateTime", "description": "ISO-8601 instant" },
            { "kind": "SCALAR", "name": "String" },
            { "kind": "SCALAR", "name": "ID" },
            { "kind": "SCALAR", "name": "Int" },
            { "kind": "OBJECT", "name": "__Ignored", "fields": [] }
          ],
          "directives": [
            {
              "name": "cached", "description": "Server-side cache hint",
              "locations": [ "FIELD", "QUERY" ],
              "args": [ { "name": "ttl", "type": { "kind": "SCALAR", "name": "Int" }, "defaultValue": "60" } ]
            }
          ]
        }
      }
    }
    """;

    public const string IntrospectionDisabled = """
    {
      "errors": [
        { "message": "GraphQL introspection is not allowed by Apollo Server, but the query contained __schema or __type." }
      ]
    }
    """;
}
