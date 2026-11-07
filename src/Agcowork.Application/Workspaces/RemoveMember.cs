using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Workspaces;

/// <param name="ActorId">Member to remove.</param>
public sealed record RemoveMemberRequest(Guid ActorId);

/// <summary>
/// Removes a member from the current workspace. Requires <c>admin</c>; removing an owner requires the
/// owner role. The last owner cannot be removed (409 <c>last_owner</c>). Leaving the workspace oneself is allowed.
/// </summary>
public sealed class RemoveMember(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<RemoveMemberRequest>
{
    public async Task<Result> HandleAsync(RemoveMemberRequest request, CancellationToken cancellationToken)
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

        // Owner counting and the removal must see the same memberships: lock the workspace row for the transaction.
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

        if (membership.IsOwner && MemberAuthorization.RequireOwner(tenant, caller) is { } notOwner)
        {
            return notOwner;
        }

        var owners = await db.Memberships.CountAsync(m => m.Role == WorkspaceRole.Owner, cancellationToken).ConfigureAwait(false);
        if (!OwnerInvariant.HoldsAfter(owners, membership, newRole: null))
        {
            return WorkspaceConflicts.LastOwner(field: null);
        }

        db.Memberships.Remove(membership);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
