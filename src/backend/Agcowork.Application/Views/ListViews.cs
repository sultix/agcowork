using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Views;

public sealed record ListViewsRequest(Guid CollectionId);

/// <summary>
/// Views of a collection by sort order, then name. Requires <c>read</c>. Never creates views: the standard views
/// are created together with the collection.
/// </summary>
public sealed class ListViews(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<ListViewsRequest, IReadOnlyList<ViewDto>>
{
    public async Task<Result<IReadOnlyList<ViewDto>>> HandleAsync(ListViewsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        if (!await db.Collections.AnyAsync(c => c.Id == request.CollectionId, cancellationToken).ConfigureAwait(false))
        {
            return new NotFoundError("collection");
        }

        var views = await db.Views.AsNoTracking()
            .Where(v => v.CollectionId == request.CollectionId)
            .OrderBy(v => v.SortOrder).ThenBy(v => v.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return Result.Success<IReadOnlyList<ViewDto>>([.. views.Select(ViewDto.From)]);
    }
}
