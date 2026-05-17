using System.Text.Json;

namespace Vegha.Core.Flow;

/// <summary>
/// JSON read/write for flows on disk. We persist a thin wrapper around <see cref="FlowDefinition"/>
/// plus per-node canvas positions (so the visual designer survives reloads). The on-disk schema:
/// <code>
/// {
///   "name": "my-flow",
///   "nodes": [
///     { "id": "...", "kind": "Request", "label": "...",
///       "properties": { "requestId": "..." }, "x": 120.0, "y": 80.0 }
///   ],
///   "edges": [ { "fromNodeId": "...", "toNodeId": "...", "label": null } ]
/// }
/// </code>
/// Properties is an arbitrary string→string map; <c>x</c> + <c>y</c> sit at the node level so the
/// designer doesn't have to teach the executor about layout.
/// </summary>
public static class FlowFileFormat
{
    /// <summary>Loaded flow with optional per-node layout. Nodes without coords get
    /// (0,0) — the designer auto-lays-out missing positions.</summary>
    public sealed record FlowFile(
        FlowDefinition Definition,
        IReadOnlyDictionary<string, (double X, double Y)> NodePositions);

    public static FlowFile Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "flow" : "flow";

        var nodes = new List<FlowNode>();
        var positions = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);

        if (root.TryGetProperty("nodes", out var nodeArr) && nodeArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var ne in nodeArr.EnumerateArray())
            {
                var id = ne.GetProperty("id").GetString() ?? string.Empty;
                var kind = Enum.TryParse<FlowNodeKind>(ne.GetProperty("kind").GetString(), true, out var k)
                    ? k : FlowNodeKind.Start;
                var label = ne.TryGetProperty("label", out var l) ? l.GetString() ?? string.Empty : string.Empty;

                var props = new Dictionary<string, string>(StringComparer.Ordinal);
                if (ne.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object)
                    foreach (var prop in p.EnumerateObject())
                        if (prop.Value.ValueKind == JsonValueKind.String)
                            props[prop.Name] = prop.Value.GetString() ?? string.Empty;

                nodes.Add(new FlowNode(id, kind, label, props));

                double x = ne.TryGetProperty("x", out var xe) && xe.TryGetDouble(out var xd) ? xd : 0;
                double y = ne.TryGetProperty("y", out var ye) && ye.TryGetDouble(out var yd) ? yd : 0;
                if (x != 0 || y != 0) positions[id] = (x, y);
            }
        }

        var edges = new List<FlowEdge>();
        if (root.TryGetProperty("edges", out var edgeArr) && edgeArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var ee in edgeArr.EnumerateArray())
            {
                var from = ee.GetProperty("fromNodeId").GetString() ?? string.Empty;
                var to = ee.GetProperty("toNodeId").GetString() ?? string.Empty;
                var label = ee.TryGetProperty("label", out var lab) ? lab.GetString() : null;
                edges.Add(new FlowEdge(from, to, label));
            }
        }
        return new FlowFile(new FlowDefinition(name, nodes, edges), positions);
    }

    public static string Write(FlowFile file)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("name", file.Definition.Name);
            w.WritePropertyName("nodes");
            w.WriteStartArray();
            foreach (var node in file.Definition.Nodes)
            {
                w.WriteStartObject();
                w.WriteString("id", node.Id);
                w.WriteString("kind", node.Kind.ToString());
                w.WriteString("label", node.Label);
                w.WritePropertyName("properties");
                w.WriteStartObject();
                foreach (var p in node.Properties) w.WriteString(p.Key, p.Value);
                w.WriteEndObject();
                if (file.NodePositions.TryGetValue(node.Id, out var pos))
                {
                    w.WriteNumber("x", pos.X);
                    w.WriteNumber("y", pos.Y);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WritePropertyName("edges");
            w.WriteStartArray();
            foreach (var edge in file.Definition.Edges)
            {
                w.WriteStartObject();
                w.WriteString("fromNodeId", edge.FromNodeId);
                w.WriteString("toNodeId", edge.ToNodeId);
                if (edge.Label is null) w.WriteNull("label");
                else w.WriteString("label", edge.Label);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
