using System;
using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace Vegha.App.Controls.Workspace.Highlighting;

/// <summary>
/// Lazily registers the embedded GraphQL <c>.xshd</c> with AvaloniaEdit's
/// <see cref="HighlightingManager"/>. Called from the editor the first time a
/// <c>SyntaxHighlightingName="GraphQL"</c> is requested — nothing loads at startup
/// (cold-start budget) and non-GraphQL editors never touch this type.
/// </summary>
internal static class GraphQLHighlighting
{
    private static bool _registered;

    /// <summary>Idempotent; UI-thread only (matches every ApplySyntaxHighlighting call site).</summary>
    public static void EnsureRegistered()
    {
        if (_registered) return;
        using var stream = typeof(GraphQLHighlighting).Assembly.GetManifestResourceStream(
            "Vegha.App.Controls.Workspace.Highlighting.GraphQL.xshd");
        if (stream is null) return; // resource missing — editor stays unhighlighted
        using var reader = XmlReader.Create(stream);
        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        HighlightingManager.Instance.RegisterHighlighting(
            "GraphQL", new[] { ".graphql", ".gql" }, definition);
        _registered = true;
    }
}
