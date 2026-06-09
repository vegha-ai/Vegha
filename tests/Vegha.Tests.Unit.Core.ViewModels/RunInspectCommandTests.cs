using Vegha.App.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

public class RunInspectCommandTests
{
    private static RequestEditorViewModel NewEditor() =>
        new(
            executor: new Vegha.Core.Requests.HttpExecutor(new System.Net.Http.HttpClient()),
            oauth2: new Vegha.Core.Requests.OAuth2TokenAcquirer(new System.Net.Http.HttpClient()),
            scriptHost: new Vegha.Core.Scripting.JintHost(),
            logger: NullLogger<RequestEditorViewModel>.Instance);

    [Fact]
    public void RunPreRequest_populates_console_and_runtime_vars()
    {
        var vm = NewEditor();
        vm.PreRequestScript = "bru.setVar('t', '1'); console.log('hello');";

        vm.RunPreRequestInspectCommand.Execute(null);

        vm.IsInspectPanelOpen.Should().BeTrue();
        vm.InspectMessage.Should().BeNull();
        vm.InspectConsole.Should().ContainSingle(c => c.Text == "hello");
        vm.InspectVariables.Should().Contain(r => r.Scope == "Runtime" && r.Name == "t" && r.Value == "1");
    }

    [Fact]
    public void RunPreRequest_setEnvVar_shows_as_preview_and_does_not_persist()
    {
        var vm = NewEditor();
        var raised = false;
        vm.EnvironmentVariablesMutated += (_, _) => raised = true;
        vm.PreRequestScript = "bru.setEnvVar('token', 'abc');";

        vm.RunPreRequestInspectCommand.Execute(null);

        vm.InspectVariables.Should().Contain(r => r.Scope == "Env (preview)" && r.Name == "token" && r.Value == "abc");
        raised.Should().BeFalse("inspect runs in isolation and must not persist env mutations");
    }

    [Fact]
    public void RunPostResponse_without_response_shows_send_first_hint()
    {
        var vm = NewEditor();
        vm.PostResponseScript = "bru.setEnvVar('x', res.getStatus());";

        vm.RunPostResponseInspectCommand.Execute(null);

        vm.IsInspectPanelOpen.Should().BeTrue();
        vm.InspectMessage.Should().Contain("Send the request first");
        vm.InspectConsole.Should().BeEmpty();
    }

    [Fact]
    public void RunTests_with_last_response_populates_test_results()
    {
        var vm = NewEditor();
        // Simulate a captured response.
        vm.HasResponse = true;
        vm.ResponseStatusCode = 200;
        vm.ResponseStatusText = "OK";
        vm.ResponseBody = "hello";
        vm.TestsScript = """
            test('status is 200', function () { expect(res.getStatus()).to.equal(200); });
            test('intentional fail', function () { expect(res.getStatus()).to.equal(500); });
            """;

        vm.RunTestsInspectCommand.Execute(null);

        vm.IsInspectPanelOpen.Should().BeTrue();
        vm.InspectTests.Should().HaveCount(2);
        vm.InspectTests.Count(t => t.Passed).Should().Be(1);
        vm.InspectTests.Count(t => !t.Passed).Should().Be(1);
    }
}
