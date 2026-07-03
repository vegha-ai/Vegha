using System.Net.Http;
using Avalonia.Headless.XUnit;
using Vegha.App.ViewModels;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>Headless tests for the request-editor tab indicators (the "has data" dots).
/// Each <c>*HasData</c> property on <see cref="RequestEditorViewModel"/> drives the dot's
/// IsVisible binding on its respective sub-tab header. These tests poke the VM directly
/// (no XAML inflation needed) — the same value the binding reads is the one we assert.</summary>
public class RequestEditorTabIndicatorTests
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

    [AvaloniaFact]
    public void BodyHasData_FlipsWithBodyType()
    {
        var vm = CreateVm();
        vm.BodyHasData.Should().BeFalse("default BodyType is 'none'");

        vm.BodyType = "json";
        vm.BodyHasData.Should().BeTrue();

        vm.BodyType = "none";
        vm.BodyHasData.Should().BeFalse();
    }

    [AvaloniaFact]
    public void AuthHasData_FlipsWithAuthType()
    {
        var vm = CreateVm();
        vm.AuthHasData.Should().BeFalse("default AuthType is 'none'");

        vm.AuthType = "bearer";
        vm.AuthHasData.Should().BeTrue();

        // "inherit" is treated as no-data too — the dot only lights up for a concrete choice.
        vm.AuthType = "inherit";
        vm.AuthHasData.Should().BeFalse();
    }

    [AvaloniaFact]
    public void ScriptHasData_FollowsScriptContent()
    {
        var vm = CreateVm();
        vm.PreRequestScriptHasData.Should().BeFalse();
        vm.PostResponseScriptHasData.Should().BeFalse();
        vm.TestsScriptHasData.Should().BeFalse();
        vm.DocsHasData.Should().BeFalse();

        vm.PreRequestScript = "bru.setVar('x', 1);";
        vm.PreRequestScriptHasData.Should().BeTrue();

        vm.PostResponseScript = "bru.setEnvVar('t', res.getBody().token);";
        vm.PostResponseScriptHasData.Should().BeTrue();

        vm.TestsScript = "test('ok', () => expect(res.getStatus()).to.equal(200));";
        vm.TestsScriptHasData.Should().BeTrue();

        vm.Docs = "# Notes";
        vm.DocsHasData.Should().BeTrue();

        // Clearing flips the dot back off.
        vm.PreRequestScript = string.Empty;
        vm.PreRequestScriptHasData.Should().BeFalse();
    }

    [AvaloniaFact]
    public void VarsTab_TracksBothPreAndPostCollections()
    {
        // The collections always carry a trailing blank ghost row (type-to-add UX), so the
        // badge count (VarsTotalCount) — not the raw collection Count — is what the tab
        // header binds to, and it must ignore ghost rows.
        var vm = CreateVm();
        vm.Variables.Should().ContainSingle(v => v.IsBlank, "a fresh editor seeds one ghost row");
        vm.PostResponseVariables.Should().ContainSingle(v => v.IsBlank);
        vm.VarsTotalCount.Should().Be(0, "ghost rows don't light the badge");

        vm.Variables.Add(new KvEntry("user", "alice"));
        vm.PostResponseVariables.Add(new KvEntry("token", "res.getBody().access_token"));

        vm.VarsTotalCount.Should().Be(2);

        // The UI path — typing into the ghost row — spawns the next ghost automatically.
        var ghost = vm.Variables[0];
        ghost.IsBlank.Should().BeTrue();
        vm.Variables.Move(0, vm.Variables.Count - 1);
        ghost.Name = "typed";
        vm.Variables[^1].IsBlank.Should().BeTrue("editing the tail ghost appends a fresh one");
        vm.VarsTotalCount.Should().Be(3);
    }
}
