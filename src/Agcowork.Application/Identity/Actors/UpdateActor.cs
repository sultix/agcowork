using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Identity.Actors;

/// <summary>Partial update: null leaves a field unchanged.</summary>
/// <param name="ActorId">Actor to change.</param>
/// <param name="Name">New display name.</param>
/// <param name="Role">New free-text role; an empty string clears it.</param>
/// <param name="InstanceScopes">Replaces the whole set when given.</param>
/// <param name="IsActive">False deactivates: no login, keys rejected.</param>
public sealed record UpdateActorRequest(
    Guid ActorId,
    string? Name = null,
    string? Role = null,
    IReadOnlyList<string>? InstanceScopes = null,
    bool? IsActive = null);

/// <summary>Changes name, role, instance scopes or activation of an actor. Requires <c>instance:admin</c>.</summary>
public sealed class UpdateActor(IAgcoworkDbContext db, ICurrentActor currentActor) : IUseCase<UpdateActorRequest, ActorDto>
{
    public async Task<Result<ActorDto>> HandleAsync(UpdateActorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (currentActor.RequireInstanceScope(InstanceScope.Admin) is { } denied)
        {
            return denied;
        }

        var errors = new FieldErrors();
        if (request.Name is not null)
        {
            ActorInputValidation.Name(errors, request.Name);
        }

        ActorInputValidation.Role(errors, request.Role);
        ActorInputValidation.InstanceScopes(errors, request.InstanceScopes);
        if (errors.Any)
        {
            return errors.ToError();
        }

        var actor = await db.Actors.SingleOrDefaultAsync(a => a.Id == request.ActorId, cancellationToken).ConfigureAwait(false);
        if (actor is null)
        {
            return new NotFoundError("actor");
        }

        // An admin must not lock themselves out; otherwise the installation can end up without any admin.
        if (actor.Id == currentActor.Id)
        {
            if (request.IsActive == false)
            {
                return new ConflictError("self_lockout", "You cannot deactivate yourself.", "isActive");
            }

            if (request.InstanceScopes is { } newScopes && !newScopes.Contains(InstanceScope.Admin, StringComparer.Ordinal))
            {
                return new ConflictError("self_lockout", $"You cannot remove {InstanceScope.Admin} from yourself.", "instanceScopes");
            }
        }

        if (request.Name is not null)
        {
            actor.Rename(request.Name);
        }

        if (request.Role is not null)
        {
            actor.ChangeRole(request.Role);
        }

        if (request.InstanceScopes is not null)
        {
            actor.SetInstanceScopes(request.InstanceScopes);
        }

        if (request.IsActive is { } isActive)
        {
            if (isActive)
            {
                actor.Activate();
            }
            else
            {
                actor.Deactivate();
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ActorDto.From(actor);
    }
}
