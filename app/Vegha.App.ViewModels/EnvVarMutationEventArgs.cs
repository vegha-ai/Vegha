namespace Vegha.App.ViewModels;

/// <summary>
/// Carries the environment-variable changes a script performed during a Send, expressed as
/// deltas relative to the variable snapshot the script ran against.
///
/// <see cref="Updated"/> holds added or changed name→value pairs (from <c>bru.setEnvVar</c>);
/// <see cref="Removed"/> holds names cleared via <c>bru.deleteEnvVar</c>. The host applies these
/// to the active environment in memory and re-pushes the merged snapshot to open tabs so the
/// next request resolves the new values — without this round-trip a token extracted in a
/// post-response script never reaches the following request.
/// </summary>
public sealed class EnvVarMutationEventArgs : EventArgs
{
    public EnvVarMutationEventArgs(
        IReadOnlyDictionary<string, string> updated,
        IReadOnlyCollection<string> removed)
    {
        Updated = updated;
        Removed = removed;
    }

    /// <summary>Variables the script added or changed (last value wins). Never null.</summary>
    public IReadOnlyDictionary<string, string> Updated { get; }

    /// <summary>Names the script deleted via <c>bru.deleteEnvVar</c>. Never null.</summary>
    public IReadOnlyCollection<string> Removed { get; }

    /// <summary>True when the script made no environment changes — callers can skip the event.</summary>
    public bool IsEmpty => Updated.Count == 0 && Removed.Count == 0;
}
