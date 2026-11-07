using Agcowork.Application.Collections.Templates;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Schema;
using Agcowork.Application.Spaces;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Collections;

public sealed record DescribeCollectionRequest(Guid CollectionId);

/// <summary>
/// Orientation for agents (architecture §4.1, <c>collection_describe</c>): the collection with its schema, the
/// JSON Schema of record properties, an example, templates and statistics. Requires <c>read</c>.
/// </summary>
public sealed class DescribeCollection(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<DescribeCollectionRequest, CollectionDescriptionDto>
{
    public async Task<Result<CollectionDescriptionDto>> HandleAsync(DescribeCollectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var collection = await db.Collections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == request.CollectionId, cancellationToken).ConfigureAwait(false);
        if (collection is null)
        {
            return new NotFoundError("collection");
        }

        var space = await db.Spaces.AsNoTracking().SingleAsync(s => s.Id == collection.SpaceId, cancellationToken).ConfigureAwait(false);
        var templates = await ListTemplates.LoadAsync(db, collection.Id, cancellationToken).ConfigureAwait(false);

        return new CollectionDescriptionDto(
            CollectionDto.From(collection),
            SpaceDto.From(space),
            JsonSchemaBuilder.Build(collection),
            JsonSchemaBuilder.Example(collection.Schema),
            templates,
            new CollectionStatisticsDto(RecordCount: null));
    }
}
