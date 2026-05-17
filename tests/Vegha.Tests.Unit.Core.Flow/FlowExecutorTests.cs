using Vegha.Core.Flow;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Flow;

public class FlowExecutorTests
{
    private sealed class FakeRunner : IFlowNodeRunner
    {
        private readonly List<string> _seen = new();
        public IReadOnlyList<string> Seen => _seen;
        public Func<FlowNode, FlowExecutionState, FlowNodeResult> Behavior { get; set; } =
            (_, _) => new FlowNodeResult(true, null, null);

        public Task<FlowNodeResult> RunAsync(FlowNode node, FlowExecutionState state, CancellationToken ct)
        {
            _seen.Add(node.Id);
            return Task.FromResult(Behavior(node, state));
        }
    }

    private static FlowExecutor MakeExecutor(IFlowNodeRunner runner) =>
        new(new Dictionary<FlowNodeKind, IFlowNodeRunner>
        {
            [FlowNodeKind.Request] = runner,
            [FlowNodeKind.Script] = runner,
            [FlowNodeKind.Branch] = runner,
            [FlowNodeKind.Parallel] = runner,
            [FlowNodeKind.Delay] = runner,
        });

    private static IReadOnlyDictionary<string, string> Empty() => new Dictionary<string, string>();

    [Fact]
    public async Task LinearFlow_VisitsEveryNodeInOrder()
    {
        var runner = new FakeRunner();
        var flow = new FlowDefinition("L", new[]
        {
            new FlowNode("s", FlowNodeKind.Start, "Start", Empty()),
            new FlowNode("a", FlowNodeKind.Request, "A", Empty()),
            new FlowNode("b", FlowNodeKind.Request, "B", Empty()),
            new FlowNode("e", FlowNodeKind.End, "End", Empty()),
        }, new[]
        {
            new FlowEdge("s", "a"),
            new FlowEdge("a", "b"),
            new FlowEdge("b", "e"),
        });

        var result = await MakeExecutor(runner).RunAsync(flow);
        result.Succeeded.Should().BeTrue();
        runner.Seen.Should().Equal("a", "b");
    }

    [Fact]
    public async Task BranchFlow_FollowsPreferredEdgeLabel()
    {
        var runner = new FakeRunner
        {
            Behavior = (node, _) => node.Kind == FlowNodeKind.Branch
                ? new FlowNodeResult(true, null, "yes")
                : new FlowNodeResult(true, null, null),
        };

        var flow = new FlowDefinition("B", new[]
        {
            new FlowNode("s", FlowNodeKind.Start, "Start", Empty()),
            new FlowNode("br", FlowNodeKind.Branch, "Branch", Empty()),
            new FlowNode("y", FlowNodeKind.Request, "Yes", Empty()),
            new FlowNode("n", FlowNodeKind.Request, "No", Empty()),
            new FlowNode("e", FlowNodeKind.End, "End", Empty()),
        }, new[]
        {
            new FlowEdge("s", "br"),
            new FlowEdge("br", "y", "yes"),
            new FlowEdge("br", "n", "no"),
            new FlowEdge("y", "e"),
            new FlowEdge("n", "e"),
        });

        var result = await MakeExecutor(runner).RunAsync(flow);
        result.Succeeded.Should().BeTrue();
        runner.Seen.Should().Contain("y");
        runner.Seen.Should().NotContain("n");
    }

    [Fact]
    public async Task NodeFailure_StopsRun()
    {
        var runner = new FakeRunner { Behavior = (n, _) => n.Id == "a" ? new FlowNodeResult(false, "boom", null) : new FlowNodeResult(true, null, null) };
        var flow = new FlowDefinition("F", new[]
        {
            new FlowNode("s", FlowNodeKind.Start, "Start", Empty()),
            new FlowNode("a", FlowNodeKind.Request, "A", Empty()),
            new FlowNode("b", FlowNodeKind.Request, "B", Empty()),
        }, new[] { new FlowEdge("s", "a"), new FlowEdge("a", "b") });

        var result = await MakeExecutor(runner).RunAsync(flow);
        result.Succeeded.Should().BeFalse();
        runner.Seen.Should().Equal("a");
    }

    [Fact]
    public async Task CycleDetection_Throws()
    {
        var runner = new FakeRunner();
        var flow = new FlowDefinition("C", new[]
        {
            new FlowNode("s", FlowNodeKind.Start, "Start", Empty()),
            new FlowNode("a", FlowNodeKind.Request, "A", Empty()),
            new FlowNode("b", FlowNodeKind.Request, "B", Empty()),
        }, new[] { new FlowEdge("s", "a"), new FlowEdge("a", "b"), new FlowEdge("b", "a") });

        var act = async () => await MakeExecutor(runner).RunAsync(flow);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Cycle*");
    }

    [Fact]
    public async Task ParallelFlow_FansOutToAllEdges()
    {
        var runner = new FakeRunner();
        var flow = new FlowDefinition("P", new[]
        {
            new FlowNode("s", FlowNodeKind.Start, "Start", Empty()),
            new FlowNode("p", FlowNodeKind.Parallel, "Fan", Empty()),
            new FlowNode("a", FlowNodeKind.Request, "A", Empty()),
            new FlowNode("b", FlowNodeKind.Request, "B", Empty()),
            new FlowNode("c", FlowNodeKind.Request, "C", Empty()),
        }, new[]
        {
            new FlowEdge("s", "p"),
            new FlowEdge("p", "a"),
            new FlowEdge("p", "b"),
            new FlowEdge("p", "c"),
        });

        var result = await MakeExecutor(runner).RunAsync(flow);
        result.Succeeded.Should().BeTrue();
        runner.Seen.Should().Contain(new[] { "a", "b", "c" });
    }
}
