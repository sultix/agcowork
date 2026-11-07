using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Identity.ApiKeys;

public sealed record ListApiKeysRequest(Guid ActorId);

/// <summary>Lists the keys of an actor (metadata only, newest first, including revoked ones). Requires <c>instance:admin</c>.</summary>
public sealed class ListApiKeys(IAgcoworkDbContext db, ICurrentActor currentActor) : IUseCase<ListApiKeysRequest, IReadOnlyList<ApiKeyDto>>
{
    public async Task<Result<IReadOnlyList<ApiKeyDto>>> HandleAsync(ListApiKeysRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (currentActor.RequireInstanceScope(InstanceScope.Admin) is { } denied)
        {
            return denied;
        }

        if (!await db.Actors.AnyAsync(a => a.Id == request.ActorId, cancellationToken).ConfigureAwait(false))
        {
            return new NotFoundError("actor");
        }

        var keys = await db.ApiKeys.AsNoTracking()
            .Where(k => k.ActorId == request.ActorId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Result.Success<IReadOnlyList<ApiKeyDto>>([.. keys.Select(ApiKeyDto.From)]);
    }
}
