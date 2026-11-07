using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Workspaces;

/// <summary>Checks of membership changes that run under the workspace lock (UpdateMember, RemoveMember).</summary>
internal static class MemberAuthorization
{
    /// <summary>The caller's current membership (tracked). Null when it was removed since the tenant was resolved.</summary>
    public static Task<Membership?> LoadCallerAsync(IAgcoworkDbContext db, ITenantContext tenant, CancellationToken cancellationToken) =>
        db.Memberships.SingleOrDefaultAsync(m => m.ActorId == tenant.Membership.ActorId, cancellationToken);

    /// <summary>The <c>admin</c> scope checked against the tenant snapshot must still hold for the current membership.</summary>
    public static ForbiddenError? RequireAdmin(Membership caller) =>
        caller.Scopes.Contains(WorkspaceScope.Admin, StringComparer.Ordinal) ? null : ForbiddenError.MissingScope(WorkspaceScope.Admin);

    /// <summary>
    /// Owner-only: the key must allow <c>admin</c> (<see cref="TenantAuthorization.RequireOwner"/>) and the
    /// caller must still be owner now, not only when the request started.
    /// </summary>
    public static Error? RequireOwner(ITenantContext tenant, Membership caller)
    {
        if (tenant.RequireOwner() is { } denied)
        {
            return denied;
        }

        return caller.IsOwner ? null : ForbiddenError.MissingScope("role owner");
    }

    /// <summary>
    /// 409 <c>self_escalation</c> when a change of the caller's own membership would raise the role or add a
    /// scope. Narrowing (and demoting oneself) is allowed; the owner invariant is checked separately.
    /// </summary>
    public static ConflictError? EscalatesSelf(Membership own, WorkspaceRole? newRole, IReadOnlyList<string>? newScopes)
    {
        var role = newRole ?? own.Role;
        if (role > own.Role)
        {
            return SelfEscalation("role");
        }

        // A new role without explicit scopes gets the role's defaults, which may exceed a narrowed membership.
        var resulting = newScopes ?? (newRole is null ? own.Scopes : WorkspaceScope.ForRole(role));
        return resulting.All(scope => own.Scopes.Contains(scope, StringComparer.Ordinal))
            ? null
            : SelfEscalation(newScopes is null ? "role" : "scopes");
    }

    private static ConflictError SelfEscalation(string field) =>
        new("self_escalation", "You can narrow your own membership but not widen it. Ask another admin or owner.", field);
}
