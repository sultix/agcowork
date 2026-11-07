using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Identity.Actors;

public sealed record GetActorRequest(Guid ActorId);

/// <summary>Reads one actor. Requires <c>instance:admin</c>.</summary>
public sealed class GetActor(IAgcoworkDbContext db, ICurrentActor currentActor) : IUseCase<GetActorRequest, ActorDto>
{
    public async Task<Result<ActorDto>> HandleAsync(GetActorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (currentActor.RequireInstanceScope(InstanceScope.Admin) is { } denied)
        {
            return denied;
        }

        var actor = await db.Actors.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == request.ActorId, cancellationToken)
            .ConfigureAwait(false);

        return actor is null ? new NotFoundError("actor") : ActorDto.From(actor);
    }
}
