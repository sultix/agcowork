using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Spaces;

/// <param name="IncludeArchived">Also list archived spaces.</param>
public sealed record ListSpacesRequest(bool IncludeArchived = false);

/// <summary>Spaces of the current workspace by sort order, then name. Requires <c>read</c>.</summary>
public sealed class ListSpaces(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<ListSpacesRequest, IReadOnlyList<SpaceDto>>
{
    public async Task<Result<IReadOnlyList<SpaceDto>>> HandleAsync(ListSpacesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var query = db.Spaces.AsNoTracking();
        if (!request.IncludeArchived)
        {
            query = query.Where(s => s.ArchivedAt == null);
        }

        var spaces = await query.OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ThenBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success<IReadOnlyList<SpaceDto>>([.. spaces.Select(SpaceDto.From)]);
    }
}
