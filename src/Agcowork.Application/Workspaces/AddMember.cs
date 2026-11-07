using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Workspaces;

/// <param name="ActorId">Actor (human or agent) to add; see <c>GET /api/v1/actors</c>.</param>
/// <param name="Role"><c>owner</c>, <c>admin</c>, <c>editor</c> or <c>viewer</c>.</param>
/// <param name="Scopes">Optional narrowing: a non-empty subset of the role's scopes. Omit for the role's defaults.</param>
public sealed record AddMemberRequest(Guid? ActorId, string? Role, IReadOnlyList<string>? Scopes = null);

/// <summary>
/// Adds an actor to the current workspace. Requires <c>admin</c>; only owners can add owners.
/// </summary>
public sealed class AddMember(IAgcoworkDbContext db, ICurrentActor currentActor, ITenantContext tenant, TimeProvider timeProvider)
    : IUseCase<AddMemberRequest, MemberDto>
{
    public async Task<Result<MemberDto>> HandleAsync(AddMemberRequest request, CancellationToken cancellationToken)
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
        if (request.ActorId is null || request.ActorId == Guid.Empty)
        {
            errors.Add("actorId", ErrorCodes.Required, "'actorId' is required.");
        }

        var role = WorkspaceInputValidation.Role(errors, request.Role, required: true);
        WorkspaceInputValidation.Scopes(errors, request.Scopes, role);
        if (errors.Any)
        {
            return errors.ToError();
        }

        if (role == WorkspaceRole.Owner && tenant.RequireOwner() is { } notOwner)
        {
            return notOwner;
        }

        var actorId = request.ActorId!.Value;
        var actor = await db.Actors.AsNoTracking().SingleOrDefaultAsync(a => a.Id == actorId, cancellationToken).ConfigureAwait(false);
        if (actor is null)
        {
            return new FieldError("actorId", ErrorCodes.NotFound, "No actor with this id exists.").ToValidationError();
        }

        if (!actor.IsActive)
        {
            return new FieldError("actorId", ErrorCodes.ActorInactive, "The actor is deactivated and cannot become a member. Reactivate it first.")
                .ToValidationError();
        }

        if (await db.Memberships.AnyAsync(m => m.ActorId == actorId, cancellationToken).ConfigureAwait(false))
        {
            return WorkspaceConflicts.Member.Error;
        }

        var membership = Membership.Create(tenant.WorkspaceId, actorId, role!.Value, request.Scopes, currentActor.Id, timeProvider);
        db.Memberships.Add(membership);
        if (await db.SaveChangesOrConflictAsync(cancellationToken, WorkspaceConflicts.Member).ConfigureAwait(false) is { } conflict)
        {
            return conflict;
        }

        return MemberDto.From(membership, actor);
    }
}
