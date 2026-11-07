using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Workspaces;

/// <param name="SpaceId">Only this space (MCP resource <c>agcowork://space/{id}/overview</c>); null for the whole workspace.</param>
public sealed record GetWorkspaceOverviewRequest(Guid? SpaceId = null);

/// <summary>Orientation in one response (architecture §4.1): the workspace, its spaces and their collections with descriptions and counts.</summary>
/// <param name="Workspace">The workspace with the caller's role and scopes.</param>
/// <param name="Spaces">Spaces that are not archived, in navigation order.</param>
public sealed record WorkspaceOverviewDto(WorkspaceDto Workspace, IReadOnlyList<SpaceOverviewDto> Spaces);

/// <summary>A space with its collections.</summary>
/// <param name="Id">Space id (<c>spaceId</c> of collections and free pages).</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">What the space is for.</param>
/// <param name="Icon">Emoji or icon name.</param>
/// <param name="Collections">Collections of the space that are not archived, by name.</param>
/// <param name="PageCount">Free pages (records without collection) of the space that are not archived.</param>
public sealed record SpaceOverviewDto(Guid Id, string Name, string? Description, string? Icon, IReadOnlyList<CollectionSummaryDto> Collections, long PageCount);

/// <summary>A collection in the overview.</summary>
/// <param name="Id">Collection id (<c>collectionId</c>).</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">What a record is and when to add one. Written for agents.</param>
/// <param name="SchemaVersion">Current schema version.</param>
/// <param name="RecordCount">Records that are not archived.</param>
public sealed record CollectionSummaryDto(Guid Id, string Name, string Description, int SchemaVersion, long RecordCount);

/// <summary>
/// The entry point for agents (MCP <c>workspace_overview</c>): workspace, spaces, collections with descriptions and record
/// counts, archived spaces and collections left out. Requires <c>read</c>. With <see cref="GetWorkspaceOverviewRequest.SpaceId"/>
/// only that space; an unknown or archived space is 404.
/// </summary>
public sealed class GetWorkspaceOverview(IAgcoworkDbContext db, ITenantContext tenant, GetWorkspace getWorkspace)
    : IUseCase<GetWorkspaceOverviewRequest, WorkspaceOverviewDto>
{
    public async Task<Result<WorkspaceOverviewDto>> HandleAsync(GetWorkspaceOverviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var workspace = await getWorkspace.HandleAsync(new GetWorkspaceRequest(), cancellationToken).ConfigureAwait(false);
        if (workspace.IsFailure)
        {
            return workspace.Error;
        }

        var spaceQuery = db.Spaces.AsNoTracking().Where(s => s.ArchivedAt == null);
        if (request.SpaceId is { } spaceId)
        {
            spaceQuery = spaceQuery.Where(s => s.Id == spaceId);
        }

        var spaces = await spaceQuery.OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ThenBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (request.SpaceId is not null && spaces.Count == 0)
        {
            return new NotFoundError("space");
        }

        var spaceIds = spaces.Select(s => s.Id).ToList();
        var collections = await db.Collections.AsNoTracking()
            .Where(c => c.ArchivedAt == null && spaceIds.Contains(c.SpaceId))
            .OrderBy(c => c.Name).ThenBy(c => c.CreatedAt)
            .Select(c => new { c.Id, c.SpaceId, c.Name, c.Description, c.SchemaVersion })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // One grouped count for all collections and free pages of the listed spaces.
        var counts = await db.Records.AsNoTracking()
            .Where(r => r.ArchivedAt == null && spaceIds.Contains(r.SpaceId))
            .GroupBy(r => new { r.SpaceId, r.CollectionId })
            .Select(g => new { g.Key.SpaceId, g.Key.CollectionId, Count = g.LongCount() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var recordsByCollection = counts.Where(c => c.CollectionId != null).ToDictionary(c => c.CollectionId!.Value, c => c.Count);
        var pagesBySpace = counts.Where(c => c.CollectionId == null).ToDictionary(c => c.SpaceId, c => c.Count);

        return new WorkspaceOverviewDto(
            workspace.Value,
            [.. spaces.Select(space => new SpaceOverviewDto(
                space.Id,
                space.Name,
                space.Description,
                space.Icon,
                [.. collections.Where(c => c.SpaceId == space.Id)
                    .Select(c => new CollectionSummaryDto(c.Id, c.Name, c.Description, c.SchemaVersion, recordsByCollection.GetValueOrDefault(c.Id)))],
                pagesBySpace.GetValueOrDefault(space.Id)))]);
    }
}
