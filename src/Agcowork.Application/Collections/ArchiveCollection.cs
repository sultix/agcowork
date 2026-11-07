using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Collections;

public sealed record ArchiveCollectionRequest(Guid CollectionId);

/// <summary>Archives a collection: readable, no longer changeable, hidden from lists by default. Requires <c>schema</c>.</summary>
public sealed class ArchiveCollection(IAgcoworkDbContext db, ITenantContext tenant, TimeProvider timeProvider)
    : IUseCase<ArchiveCollectionRequest, CollectionDto>
{
    public async Task<Result<CollectionDto>> HandleAsync(ArchiveCollectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireWritable() is { } archived)
        {
            return archived;
        }

        if (tenant.RequireScope(WorkspaceScope.Schema) is { } denied)
        {
            return denied;
        }

        var collection = await db.Collections.SingleOrDefaultAsync(c => c.Id == request.CollectionId, cancellationToken).ConfigureAwait(false);
        if (collection is null)
        {
            return new NotFoundError("collection");
        }

        if (collection.IsArchived)
        {
            return CollectionConflicts.Archived;
        }

        collection.Archive(timeProvider);
        if (await CollectionSave.SaveAsync(db, collection.Id, null, cancellationToken).ConfigureAwait(false) is { } conflict)
        {
            return conflict;
        }
        return CollectionDto.From(collection);
    }
}
