namespace Vegha.App.ViewModels;

/// <summary>Which environment set an <see cref="EnvironmentsViewModel"/> instance manages.
/// The DI singleton uses <see cref="Collection"/> (the left-toolbar Environments panel);
/// the Manage Global Environments dialog constructs a transient <see cref="Global"/>-scoped
/// instance over the workspace's <c>environments/</c> folder.</summary>
public enum EnvironmentScopeKind
{
    /// <summary>The active collection's <c>environments/</c> folder —
    /// <c>CollectionEnvironments</c> / <c>ActiveEnvironment</c>.</summary>
    Collection,

    /// <summary>The active workspace's <c>environments/</c> folder —
    /// <c>GlobalEnvironments</c> / <c>ActiveGlobalEnvironment</c>.</summary>
    Global,
}
