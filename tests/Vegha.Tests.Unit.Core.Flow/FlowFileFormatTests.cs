using Vegha.Core.Flow;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Flow;

public class FlowFileFormatTests
{
    [Fact]
    public void Read_ParsesNodes_Edges_AndPositions()
    {
        const string json = """
            {
              "name": "demo",
              "nodes": [
                { "id": "s", "kind": "Start", "label": "Begin", "properties": {}, "x": 100, "y": 50 },
                { "id": "r", "kind": "Request", "label": "Hit /users",
                  "properties": { "requestId": "abc" }, "x": 260, "y": 50 },
                { "id": "e", "kind": "End", "label": "Done", "properties": {} }
              ],
              "edges": [
                { "fromNodeId": "s", "toNodeId": "r", "label": null },
                { "fromNodeId": "r", "toNodeId": "e", "label": "ok" }
              ]
            }
            """;

        var file = FlowFileFormat.Read(json);
        file.Definition.Name.Should().Be("demo");
        file.Definition.Nodes.Should().HaveCount(3);
        file.Definition.Edges.Should().HaveCount(2);
        file.NodePositions["s"].Should().Be((100, 50));
        file.NodePositions["r"].Should().Be((260, 50));
        // 'e' has no x/y; positions dict simply omits it.
        file.NodePositions.ContainsKey("e").Should().BeFalse();

        var requestNode = file.Definition.Nodes.Single(n => n.Id == "r");
        requestNode.Properties["requestId"].Should().Be("abc");
        requestNode.Kind.Should().Be(FlowNodeKind.Request);
    }

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var original = new FlowFileFormat.FlowFile(
            new FlowDefinition("rt", new[]
            {
                new FlowNode("s", FlowNodeKind.Start, "Start",
                    new Dictionary<string, string>()),
                new FlowNode("r", FlowNodeKind.Request, "Hit",
                    new Dictionary<string, string> { ["requestId"] = "u_42" }),
                new FlowNode("e", FlowNodeKind.End, "End",
                    new Dictionary<string, string>()),
            }, new[]
            {
                new FlowEdge("s", "r", null),
                new FlowEdge("r", "e", "ok"),
            }),
            new Dictionary<string, (double X, double Y)>
            {
                ["s"] = (50, 50),
                ["r"] = (220, 50),
                ["e"] = (390, 50),
            });

        var json = FlowFileFormat.Write(original);
        var restored = FlowFileFormat.Read(json);

        restored.Definition.Name.Should().Be("rt");
        restored.Definition.Nodes.Select(n => n.Id).Should()
            .Equal(original.Definition.Nodes.Select(n => n.Id));
        restored.Definition.Edges.Should().HaveCount(2);
        restored.Definition.Edges.Single(e => e.FromNodeId == "r").Label.Should().Be("ok");
        restored.Definition.Nodes.Single(n => n.Id == "r").Properties["requestId"].Should().Be("u_42");
        restored.NodePositions["e"].Should().Be((390, 50));
    }

    [Fact]
    public void Read_TolerantOfMissingProperties_Block()
    {
        const string json = """
            { "name": "lite",
              "nodes": [{ "id": "x", "kind": "Start", "label": "" }],
              "edges": [] }
            """;
        var file = FlowFileFormat.Read(json);
        file.Definition.Nodes.Single().Properties.Should().BeEmpty();
    }
}
