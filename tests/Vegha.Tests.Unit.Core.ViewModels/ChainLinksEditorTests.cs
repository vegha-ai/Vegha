using FluentAssertions;
using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Scriptless;
using Vegha.Core.Domain;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>The linked-requests editor: requests are picked (not typed), conditions are built
/// from pickers, and what gets saved is still the plain <c>path: condition</c> pair.</summary>
public class ChainLinksEditorTests
{
    private static Collection Shop() => new()
    {
        Name = "Shop",
        Requests = { new RequestItem { Name = "Health", Method = "GET" } },
        Folders =
        {
            new Folder
            {
                Name = "auth",
                Requests =
                {
                    new RequestItem
                    {
                        Name = "Login", Method = "POST",
                        PostResponseVars = { new KvPair("token", "res.body.token"), new KvPair("old", "res.body.old", enabled: false) },
                        PostResponseEnvVars = { new KvPair("refreshToken", "res.body.refresh") },
                    },
                    new RequestItem { Name = "Get Profile", Method = "GET", PostResponseVars = { new KvPair("role", "res.body.role") } },
                },
            },
        },
    };

    private static NodePropertiesViewModel Folder(params KvPair[] links)
    {
        var vm = new NodePropertiesViewModel(NodePropertiesViewModel.Kind.Folder, new Folder { Name = "orders", ChainLinks = links.ToList() });
        vm.SetLinkableCollection(Shop());
        return vm;
    }

    [Fact]
    public void Candidates_carry_method_folder_and_enabled_captures()
    {
        var all = ChainCandidate.Of(Shop(), null);

        all.Select(c => c.Path).Should().Equal("Health.bru", "auth/Login.bru", "auth/Get Profile.bru");
        var login = all[1];
        login.MethodLabel.Should().Be("POST");
        login.Folder.Should().Be("auth");
        login.Captures.Should().Equal("token", "refreshToken");
    }

    [Theory]
    [InlineData("login", true)]
    [InlineData("post auth", true)]
    [InlineData("AUTH LOG", true)]
    [InlineData("get login", false)]
    [InlineData("", true)]
    public void The_picker_filter_matches_every_term(string query, bool matches) =>
        ChainCandidate.Matches(query, ChainCandidate.Of(Shop(), null)[1]).Should().Be(matches);

    [Fact]
    public void Choosing_a_request_in_the_ghost_row_adds_a_link_and_hides_it_from_the_others()
    {
        var vm = Folder();
        var editor = vm.ChainEditor;
        var ghost = vm.ChainLinks.Single();
        ghost.AvailableCandidates.Should().HaveCount(3);

        ghost.SelectedCandidate = ghost.AvailableCandidates.Single(c => c.Name == "Login");

        vm.ChainLinks.Should().HaveCount(2, "a fresh ghost row follows");
        ghost.StepLabel.Should().Be("Request 1");
        ghost.CapturesText.Should().Be("{{token}}  {{refreshToken}}");
        ghost.ShowEmptySuggestion.Should().BeTrue();
        vm.ChainLinks[1].AvailableCandidates.Select(c => c.Name).Should().Equal("Health", "Get Profile");
        editor.Resolve("Login").Should().BeSameAs(ghost.Target, "a bare unique name resolves like the engine");

        vm.BuildSnapshot().Folder!.ChainLinks.Should().Equal(new KvPair("auth/Login.bru", "always"));
    }

    [Fact]
    public void Conditions_are_built_from_pickers()
    {
        var vm = Folder(new KvPair("auth/Login.bru", "always"));
        var row = vm.ChainLinks[0];

        row.RunWhenEmptyCommand.Execute(null);
        row.ToKvPair().Value.Should().Be("when-empty token");

        row.ModeOption = ScriptlessOptions.ChainModeFor(ChainRunMode.WhenStale);
        row.StaleAmount.Should().Be("10");
        row.StaleAmount = "30";
        row.StaleUnit = ChainLinkRow.AgeUnits.Single(u => u.Suffix == "h");
        row.ToKvPair().Value.Should().Be("when-stale 30h");

        row.ModeOption = ScriptlessOptions.ChainModeFor(ChainRunMode.When);
        row.ConditionVariable.Should().Be("token", "the linked request's capture is the likely subject");
        row.ConditionOperator = ChainLinkRow.ConditionOperators.Single(o => o.Token == "^=");
        row.ConditionValue = "eyJ";
        row.ToKvPair().Value.Should().Be("when {{token}} ^= eyJ");
        row.StoredText.Should().Be("auth/Login.bru: when {{token}} ^= eyJ");

        var parsed = ChainLinkRow.FromStorage(new KvPair("x", "when {{role}} >= 3"));
        (parsed.ConditionVariable, parsed.ConditionOperator.Token, parsed.ConditionValue).Should().Be(("role", ">=", "3"));
    }

    [Fact]
    public void A_variable_condition_follows_the_newly_chosen_request()
    {
        var vm = Folder(new KvPair("auth/Login.bru", "when-empty token"));
        var row = vm.ChainLinks[0];

        row.SelectedCandidate = row.AvailableCandidates.Single(c => c.Name == "Get Profile");

        row.ToKvPair().Should().Be(new KvPair("auth/Get Profile.bru", "when-empty role"));
    }

    [Fact]
    public void Rows_reorder_and_the_ghost_row_stays_last()
    {
        var vm = Folder(new KvPair("Health.bru", "always"), new KvPair("auth/Login.bru", "always"));
        var editor = vm.ChainEditor;
        var login = vm.ChainLinks[1];
        login.IsLast.Should().BeTrue();

        editor.MoveUpCommand.Execute(login);
        vm.ChainLinks.IndexOf(login).Should().Be(0);
        login.StepLabel.Should().Be("Request 1");
        login.IsFirst.Should().BeTrue();

        editor.MoveDownCommand.Execute(vm.ChainLinks[1]);
        vm.ChainLinks[^1].IsBlank.Should().BeTrue();

        editor.RemoveCommand.Execute(login);
        vm.BuildSnapshot().Folder!.ChainLinks.Should().Equal(new KvPair("Health.bru", "always"));
    }

    [Fact]
    public void An_unknown_path_is_flagged()
    {
        var row = Folder(new KvPair("auth/Gone.bru", "always")).ChainLinks[0];
        row.IsUnresolved.Should().BeTrue();
        row.Target.Should().BeNull();
    }
}
