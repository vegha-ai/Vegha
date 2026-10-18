using System.Text;
using System.Text.Json;

namespace Vegha.Core.Scripting.Testing;

/// <summary>
/// Maps a caret position in JSON text (typically the pretty-printed response body) to the
/// friendly path of the value under it — the engine behind "right-click a value → Capture as
/// variable / Assert". Tolerates the caret sitting on a key, a value, a bracket, or whitespace
/// after a value (the nearest preceding token wins).
/// </summary>
public static class JsonPathLocator
{
    /// <summary>The value under the caret. <see cref="Path"/> is a friendly path ("" = root);
    /// <see cref="SuggestedName"/> is a variable-name suggestion derived from the nearest key.</summary>
    public sealed record Location(string Path, string SuggestedName, bool IsContainer);

    private sealed class Frame
    {
        public bool IsArray;
        public int Index = -1;
        public string? Property;
    }

    public static Location? Locate(string json, int charOffset)
    {
        if (string.IsNullOrEmpty(json)) return null;
        charOffset = Math.Clamp(charOffset, 0, json.Length);
        var bytes = Encoding.UTF8.GetBytes(json);
        var caret = Encoding.UTF8.GetByteCount(json.AsSpan(0, charOffset));

        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var stack = new List<Frame>();
        Location? best = null;

        try
        {
            while (reader.Read())
            {
                var start = (int)reader.TokenStartIndex;
                if (start > caret) break;
                var end = (int)reader.BytesConsumed;
                var top = stack.Count > 0 ? stack[^1] : null;

                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        top!.Property = reader.GetString();
                        best = Build(stack, isContainer: false);
                        break;

                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        if (top is { IsArray: true }) top.Index++;
                        // Caret on the opening bracket selects the container itself.
                        best = Build(stack, isContainer: true);
                        stack.Add(new Frame { IsArray = reader.TokenType == JsonTokenType.StartArray });
                        break;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                        // Caret on the closing bracket also selects the container.
                        if (caret >= start && caret < end) best = Build(stack, isContainer: true);
                        break;

                    default: // primitive value
                        if (top is { IsArray: true }) top.Index++;
                        best = Build(stack, isContainer: false);
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Truncated / invalid JSON: return what we resolved before the error.
        }
        return best;
    }

    private static Location Build(List<Frame> stack, bool isContainer)
    {
        var segments = new List<FriendlyPath.Segment>();
        string? lastKey = null;
        foreach (var f in stack)
        {
            if (f.IsArray)
            {
                if (f.Index >= 0) segments.Add(new FriendlyPath.Segment(null, f.Index, false));
            }
            else if (f.Property is not null)
            {
                segments.Add(new FriendlyPath.Segment(f.Property, null, false));
                lastKey = f.Property;
            }
        }
        return new Location(FriendlyPath.Format(segments), ToVariableName(lastKey), isContainer);
    }

    /// <summary>Turns a JSON key into a variable-name suggestion: <c>access_token</c> stays,
    /// <c>user-id</c> → <c>userId</c>, empty → <c>value</c>.</summary>
    public static string ToVariableName(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "value";
        var sb = new StringBuilder();
        var upperNext = false;
        foreach (var c in key)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(upperNext && sb.Length > 0 ? char.ToUpperInvariant(c) : c);
                upperNext = false;
            }
            else upperNext = true;
        }
        if (sb.Length == 0) return "value";
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }
}
