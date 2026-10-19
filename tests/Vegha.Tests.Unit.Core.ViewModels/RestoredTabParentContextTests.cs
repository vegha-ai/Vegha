using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Tabs;
using Vegha.Core.Domain;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>Tabs restored at startup open before their collections load and only know their
/// file. Once the collection is known they must get its context — otherwise the Chain tab offers
/// no requests ("No other requests in this collection") and inherited settings are missing.</summary>
public class RestoredTabParentContextTests
{
    private static RequestEditorViewModel NewEditor() => new(
        executor: new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
        oauth2: new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
        scriptHost: new Vegha.Core.Scripting.JintHost(),
        logger: NullLogger<RequestEditorViewModel>.Instance);

    [Fact]
    public void A_tab_opened_with_only_its_file_gets_the_collection_once_it_resolves()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vegha-restore-" + Guid.NewGuid().ToString("N"));
        var accounts = new RequestItem { Name = "Get Account List", SourcePath = Path.Combine(dir, "auth", "accounts.bru") };
        var login = new RequestItem { Name = "Login", Method = "POST", SourcePath = Path.Combine(dir, "auth", "login.bru") };
        var collection = new Collection { Name = "c", Folders = { new Folder { Name = "auth", Requests = { accounts, login } } } };

        var tabs = new OpenTabsViewModel(NewEditor, NullLogger<OpenTabsViewModel>.Instance);
        var tab = (HttpRequestTabViewModel)tabs.OpenOrActivate(accounts, accounts.SourcePath);
        tab.Editor.ChainEditor.Candidates.Should().BeEmpty("nothing knows the collection yet");

        // The collections tree finishes loading.
        tabs.ParentContextResolver = path =>
            string.Equals(path, accounts.SourcePath, StringComparison.OrdinalIgnoreCase)
                ? (collection, collection.Folders.ToList())
                : null;
        tabs.RefreshParentContexts();

        tab.Editor.ChainEditor.Candidates.Select(c => c.Name).Should().Equal("Login");

        // Re-opening by file only (as session restore does) keeps the context.
        tabs.OpenOrActivate(accounts, accounts.SourcePath);
        tab.Editor.ChainEditor.Candidates.Should().ContainSingle();
    }
}
