using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Identity.Authentication;

public sealed record AuthenticateSessionRequest(Guid ActorId);

/// <summary>
/// Re-validates a cookie session on every request: the actor must still exist, be human and be
/// active. Instance scopes are re-read, so changes take effect immediately instead of at cookie expiry.
/// </summary>
public sealed class AuthenticateSession(IAgcoworkDbContext db) : IUseCase<AuthenticateSessionRequest, AuthenticatedActor>
{
    public async Task<Result<AuthenticatedActor>> HandleAsync(AuthenticateSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = await db.Actors.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == request.ActorId, cancellationToken)
            .ConfigureAwait(false);

        if (actor is null || actor.Kind != ActorKind.Human || !actor.IsActive)
        {
            return new UnauthorizedError("Session actor missing or inactive.");
        }

        return AuthenticatedActor.ForSession(actor);
    }
}
