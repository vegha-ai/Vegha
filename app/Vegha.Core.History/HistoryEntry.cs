namespace Vegha.Core.History;

/// <summary>One row of request/response history.</summary>
public sealed record HistoryEntry(
    long Id,
    DateTimeOffset TimestampUtc,
    string Method,
    string Url,
    int StatusCode,
    long DurationMs,
    string? ResponseBodyPreview,
    string? ErrorMessage);
