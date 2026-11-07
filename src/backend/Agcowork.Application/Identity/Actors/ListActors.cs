using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Identity.Actors;

public sealed record ListActorsRequest;

/// <summary>Lists all actors of the installation, ordered by name. Requires <c>instance:admin</c>.</summary>
/// <remarks>No paging yet: installations have tens of actors, not thousands.</remarks>
public sealed class ListActors(IAgcoworkDbContext db, ICurrentActor currentActor) : IUseCase<ListActorsRequest, IReadOnlyList<ActorDto>>
{
    public async Task<Result<IReadOnlyList<ActorDto>>> HandleAsync(ListActorsRequest request, CancellationToken cancellationToken)
    {
        if (currentActor.RequireInstanceScope(InstanceScope.Admin) is { } denied)
        {
            return denied;
        }

        var actors = await db.Actors.AsNoTracking()
            .OrderBy(a => a.Name).ThenBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Result.Success<IReadOnlyList<ActorDto>>([.. actors.Select(ActorDto.From)]);
    }
}
