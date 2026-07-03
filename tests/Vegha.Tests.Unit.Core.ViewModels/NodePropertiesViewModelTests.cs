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
}
