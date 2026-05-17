namespace Vegha.Core.Bru.Parser;

public sealed record BruDocument(IReadOnlyList<BruBlock> Blocks);

public abstract record BruBlock(string Name);

/// <summary>Block whose body is a list of key:value pairs (with optional annotations and disable prefix).</summary>
/// <remarks>Pairs are ordered; duplicate keys are permitted (e.g., repeated headers).</remarks>
public sealed record DictBlock(string Name, IReadOnlyList<BruPair> Pairs) : BruBlock(Name);

/// <summary>Block whose body is a single freeform text payload (script:*, body:json/xml/text/graphql/sparql, tests, docs, example).</summary>
public sealed record TextBlock(string Name, string Text) : BruBlock(Name);

/// <summary>Block whose body is a comma-separated list of identifiers in <c>[ ... ]</c>
/// — used by <c>vars:secret</c> to mark which variable names in the env file are secret.</summary>
public sealed record ListBlock(string Name, IReadOnlyList<string> Items) : BruBlock(Name);

/// <summary>One entry inside a <see cref="DictBlock"/>.</summary>
public sealed record BruPair(
    string Name,
    BruValue Value,
    bool Enabled = true,
    IReadOnlyList<BruAnnotation>? Annotations = null);

public abstract record BruValue;

/// <summary>Single-line string value (the most common case).</summary>
public sealed record StringValue(string Text) : BruValue
{
    public static implicit operator string(StringValue v) => v.Text;
}

/// <summary>List value: <c>key: [\n item1\n item2\n ]</c>. Items are restricted to alnum/_/- per Bruno grammar.</summary>
public sealed record ListValue(IReadOnlyList<string> Items) : BruValue;

/// <summary>Multiline-text value delimited by triple-quotes: <c>key: '''...''' @contentType(...)?</c></summary>
public sealed record MultilineValue(string Text, string? ContentType = null) : BruValue;

/// <summary>Decorator preceding a pair: <c>@description("…")</c> on its own line.</summary>
public sealed record BruAnnotation(string Name, string? RawArgs);
