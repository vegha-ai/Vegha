using FluentAssertions;
using Vegha.App.ViewModels;
using Vegha.Core.Bru.Parser;
using Vegha.Core.Domain;
using Vegha.Core.Importers;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>
/// Regression coverage for New Request → file scaffold → load. A dialog-created GraphQL
/// request previously scaffolded a bare `get { url }` file, which loaded back as a plain
/// REST GET — no query editor, no schema pane.
/// </summary>
public class NewRequestScaffoldTests
{
    private static RequestItem Load(string bru)
    {
        var doc = BruParser.Parse(bru);
        return BruToRequestConverter.Convert(doc);
    }

    [Fact]
    public void GraphQLScaffold_LoadsAsGraphQL_PostWithGraphQLBody()
    {
        var bru = CollectionsViewModel.BuildMinimalBru(
            NewRequestKind.GraphQL, "my-gql", method: "GET" /* dialog default is ignored */,
            url: "https://api.acme.io/graphql");

        var item = Load(bru);

        item.Kind.Should().Be(RequestKind.GraphQL);
        item.Method.Should().Be("POST", "GraphQL requests always POST");
        item.Url.Should().Be("https://api.acme.io/graphql");
        item.Body.Mode.Should().Be(BodyMode.GraphQL);
        item.Body.GraphQLQuery.Should().Contain("__typename",
            "the starter query is valid against any schema and sendable immediately");
        item.MetaType.Should().Be("graphql");
    }

    [Fact]
    public void GraphQLScaffold_EmptyUrl_FallsBackToPlaceholder()
    {
        var item = Load(CollectionsViewModel.BuildMinimalBru(
            NewRequestKind.GraphQL, "gql", method: "", url: ""));
        item.Url.Should().NotBeNullOrWhiteSpace();
        item.Body.Mode.Should().Be(BodyMode.GraphQL);
    }

    [Fact]
    public void HttpScaffold_Unchanged_LoadsAsHttpWithChosenMethod()
    {
        var item = Load(CollectionsViewModel.BuildMinimalBru(
            NewRequestKind.Http, "plain", method: "PUT", url: "https://api.acme.io/x"));
        item.Kind.Should().Be(RequestKind.Http);
        item.Method.Should().Be("PUT");
        item.Body.Mode.Should().Be(BodyMode.None);
    }

    [Fact]
    public void DeclaredGraphQLBody_WithoutTextBlock_StillResolvesGraphQLKind()
    {
        // The emitter skips empty text blocks, so a saved-with-empty-query request has
        // `body: graphql` in the verb block but no body:graphql section.
        const string bru = """
            meta {
              name: empty-query
              type: graphql
              seq: 1
            }

            post {
              url: https://api.acme.io/graphql
              body: graphql
              auth: none
            }
            """;
        var item = Load(bru);
        item.Kind.Should().Be(RequestKind.GraphQL);
        item.Body.Mode.Should().Be(BodyMode.GraphQL);
    }
}
