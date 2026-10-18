using FluentAssertions;
using Vegha.Core.Domain;
using Vegha.Core.Engine;
using Xunit;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.Tests.Unit.Core.Engine;

public class EnvironmentMutationsTests
{
    private static DomainEnv Env(string name, params (string Name, string Value)[] vars) => new()
    {
        Name = name,
        Variables = vars.Select(v => new KvPair(v.Name, v.Value)).ToList(),
    };

    private static readonly IReadOnlyCollection<string> None = Array.Empty<string>();

    private static Dictionary<string, string> Vars(params (string K, string V)[] kv) =>
        kv.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public void Update_targets_the_scope_where_the_variable_already_lives()
    {
        var coll = Env("dev", ("host", "a"));
        var global = Env("global", ("token", "old"));

        var changed = EnvironmentMutations.Apply(coll, global, Vars(("token", "new"), ("host", "b")), None);

        changed.Should().BeTrue();
        global.Variables.Single().Value.Should().Be("new");
        coll.Variables.Single().Value.Should().Be("b");
    }

    [Fact]
    public void New_variable_lands_in_collection_env_or_global_when_none_is_active()
    {
        var coll = Env("dev");
        var global = Env("global");
        EnvironmentMutations.Apply(coll, global, Vars(("fresh", "1")), None);
        coll.Variables.Should().ContainSingle(v => v.Name == "fresh");
        global.Variables.Should().BeEmpty();

        var onlyGlobal = Env("global");
        EnvironmentMutations.Apply(null, onlyGlobal, Vars(("fresh", "1")), None);
        onlyGlobal.Variables.Should().ContainSingle(v => v.Name == "fresh");
    }

    [Fact]
    public void Global_target_writes_global_even_when_collection_defines_the_name()
    {
        var coll = Env("dev", ("token", "coll"));
        var global = Env("global");

        EnvironmentMutations.Apply(coll, global, Vars(("token", "g")), None, targetGlobal: true);

        coll.Variables.Single().Value.Should().Be("coll");
        global.Variables.Should().ContainSingle(v => v.Name == "token" && v.Value == "g");
    }

    [Fact]
    public void Removal_clears_both_scopes_and_update_preserves_enabled_flag()
    {
        var coll = new DomainEnv
        {
            Name = "dev",
            Variables = new List<KvPair> { new("off", "x", enabled: false), new("gone", "1") },
        };
        var global = Env("global", ("gone", "2"));

        EnvironmentMutations.Apply(coll, global, Vars(("off", "y")), new[] { "gone" });

        coll.Variables.Should().ContainSingle().Which.Should().Match<KvPair>(v => v.Name == "off" && v.Value == "y" && !v.Enabled);
        global.Variables.Should().BeEmpty();
    }

    [Fact]
    public void No_active_environment_is_a_no_op()
    {
        EnvironmentMutations.Apply(null, null, Vars(("a", "1")), new[] { "b" }).Should().BeFalse();
    }
}
