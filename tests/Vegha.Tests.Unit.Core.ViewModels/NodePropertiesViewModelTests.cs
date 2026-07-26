using Vegha.App.ViewModels;
using Vegha.Core.Domain;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>Pins the collection/folder settings snapshot — including the two fields the old
/// dialog silently dropped (<c>post-response</c> script) or never had (presets).</summary>
public class NodePropertiesViewModelTests
{
    [Fact]
    public void BuildSnapshot_Collection_RoundTrips_PostResponseScript()
    {
        var source = new Collection
        {
            Name = "Api",
            PostResponseScript = "bru.setEnvVar('t', res.body.token);",
        };
        var vm = new NodePropertiesViewModel(NodePropertiesViewModel.Kind.Collection, source);

        // Seeded from the source (regression: this used to be empty on load).
        vm.PostResponseScript.Should().Contain("setEnvVar");

        var snap = vm.BuildSnapshot();
        snap.Collection!.PostResponseScript.Should().Contain("setEnvVar");
    }

    [Fact]
    public void BuildSnapshot_Folder_RoundTrips_PostResponseScript()
    {
        var source = new Folder { Name = "f", PostResponseScript = "console.log('post');" };
        var vm = new NodePropertiesViewModel(NodePropertiesViewModel.Kind.Folder, source);
        vm.PostResponseScript.Should().Contain("post");
        vm.BuildSnapshot().Folder!.PostResponseScript.Should().Contain("post");
    }

    [Fact]
    public void BuildSnapshot_Collection_RoundTrips_Presets()
    {
        var source = new Collection
        {
            Name = "Api",
            Presets = new RequestPresets { RequestType = "grpc", BaseUrl = "grpc://svc:50051" },
        };
        var vm = new NodePropertiesViewModel(NodePropertiesViewModel.Kind.Collection, source);
        vm.PresetRequestType.Should().Be("grpc");
        vm.PresetBaseUrl.Should().Be("grpc://svc:50051");

        var snap = vm.BuildSnapshot();
        snap.Collection!.Presets.Should().NotBeNull();
        snap.Collection.Presets!.RequestType.Should().Be("grpc");
        snap.Collection.Presets.BaseUrl.Should().Be("grpc://svc:50051");
    }

    [Fact]
    public void BuildSnapshot_Collection_EmptyPresets_ProduceNull()
    {
        var vm = new NodePropertiesViewModel(NodePropertiesViewModel.Kind.Collection,
            new Collection { Name = "Api" });
        // Defaults are http + empty url → IsEmpty → null in the snapshot (no presets block).
        vm.BuildSnapshot().Collection!.Presets.Should().BeNull();
    }

    // ---- Auth now uses the same section VM (and therefore the same on-disk shape) the
    // request editor uses, instead of a free-text "key: value" blob. ----

    [Fact]
    public void Collection_OAuth2_RoundTripsThroughTheSharedAuthSection()
    {
        var source = new Collection
        {
            Name = "Api",
            Auth = new AuthConfig
            {
                Type = AuthType.OAuth2,
                Parameters = new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["access_token_url"] = "https://idp.test/token",
                    ["client_id"] = "cid",
                    ["client_secret"] = "csec",
                    ["scope"] = "read write",
                },
            },
        };
        var vm = new NodePropertiesViewModel(NodePropertiesViewModel.Kind.Collection, source);

        // Loaded into the typed fields — not a text blob.
        vm.Auth.AuthType.Should().Be("oauth2");
        vm.Auth.OAuth2TokenUrl.Should().Be("https://idp.test/token");
        vm.Auth.OAuth2ClientId.Should().Be("cid");
        vm.Auth.OAuth2Scope.Should().Be("read write");

        var saved = vm.BuildSnapshot().Collection!.Auth;
        saved.Should().NotBeNull();
        saved!.Type.Should().Be(AuthType.OAuth2);
        saved.Parameters["access_token_url"].Should().Be("https://idp.test/token");
        saved.Parameters["client_secret"].Should().Be("csec");
    }

    [Fact]
    public void Folder_Digest_RoundTrips()
    {
        var source = new Folder
        {
            Name = "f",
            Auth = new AuthConfig
            {
                Type = AuthType.Digest,
                Parameters = new Dictionary<string, string>
                {
                    ["username"] = "alice",
                    ["password"] = "s3cret",
                },
            },
        };
        var vm = new NodePropertiesViewModel(NodePropertiesViewModel.Kind.Folder, source);

        vm.Auth.AuthType.Should().Be("digest");
        vm.Auth.DigestUsername.Should().Be("alice");

        var saved = vm.BuildSnapshot().Folder!.Auth;
        saved!.Type.Should().Be(AuthType.Digest);
        saved.Parameters["password"].Should().Be("s3cret");
    }

    [Fact]
    public void CollectionScope_DoesNotOfferInherit_ButFolderScopeDoes()
    {
        // A collection is the top of the chain — "inherit" has nothing to resolve against.
        var collection = new NodePropertiesViewModel(
            NodePropertiesViewModel.Kind.Collection, new Collection { Name = "Api" });
        collection.Auth.AvailableAuthTypes.Should().NotContain("inherit");
        collection.Auth.Scope.Should().Be(AuthScope.Collection);

        var folder = new NodePropertiesViewModel(
            NodePropertiesViewModel.Kind.Folder, new Folder { Name = "f" });
        folder.Auth.AvailableAuthTypes.Should().Contain("inherit");
        folder.Auth.Scope.Should().Be(AuthScope.Folder);

        // Every other type is offered at both levels — OAuth2 included.
        collection.Auth.AvailableAuthTypes.Should().Contain("oauth2");
        folder.Auth.AvailableAuthTypes.Should().Contain("oauth2");
    }

    [Fact]
    public void NodeVariables_AreInScopeForItsOwnAuthFields()
    {
        var source = new Collection
        {
            Name = "Api",
            Variables = new List<KvPair> { new("jwt", "abc123", true) },
        };
        var vm = new NodePropertiesViewModel(NodePropertiesViewModel.Kind.Collection, source);

        vm.Auth.ResolvedVariablesSnapshot.Should().ContainKey("jwt").WhoseValue.Should().Be("abc123");
    }
}
