using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Tabs;
using Vegha.Core.Domain;
using Vegha.Core.Persistence;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>End-to-end proof that a tab's full editor state — unsaved/dirty edits and untitled
/// scratch drafts — survives a persist→restore cycle through <see cref="TabStateStore"/>. This is
/// the durability guarantee behind "no save/discard prompt on close" and "dirty survives a
/// workspace switch / app restart". Mirrors the host's MainWindow.PersistTabs/RestoreOpenTabsAsync
/// mapping.</summary>
public class TabStatePersistenceRoundTripTests
{
    private static OpenTabsViewModel NewTabs() => new(
        () => new RequestEditorViewModel(
            new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
            new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
            new Vegha.Core.Scripting.JintHost(),
            NullLogger<RequestEditorViewModel>.Instance),
        NullLogger<OpenTabsViewModel>.Instance);

    private static string TempDb() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vegha-rt-" + Guid.NewGuid().ToString("N") + ".db");

    private static void Persist(OpenTabsViewModel tabs, TabStateStore store) =>
        store.SaveAll(tabs.FullSnapshot().Select(s => new TabStateRow(
            s.Id, s.WorkspaceId, s.CollectionPath, s.SourcePath, s.Name, s.Kind.ToString(),
            s.OrderIndex, s.IsActive, s.IsDirty, s.IsScratch, s.StateBlob)).ToList());

    private static void Restore(OpenTabsViewModel tabs, TabStateStore store)
    {
        foreach (var r in store.LoadAll())
        {
            if (!string.IsNullOrEmpty(r.StateBlob))
            {
                var item = System.Text.Json.JsonSerializer.Deserialize<RequestItem>(r.StateBlob);
                if (item is not null)
                    tabs.RestoreHttpTab(item, r.Id, r.SourcePath, r.CollectionPath,
                        r.WorkspaceId, r.IsScratch, r.IsDirty, r.Name);
            }
        }
    }

    [Fact]
    public void DirtyScratchDraft_SurvivesPersistAndRestore()
    {
        var db = TempDb();
        try
        {
            // Author: a "+" scratch draft with unsaved edits.
            var tabs = NewTabs();
            tabs.ActiveWorkspaceId = "/ws/a";
            var scratch = tabs.CreateScratch("/ws/a");
            scratch.Editor.Url = "https://typed-but-never-saved/";   // setting Url marks the editor dirty
            scratch.Editor.BodyType = "json";
            scratch.Editor.BodyContent = "{\"wip\":true}";
            scratch.IsDirty.Should().BeTrue();

            var store = new TabStateStore(db);
            Persist(tabs, store);

            // Restore into a fresh session (new app launch / workspace return).
            var restoredTabs = NewTabs();
            restoredTabs.ActiveWorkspaceId = "/ws/a";
            Restore(restoredTabs, store);

            var t = restoredTabs.Tabs.OfType<HttpRequestTabViewModel>().Single();
            t.Name.Should().Be("Untitled");
            t.IsScratch.Should().BeTrue();
            t.WorkspaceId.Should().Be("/ws/a");
            t.IsDirty.Should().BeTrue("unsaved edits are preserved, so the tab is still dirty");
            t.Editor.Url.Should().Be("https://typed-but-never-saved/");
            t.Editor.BodyContent.Should().Be("{\"wip\":true}");
            restoredTabs.VisibleTabs.Should().Contain(t, "the scratch tab belongs to the active workspace");
        }
        finally { SqliteConnection.ClearAllPools(); try { System.IO.File.Delete(db); } catch { } }
    }

    [Fact]
    public void RestoredScratch_HiddenUnderOtherWorkspace()
    {
        var db = TempDb();
        try
        {
            var tabs = NewTabs();
            tabs.ActiveWorkspaceId = "/ws/a";
            tabs.CreateScratch("/ws/a").Editor.Url = "https://a/";
            var store = new TabStateStore(db);
            Persist(tabs, store);

            // Restore while a DIFFERENT workspace is active → the draft must not show.
            var restoredTabs = NewTabs();
            restoredTabs.ActiveWorkspaceId = "/ws/b";
            Restore(restoredTabs, store);

            restoredTabs.Tabs.Should().ContainSingle("the tab is still loaded in memory");
            restoredTabs.VisibleTabs.Should().BeEmpty("but it belongs to /ws/a, not the active /ws/b");
        }
        finally { SqliteConnection.ClearAllPools(); try { System.IO.File.Delete(db); } catch { } }
    }
}
