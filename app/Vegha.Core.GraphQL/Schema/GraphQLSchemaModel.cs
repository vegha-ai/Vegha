using System.Collections.Frozen;

namespace Vegha.Core.GraphQL.Schema;

public enum GraphQLTypeKind { Scalar, Object, Interface, Union, Enum, InputObject, Unknown }

public enum TypeRefKind { Named, List, NonNull }

/// <summary>A (possibly wrapped) type reference, e.g. <c>[User!]!</c>. Kept as a tree —
/// the completion engine unwraps NON_NULL/LIST to find the selectable named type.</summary>
public sealed record TypeRef(TypeRefKind Kind, string? Name, TypeRef? OfType)
{
    /// <summary>Source-form rendering: <c>[User!]!</c>.</summary>
    public string Display => Kind switch
    {
        TypeRefKind.NonNull => (OfType?.Display ?? "?") + "!",
        TypeRefKind.List => "[" + (OfType?.Display ?? "?") + "]",
        _ => Name ?? "?",
    };

    /// <summary>The innermost named type (<c>User</c> for <c>[User!]!</c>).</summary>
    public string? UnwrappedName => Kind == TypeRefKind.Named ? Name : OfType?.UnwrappedName;

    public static TypeRef Named(string name) => new(TypeRefKind.Named, name, null);
}

public sealed record GraphQLArgInfo(
    string Name, string? Description, TypeRef Type, string? DefaultValue);

public sealed record GraphQLFieldInfo(
    string Name, string? Description, TypeRef Type,
    IReadOnlyList<GraphQLArgInfo> Args,
    bool IsDeprecated, string? DeprecationReason);

public sealed record GraphQLEnumValueInfo(
    string Name, string? Description, bool IsDeprecated);

public sealed record GraphQLTypeInfo(
    string Name,
    GraphQLTypeKind Kind,
    string? Description,
    IReadOnlyList<GraphQLFieldInfo> Fields,
    IReadOnlyList<GraphQLArgInfo> InputFields,
    IReadOnlyList<GraphQLEnumValueInfo> EnumValues,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<string> PossibleTypes);

public sealed record GraphQLDirectiveInfo(
    string Name, string? Description,
    IReadOnlyList<string> Locations,
    IReadOnlyList<GraphQLArgInfo> Args);

/// <summary>
/// Immutable in-memory GraphQL schema built from an introspection response. Type lookups
/// are hot (completion runs per keystroke), hence the frozen dictionary.
/// </summary>
public sealed class GraphQLSchemaModel
{
    public string? QueryTypeName { get; }
    public string? MutationTypeName { get; }
    public string? SubscriptionTypeName { get; }
    public FrozenDictionary<string, GraphQLTypeInfo> Types { get; }
    public IReadOnlyList<GraphQLDirectiveInfo> Directives { get; }

    public GraphQLSchemaModel(
        string? queryTypeName, string? mutationTypeName, string? subscriptionTypeName,
        IEnumerable<GraphQLTypeInfo> types, IReadOnlyList<GraphQLDirectiveInfo> directives)
    {
        QueryTypeName = queryTypeName;
        MutationTypeName = mutationTypeName;
        SubscriptionTypeName = subscriptionTypeName;
        Types = types.ToFrozenDictionary(t => t.Name, t => t, StringComparer.Ordinal);
        Directives = directives;
    }

    public GraphQLTypeInfo? FindType(string? name) =>
        name is not null && Types.TryGetValue(name, out var t) ? t : null;

    /// <summary>Root object type for an operation kind, or null when the schema doesn't
    /// support that operation (e.g. no subscriptions).</summary>
    public GraphQLTypeInfo? RootTypeFor(GraphQLOperationKind kind) => kind switch
    {
        GraphQLOperationKind.Mutation => FindType(MutationTypeName),
        GraphQLOperationKind.Subscription => FindType(SubscriptionTypeName),
        _ => FindType(QueryTypeName),
    };
}
