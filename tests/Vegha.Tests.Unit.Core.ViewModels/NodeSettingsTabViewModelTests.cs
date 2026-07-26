using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Tabs;
using Vegha.Core.Domain;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>One settings tab type now serves collections and folders — the folder Properties
/// dialog (a cut-down surface with a free-text auth box) is gone. These pin the parts that
/// legitimately differ between the two scopes; everything else is shared by construction.</summary>
public class NodeSettingsTabViewModelTests
{
    private static CollectionsViewModel Collections()
    {
        var http = new HttpClient();
        var editor = new RequestEditorViewModel(
            new HttpExecutor(http),
            new OAuth2TokenAcquirer(http),
            new JintHost(),
            NullLogger<RequestEditorViewModel>.Instance);
        return new CollectionsViewModel(editor, NullLogger<CollectionsViewModel>.Instance);
    }

    private static NodeSettingsTabViewModel CollectionTab(Collection? collection = null) =>
        new(Collections(), new CollectionRootViewModel
        {
            Name = "Api",
            SourcePath = @"C:\work\Api",
            Collection = collection ?? new Collection { Name = "Api" },
        });

    private static NodeSettingsTabViewModel FolderTab(Folder? folder = null) =>
        new(Collections(), new CollectionFolderViewModel
        {
            Name = "inner",
            Path = @"C:\work\Api\inner",
            Folder = folder ?? new Folder { Name = "inner" },
        });

    [Fact]
    public void CollectionAndFolderTabsGetDistinctIds()
    {
        CollectionTab().Id.Should().StartWith("colsettings:");
        FolderTab().Id.Should().StartWith("foldersettings:");
    }

    [Fact]
    public void FolderTabIsScopedToItsOwningCollectionPath()
    {
        var collections = Collections();
        var tab = new NodeSettingsTabViewModel(collections, new CollectionFolderViewModel
        {
            Name = "inner",
            Path = @"C:\work\Api\inner",
            Folder = new Folder { Name = "inner" },
        });

        // No collection is open in this fixture, so the scope resolves to null rather than
        // guessing — the tab still opens, it just isn't filtered under a collection.
        tab.CollectionPath.Should().BeNull();
        tab.NodePath.Should().Be(@"C:\work\Api\inner");
    }

    [Fact]
    public void PresetsAndEnvironmentsAreCollectionOnly()
    {
        CollectionTab().IsCollection.Should().BeTrue();
        FolderTab().IsCollection.Should().BeFalse();
    }

    [Fact]
    public void RequestCountCoversTheWholeSubtree()
    {
        var folder = new Folder
        {
            Name = "inner",
            Requests = new List<RequestItem> { new() { Name = "a" }, new() { Name = "b" } },
            Folders = new List<Folder>
            {
                new() { Name = "deeper", Requests = new List<RequestItem> { new() { Name = "c" } } },
            },
        };

        FolderTab(folder).RequestCount.Should().Be(3);
    }

    [Fact]
    public void BothScopesEditAuthThroughTheSharedSection()
    {
        var collectionTab = CollectionTab(new Collection
        {
            Name = "Api",
            Auth = new AuthConfig
            {
                Type = AuthType.OAuth2,
                Parameters = new Dictionary<string, string> { ["client_id"] = "cid" },
            },
        });
        var folderTab = FolderTab(new Folder
        {
            Name = "inner",
            Auth = new AuthConfig
            {
                Type = AuthType.AwsV4,
                Parameters = new Dictionary<string, string> { ["region"] = "eu-west-1" },
            },
        });

        collectionTab.Props.Auth.AuthType.Should().Be("oauth2");
        collectionTab.Props.Auth.OAuth2ClientId.Should().Be("cid");
        collectionTab.Props.Auth.Scope.Should().Be(AuthScope.Collection);

        folderTab.Props.Auth.AuthType.Should().Be("awsv4");
        folderTab.Props.Auth.AwsRegion.Should().Be("eu-west-1");
        folderTab.Props.Auth.Scope.Should().Be(AuthScope.Folder);
    }

    [Fact]
    public void ScopeWordingFollowsTheNodeKind()
    {
        CollectionTab().PreRequestHint.Should().Contain("collection");
        FolderTab().PreRequestHint.Should().Contain("folder");
        CollectionTab().RequestScopeLabel.Should().Contain("collection");
        FolderTab().RequestScopeLabel.Should().Contain("folder");
    }
}
