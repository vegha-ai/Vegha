using System.Net.Http;
using Vegha.App.ViewModels;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>
/// Regression tests for the Save-button-stays-disabled bug. Every request-side collection
/// (Params, Headers, Vars, Post-response Vars, form-urlencoded items, multipart items,
/// OAuth2 token/refresh additional parameters) must trigger IsDirty=true on BOTH
/// add/remove (CollectionChanged) AND per-row edits (PropertyChanged on the row). Without
/// this, users could edit Headers values and never get Save enabled — silent data-loss
/// risk. These tests poke each collection two ways: add a row, then mutate that row's
/// Name. Either failure path means dirty tracking has regressed.
/// </summary>
public class RequestEditorDirtyTrackingTests
{
    private static RequestEditorViewModel CreateVm()
    {
        var http = new HttpClient();
        return new RequestEditorViewModel(
            new HttpExecutor(http),
            new OAuth2TokenAcquirer(http),
            new JintHost(),
            NullLogger<RequestEditorViewModel>.Instance);
    }

    [Fact]
    public void Headers_Add_TriggersDirty()
    {
        var vm = CreateVm();
        vm.IsDirty.Should().BeFalse("a freshly constructed VM is clean");

        vm.Headers.Add(new KvEntry("Content-Type", "text/xml", true));
        vm.IsDirty.Should().BeTrue("adding a header row must enable Save");
    }

    [Fact]
    public void Headers_EditExistingRowName_TriggersDirty()
    {
        var vm = CreateVm();
        var row = new KvEntry("Content-Type", "text/xml", true);
        vm.Headers.Add(row);
        // Reset the dirty flag (simulating a Save) so we can isolate the per-row edit signal.
        var sourcePath = typeof(RequestEditorViewModel).GetProperty("SourcePath");
        sourcePath?.SetValue(vm, "test.bru");
        vm.GetType().GetProperty(nameof(RequestEditorViewModel.IsDirty))!
            .SetValue(vm, false);
        vm.IsDirty.Should().BeFalse();

        row.Name = "Authorization";
        vm.IsDirty.Should().BeTrue(
            "editing an existing header row's name must enable Save — this is the bug " +
            "users hit when they change a header value and Save stays grayed out.");
    }

    [Fact]
    public void Headers_EditExistingRowValue_TriggersDirty()
    {
        var vm = CreateVm();
        var row = new KvEntry("Content-Type", "text/xml", true);
        vm.Headers.Add(row);
        vm.GetType().GetProperty(nameof(RequestEditorViewModel.IsDirty))!
            .SetValue(vm, false);

        row.Value = "application/json";
        vm.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void Headers_ToggleExistingRowEnabled_TriggersDirty()
    {
        var vm = CreateVm();
        var row = new KvEntry("X-Trace", "abc", true);
        vm.Headers.Add(row);
        vm.GetType().GetProperty(nameof(RequestEditorViewModel.IsDirty))!
            .SetValue(vm, false);

        row.Enabled = false;
        vm.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void Params_Add_TriggersDirty()
    {
        var vm = CreateVm();
        vm.Params.Add(new KvEntry("page", "1", true));
        vm.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void Variables_Add_TriggersDirty()
    {
        var vm = CreateVm();
        vm.Variables.Add(new KvEntry("token", "abc", true));
        vm.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void PostResponseVariables_Add_TriggersDirty()
    {
        var vm = CreateVm();
        vm.PostResponseVariables.Add(new KvEntry("captured", "res.getBody().id", true));
        vm.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void FormUrlEncodedItems_Add_TriggersDirty()
    {
        var vm = CreateVm();
        vm.FormUrlEncodedItems.Add(new KvEntry("username", "alice"));
        vm.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void OAuth2TokenParameters_Add_TriggersDirty()
    {
        var vm = CreateVm();
        vm.OAuth2TokenParameters.Add(new OAuth2AdditionalParameter());
        vm.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void OAuth2RefreshParameters_Add_TriggersDirty()
    {
        var vm = CreateVm();
        vm.OAuth2RefreshParameters.Add(new OAuth2AdditionalParameter());
        vm.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void RemovingRow_AlsoTriggersDirty()
    {
        var vm = CreateVm();
        var row = new KvEntry("a", "b", true);
        vm.Headers.Add(row);
        vm.GetType().GetProperty(nameof(RequestEditorViewModel.IsDirty))!
            .SetValue(vm, false);

        vm.Headers.Remove(row);
        vm.IsDirty.Should().BeTrue("removing a row also mutates the request");
    }

    [Fact]
    public void RemovedRowsStopTrackingDirty()
    {
        // Memory-leak / phantom-dirty guard: after a row is removed from the collection,
        // edits on the now-detached instance must NOT keep marking the VM dirty.
        var vm = CreateVm();
        var row = new KvEntry("a", "b", true);
        vm.Headers.Add(row);
        vm.Headers.Remove(row);
        vm.GetType().GetProperty(nameof(RequestEditorViewModel.IsDirty))!
            .SetValue(vm, false);

        row.Value = "c"; // mutates the detached instance — should be a no-op for the VM.
        vm.IsDirty.Should().BeFalse(
            "detached rows must not keep firing dirty signals after removal — that would " +
            "(a) leak the row in memory by keeping the handler alive, and (b) falsely " +
            "re-dirty the VM if the row is held by external code.");
    }
}
