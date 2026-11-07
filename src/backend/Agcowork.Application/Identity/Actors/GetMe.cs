using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Identity.ApiKeys;
using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Identity.Actors;

public sealed record GetMeRequest;

/// <summary>Returns the calling actor, its instance scopes, whether it must change its password and, for key calls, the key metadata.</summary>
public sealed class GetMe(IAgcoworkDbContext db, ICurrentActor currentActor) : IUseCase<GetMeRequest, MeDto>
{
    public async Task<Result<MeDto>> HandleAsync(GetMeRequest request, CancellationToken cancellationToken)
    {
        if (currentActor.RequireAuthenticated() is { } denied)
        {
            return denied;
        }

        var actor = await db.Actors.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == currentActor.Id, cancellationToken)
            .ConfigureAwait(false);
        if (actor is null)
        {
            return new UnauthorizedError("Authenticated actor no longer exists.");
        }

        ApiKeyDto? key = null;
        if (currentActor.ApiKeyId is { } keyId)
        {
            var apiKey = await db.ApiKeys.AsNoTracking()
                .SingleOrDefaultAsync(k => k.Id == keyId, cancellationToken)
                .ConfigureAwait(false);
            key = apiKey is null ? null : ApiKeyDto.From(apiKey);
        }

        var mustChangePassword = actor.Kind == ActorKind.Human
            && await db.LocalLogins.AsNoTracking()
                .AnyAsync(l => l.ActorId == actor.Id && l.MustChangePassword, cancellationToken)
                .ConfigureAwait(false);

        // The caller's effective instance scopes: a restricted key carries none, even if the actor has some.
        return new MeDto(actor.Id, actor.Kind, actor.Name, actor.Role, actor.Email, [.. currentActor.InstanceScopes.Order(StringComparer.Ordinal)], key, mustChangePassword);
    }
}
