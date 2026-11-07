using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Collections;

public sealed record GetCollectionRequest(Guid CollectionId);

/// <summary>One collection with its schema, archived ones included. Requires <c>read</c>.</summary>
public sealed class GetCollection(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<GetCollectionRequest, CollectionDto>
{
    public async Task<Result<CollectionDto>> HandleAsync(GetCollectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var collection = await db.Collections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == request.CollectionId, cancellationToken).ConfigureAwait(false);
        return collection is null ? new NotFoundError("collection") : CollectionDto.From(collection);
    }
}
