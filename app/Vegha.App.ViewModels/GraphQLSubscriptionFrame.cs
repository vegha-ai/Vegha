namespace Vegha.App.ViewModels;

/// <summary>One streamed GraphQL subscription event for the frames timeline.
/// <see cref="Kind"/> is "data" / "error" / "complete" / "system".</summary>
public sealed record GraphQLSubscriptionFrame(
    DateTimeOffset Timestamp, string Kind, string Preview, string PayloadJson)
{
    public string TimeDisplay => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
    public bool IsData => Kind == "data";
    public bool IsError => Kind == "error";
}
