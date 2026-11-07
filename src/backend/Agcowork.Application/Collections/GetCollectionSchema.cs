using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Collections;

public sealed record GetCollectionSchemaRequest(Guid CollectionId);

/// <summary>The schema of a collection as definitions and JSON Schema. Requires <c>read</c>.</summary>
public sealed class GetCollectionSchema(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<GetCollectionSchemaRequest, CollectionSchemaDto>
{
    public async Task<Result<CollectionSchemaDto>> HandleAsync(GetCollectionSchemaRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var collection = await db.Collections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == request.CollectionId, cancellationToken).ConfigureAwait(false);
        return collection is null ? new NotFoundError("collection") : CollectionSchemaDto.From(collection);
    }
}
