namespace Vegha.Core.Scripting;

/// <summary>One captured console.log / warn / error / info / debug line.</summary>
public sealed record ConsoleMessage(string Level, string Text, DateTimeOffset Timestamp);

/// <summary>
/// Collects user-script console output. Wired into Jint via <see cref="ConsoleApi"/> so
/// <c>console.log("…")</c> in pre-request / tests scripts lands here. The host (UI) drains
/// <see cref="Messages"/> into a "Console" subtab on the response viewer.
/// </summary>
public sealed class ConsoleSink
{
    private readonly List<ConsoleMessage> _messages = new();

    public IReadOnlyList<ConsoleMessage> Messages => _messages;

    public void Append(string level, params object?[] args)
    {
        var text = string.Join(" ", args.Select(FormatArg));
        _messages.Add(new ConsoleMessage(level, text, DateTimeOffset.UtcNow));
    }

    public void Clear() => _messages.Clear();

    private static string FormatArg(object? arg) => arg switch
    {
        null => "null",
        string s => s,
        // For objects, JSON-stringify so the user gets something useful instead of
        // the .ToString() default which is just the type name.
        _ => TryJson(arg),
    };

    private static string TryJson(object value)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Serialize(value,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
        }
        catch
        {
            return value.ToString() ?? string.Empty;
        }
    }
}

/// <summary>JavaScript-side <c>console</c> object. Each method appends to the sink with
/// the matching level. Host code reads <see cref="ConsoleSink.Messages"/> after the run.</summary>
public sealed class ConsoleApi
{
    private readonly ConsoleSink _sink;

    public ConsoleApi(ConsoleSink sink) { _sink = sink; }

    public void log(params object?[] args) => _sink.Append("log", args);
    public void info(params object?[] args) => _sink.Append("info", args);
    public void warn(params object?[] args) => _sink.Append("warn", args);
    public void error(params object?[] args) => _sink.Append("error", args);
    public void debug(params object?[] args) => _sink.Append("debug", args);
}
