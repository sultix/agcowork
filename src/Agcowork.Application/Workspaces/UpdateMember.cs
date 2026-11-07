using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Workspaces;

/// <summary>Partial update of a membership.</summary>
/// <param name="ActorId">Member to change.</param>
/// <param name="Role">New role. Without <paramref name="Scopes"/> the scopes reset to the new role's defaults.</param>
/// <param name="Scopes">New narrowing: a non-empty subset of the (new) role's scopes.</param>
public sealed record UpdateMemberRequest(Guid ActorId, string? Role = null, IReadOnlyList<string>? Scopes = null);

/// <summary>
/// Changes role or scopes of a member. Requires <c>admin</c>; changing an owner or promoting to owner
/// requires the owner role. The last owner cannot be demoted (409 <c>last_owner</c>). Callers may narrow
/// but never widen their own membership (409 <c>self_escalation</c>).
/// </summary>
public sealed class UpdateMember(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<UpdateMemberRequest, MemberDto>
{
    public async Task<Result<MemberDto>> HandleAsync(UpdateMemberRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireWritable() is { } archived)
        {
            return archived;
        }

        if (tenant.RequireScope(WorkspaceScope.Admin) is { } denied)
        {
            return denied;
        }

        var errors = new FieldErrors();
        var newRole = WorkspaceInputValidation.Role(errors, request.Role, required: false);
        if (errors.Any)
        {
            return errors.ToError();
        }

        // Owner counting and the change must see the same memberships: lock the workspace row for the transaction.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.LockWorkspaceAsync(tenant.WorkspaceId, cancellationToken).ConfigureAwait(false);

        var membership = await db.Memberships.SingleOrDefaultAsync(m => m.ActorId == request.ActorId, cancellationToken).ConfigureAwait(false);
        if (membership is null)
        {
            return new NotFoundError("member");
        }

        // The tenant snapshot was taken before the lock; decide on the caller's membership as it is now.
        var caller = await MemberAuthorization.LoadCallerAsync(db, tenant, cancellationToken).ConfigureAwait(false);
        if (caller is null)
        {
            return new NotFoundError("workspace");
        }

        if (MemberAuthorization.RequireAdmin(caller) is { } noLongerAdmin)
        {
            return noLongerAdmin;
        }

        var targetRole = newRole ?? membership.Role;

        // Scopes are validated against the role they will belong to, which is known only now.
        WorkspaceInputValidation.Scopes(errors, request.Scopes, targetRole);
        if (errors.Any)
        {
            return errors.ToError();
        }

        if (membership.ActorId == caller.ActorId && MemberAuthorization.EscalatesSelf(membership, newRole, request.Scopes) is { } escalation)
        {
            return escalation;
        }

        if ((membership.IsOwner || targetRole == WorkspaceRole.Owner) && MemberAuthorization.RequireOwner(tenant, caller) is { } notOwner)
        {
            return notOwner;
        }

        var owners = await db.Memberships.CountAsync(m => m.Role == WorkspaceRole.Owner, cancellationToken).ConfigureAwait(false);
        if (!OwnerInvariant.HoldsAfter(owners, membership, targetRole))
        {
            return WorkspaceConflicts.LastOwner("role");
        }

        if (newRole is not null)
        {
            membership.ChangeRole(targetRole, request.Scopes);
        }
        else if (request.Scopes is not null)
        {
            membership.RestrictScopes(request.Scopes);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var actor = await db.Actors.AsNoTracking().SingleAsync(a => a.Id == membership.ActorId, cancellationToken).ConfigureAwait(false);
        return MemberDto.From(membership, actor);
    }
}
