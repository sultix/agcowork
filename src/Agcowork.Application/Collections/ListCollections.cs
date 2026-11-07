using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Collections;

/// <param name="SpaceId">Only collections of this space; null for the whole workspace.</param>
/// <param name="IncludeArchived">Also list archived collections.</param>
public sealed record ListCollectionsRequest(Guid? SpaceId = null, bool IncludeArchived = false);

/// <summary>Collections of a space or the whole workspace, by name. Requires <c>read</c>. An unknown space is 404.</summary>
public sealed class ListCollections(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<ListCollectionsRequest, IReadOnlyList<CollectionDto>>
{
    public async Task<Result<IReadOnlyList<CollectionDto>>> HandleAsync(ListCollectionsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var query = db.Collections.AsNoTracking();
        if (request.SpaceId is { } spaceId)
        {
            if (!await db.Spaces.AnyAsync(s => s.Id == spaceId, cancellationToken).ConfigureAwait(false))
            {
                return new NotFoundError("space");
            }

            query = query.Where(c => c.SpaceId == spaceId);
        }

        if (!request.IncludeArchived)
        {
            query = query.Where(c => c.ArchivedAt == null);
        }

        var collections = await query.OrderBy(c => c.Name).ThenBy(c => c.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success<IReadOnlyList<CollectionDto>>([.. collections.Select(CollectionDto.From)]);
    }
}
