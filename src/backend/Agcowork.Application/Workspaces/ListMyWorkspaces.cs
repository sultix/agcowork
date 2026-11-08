using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Workspaces;

public sealed record ListMyWorkspacesRequest;

/// <summary>
/// The workspaces the caller is a member of, with role and effective scopes, ordered by name;
/// archived ones included. A key restricted to one workspace sees only that one.
/// </summary>
public sealed class ListMyWorkspaces(IAgcoworkDbContext db, ICurrentActor currentActor, InstanceAccess instanceAccess)
    : IUseCase<ListMyWorkspacesRequest, IReadOnlyList<WorkspaceDto>>
{
    public async Task<Result<IReadOnlyList<WorkspaceDto>>> HandleAsync(ListMyWorkspacesRequest request, CancellationToken cancellationToken)
    {
        if (currentActor.RequireAuthenticated() is { } denied)
        {
            return denied;
        }

        var restriction = currentActor.KeyWorkspaceRestriction;

        // Instance-level endpoint without a tenant: the tenant filter and row-level security are bypassed (instance area),
        // bounded by the caller's own actor id.
        var query =
            from membership in db.Memberships.IgnoreQueryFilters([TenantQueryFilter.Name]).AsNoTracking()
            join workspace in db.Workspaces.AsNoTracking() on membership.WorkspaceId equals workspace.Id
            where membership.ActorId == currentActor.Id && (restriction == null || workspace.Id == restriction)
            orderby workspace.Name, workspace.CreatedAt
            select new { Workspace = workspace, membership.Role, membership.Scopes };
        var rows = await ReadAsInstance(query, cancellationToken).ConfigureAwait(false);

        var keyScopes = currentActor.KeyScopes;
        return Result.Success<IReadOnlyList<WorkspaceDto>>(
            [.. rows.Select(row => WorkspaceDto.From(row.Workspace, row.Role, EffectiveScopes.Compute(row.Scopes, keyScopes)))]);
    }

    private async Task<List<T>> ReadAsInstance<T>(IQueryable<T> query, CancellationToken cancellationToken)
    {
        using (instanceAccess.Enter("the caller's own memberships, outside any tenant"))
        {
            return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
