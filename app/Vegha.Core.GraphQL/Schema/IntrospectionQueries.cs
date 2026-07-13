namespace Vegha.Core.GraphQL.Schema;

/// <summary>
/// Introspection query variants, most-capable first. Older or locked-down servers reject
/// unknown introspection fields with a validation error, so the caller walks the chain
/// (<see cref="Chain"/>) until one succeeds. All three parse with the same
/// <see cref="IntrospectionJsonReader"/> — missing sections just come back empty.
/// </summary>
public static class IntrospectionQueries
{
    // 7 levels of ofType nesting is the conventional depth (GraphiQL uses the same) —
    // enough for [[Foo!]!]! style wrapping in practice.
    private const string TypeRefFragment = """
        fragment TypeRef on __Type {
          kind
          name
          ofType {
            kind
            name
            ofType {
              kind
              name
              ofType {
                kind
                name
                ofType {
                  kind
                  name
                  ofType {
                    kind
                    name
                    ofType {
                      kind
                      name
                      ofType { kind name }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    /// <summary>Full schema: fields (incl. deprecated), args with defaults, input fields,
    /// interfaces, possible types, enum values, and directives with locations + args.</summary>
    public const string Full = """
        query IntrospectionQuery {
          __schema {
            queryType { name }
            mutationType { name }
            subscriptionType { name }
            types { ...FullType }
            directives {
              name
              description
              locations
              args { ...InputValue }
            }
          }
        }
        fragment FullType on __Type {
          kind
          name
          description
          fields(includeDeprecated: true) {
            name
            description
            args { ...InputValue }
            type { ...TypeRef }
            isDeprecated
            deprecationReason
          }
          inputFields { ...InputValue }
          interfaces { ...TypeRef }
          enumValues(includeDeprecated: true) {
            name
            description
            isDeprecated
          }
          possibleTypes { ...TypeRef }
        }
        fragment InputValue on __InputValue {
          name
          description
          type { ...TypeRef }
          defaultValue
        }
        """ + "\n" + TypeRefFragment;

    /// <summary>Same shape minus the directives block — some gateways reject directive
    /// introspection while allowing everything else.</summary>
    public const string NoDirectives = """
        query IntrospectionQuery {
          __schema {
            queryType { name }
            mutationType { name }
            subscriptionType { name }
            types { ...FullType }
          }
        }
        fragment FullType on __Type {
          kind
          name
          description
          fields(includeDeprecated: true) {
            name
            description
            args { ...InputValue }
            type { ...TypeRef }
            isDeprecated
            deprecationReason
          }
          inputFields { ...InputValue }
          interfaces { ...TypeRef }
          enumValues(includeDeprecated: true) {
            name
            description
            isDeprecated
          }
          possibleTypes { ...TypeRef }
        }
        fragment InputValue on __InputValue {
          name
          description
          type { ...TypeRef }
          defaultValue
        }
        """ + "\n" + TypeRefFragment;

    /// <summary>Last-resort abridged query (the pre-Phase-2 shape): types, fields, args,
    /// enum values only. Loses descriptions' depth, deprecation, inputs and interfaces
    /// but still powers completion + docs basics.</summary>
    public const string Minimal = """
        {
          __schema {
            queryType { name }
            mutationType { name }
            subscriptionType { name }
            types {
              kind
              name
              description
              fields(includeDeprecated: false) {
                name
                description
                type { kind name ofType { kind name ofType { kind name ofType { kind name } } } }
                args { name type { kind name ofType { kind name ofType { kind name } } } }
              }
              enumValues(includeDeprecated: false) { name }
            }
          }
        }
        """;

    /// <summary>Most-capable-first fallback chain.</summary>
    public static readonly IReadOnlyList<string> Chain = new[] { Full, NoDirectives, Minimal };
}
