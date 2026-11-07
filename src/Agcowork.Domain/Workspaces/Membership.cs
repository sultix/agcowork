using Agcowork.Domain.Common;

namespace Agcowork.Domain.Workspaces;

/// <summary>
/// Belonging of an actor to a workspace with a role. Unique per (workspace, actor). The rule
/// "at least one owner per workspace" spans several memberships; see <see cref="OwnerInvariant"/>.
/// </summary>
public sealed class Membership : ITenantEntity
{
    private Membership(Guid id, Guid workspaceId, Guid actorId, WorkspaceRole role, IReadOnlyList<string> scopes, Guid createdBy, DateTimeOffset createdAt)
    {
        Id = id;
        WorkspaceId = workspaceId;
        ActorId = actorId;
        Role = role;
        Scopes = scopes;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public Guid ActorId { get; private set; }

    public WorkspaceRole Role { get; private set; }

    /// <summary>
    /// Ordinal-sorted subset of <see cref="WorkspaceScope.ForRole"/> for <see cref="Role"/>: all of them
    /// unless the membership was narrowed explicitly.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsOwner => Role == WorkspaceRole.Owner;

    /// <summary>Creates a membership. <paramref name="scopes"/>: null for the role's default scopes, otherwise a non-empty subset of them.</summary>
    public static Membership Create(Guid workspaceId, Guid actorId, WorkspaceRole role, IEnumerable<string>? scopes, Guid createdBy, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must be set.", nameof(workspaceId));
        }

        if (actorId == Guid.Empty)
        {
            throw new ArgumentException("Actor id must be set.", nameof(actorId));
        }

        var createdAt = timeProvider.GetUtcNow();
        return new Membership(Guid.CreateVersion7(createdAt), workspaceId, actorId, GuardRole(role), GuardScopes(role, scopes), createdBy, createdAt);
    }

    /// <summary>Changes the role. <paramref name="scopes"/> null resets the scopes to the new role's defaults.</summary>
    public void ChangeRole(WorkspaceRole role, IEnumerable<string>? scopes)
    {
        var scopesForRole = GuardScopes(GuardRole(role), scopes);
        Role = role;
        Scopes = scopesForRole;
    }

    /// <summary>Narrows (or widens back up to the role's defaults) the scopes without changing the role.</summary>
    public void RestrictScopes(IEnumerable<string>? scopes) => Scopes = GuardScopes(Role, scopes);

    /// <summary>True when <paramref name="scopes"/> is a non-empty subset of the role's default scopes.</summary>
    public static bool AreValidScopesFor(WorkspaceRole role, IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var allowed = WorkspaceScope.ForRole(role);
        var list = scopes.ToList();
        return list.Count > 0 && list.All(scope => allowed.Contains(scope, StringComparer.Ordinal));
    }

    private static WorkspaceRole GuardRole(WorkspaceRole role) =>
        Enum.IsDefined(role) ? role : throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown workspace role.");

    private static string[] GuardScopes(WorkspaceRole role, IEnumerable<string>? scopes)
    {
        if (scopes is null)
        {
            return [.. WorkspaceScope.ForRole(role).Order(StringComparer.Ordinal)];
        }

        var normalized = scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!AreValidScopesFor(role, normalized))
        {
            throw new ArgumentException(
                $"Scopes must be a non-empty subset of [{string.Join(", ", WorkspaceScope.ForRole(role))}] for role {role}.",
                nameof(scopes));
        }

        return normalized;
    }
}
