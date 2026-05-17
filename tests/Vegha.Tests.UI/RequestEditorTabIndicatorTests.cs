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
        var vm = CreateVm();
        vm.Variables.Count.Should().Be(0);
        vm.PostResponseVariables.Count.Should().Be(0);

        vm.Variables.Add(new KvEntry("user", "alice"));
        vm.PostResponseVariables.Add(new KvEntry("token", "res.getBody().access_token"));

        vm.Variables.Count.Should().Be(1);
        vm.PostResponseVariables.Count.Should().Be(1);
    }
}
