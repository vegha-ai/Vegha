using Vegha.Core.Domain;
using Vegha.Core.Requests;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>Covers the workspace inheritance layer added underneath the collection in
/// <see cref="RequestComposition"/>: workspace vars are overridable by collection/folder/request,
/// workspace scripts concatenate first in the merge order.</summary>
public class RequestCompositionWorkspaceTests
{
    [Fact]
    public void WorkspaceVariables_AreApplied_AndOverridableByCollection()
    {
        var workspace = new RequestComposition.WorkspaceContext(
            Variables: new List<KvPair>
            {
                new("baseUrl", "https://workspace.test"),
                new("trace", "from-workspace"),
            },
            PreRequestScript: null,
            PostResponseScript: null,
            TestsScript: null);

        var collection = new Collection
        {
            Name = "C",
            Variables = new List<KvPair> { new("trace", "from-collection") }
        };
        var request = new RequestItem { Name = "r", Url = "https://x.test" };

        var composed = RequestComposition.Compose(collection, Array.Empty<Folder>(), request, workspace);

        composed.Vars["baseUrl"].Should().Be("https://workspace.test"); // workspace-only var wins
        composed.Vars["trace"].Should().Be("from-collection");          // collection overrides workspace
    }

    [Fact]
    public void WorkspaceScripts_ConcatenateFirstInOrder()
    {
        var workspace = new RequestComposition.WorkspaceContext(
            Variables: null,
            PreRequestScript: "// workspace pre",
            PostResponseScript: null,
            TestsScript: "// workspace tests");

        var collection = new Collection { Name = "C", PreRequestScript = "// coll pre", TestsScript = "// coll tests" };
        var folder = new Folder { Name = "F", PreRequestScript = "// folder pre" };
        var request = new RequestItem { Name = "r", Url = "x.test", PreRequestScript = "// req pre", Tests = "// req tests" };

        var composed = RequestComposition.Compose(collection, new[] { folder }, request, workspace);

        composed.PreRequestScript.Should().Contain("// workspace pre");
        // Workspace appears BEFORE collection in concatenation order.
        composed.PreRequestScript!.IndexOf("// workspace pre").Should()
            .BeLessThan(composed.PreRequestScript.IndexOf("// coll pre"));
        composed.PreRequestScript.IndexOf("// coll pre").Should()
            .BeLessThan(composed.PreRequestScript.IndexOf("// folder pre"));
        composed.PreRequestScript.IndexOf("// folder pre").Should()
            .BeLessThan(composed.PreRequestScript.IndexOf("// req pre"));

        composed.TestsScript.Should().Contain("// workspace tests");
        composed.TestsScript!.IndexOf("// workspace tests").Should()
            .BeLessThan(composed.TestsScript.IndexOf("// coll tests"));
    }

    [Fact]
    public void EmptyWorkspaceContext_PreservesLegacyComposition()
    {
        // The no-workspace overload should produce identical results to the explicit-empty form.
        var collection = new Collection
        {
            Name = "C",
            Headers = new List<KvPair> { new("X-Trace", "c") },
            Variables = new List<KvPair> { new("baseUrl", "https://c.test") },
        };
        var request = new RequestItem { Name = "r", Url = "x.test" };

        var withoutWs = RequestComposition.Compose(collection, Array.Empty<Folder>(), request);
        var withEmptyWs = RequestComposition.Compose(collection, Array.Empty<Folder>(), request,
            RequestComposition.WorkspaceContext.Empty);

        withoutWs.Vars.Should().BeEquivalentTo(withEmptyWs.Vars);
        withoutWs.Headers.Should().BeEquivalentTo(withEmptyWs.Headers);
        withoutWs.PreRequestScript.Should().Be(withEmptyWs.PreRequestScript);
    }
}
