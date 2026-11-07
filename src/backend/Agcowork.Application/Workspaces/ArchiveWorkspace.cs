using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Workspaces;

public sealed record ArchiveWorkspaceRequest;

/// <summary>
/// Archives the current workspace: it stays readable, every write fails with 409 <c>workspace_archived</c>.
/// Owner only. There is no un-archive yet.
/// </summary>
public sealed class ArchiveWorkspace(IAgcoworkDbContext db, ITenantContext tenant, TimeProvider timeProvider)
    : IUseCase<ArchiveWorkspaceRequest, WorkspaceDto>
{
    public async Task<Result<WorkspaceDto>> HandleAsync(ArchiveWorkspaceRequest request, CancellationToken cancellationToken)
    {
        if (tenant.RequireWritable() is { } archived)
        {
            return archived;
        }

        if (tenant.RequireOwner() is { } denied)
        {
            return denied;
        }

        var workspace = await db.Workspaces.SingleOrDefaultAsync(w => w.Id == tenant.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace is null)
        {
            return new NotFoundError("workspace");
        }

        if (workspace.IsArchived)
        {
            return WorkspaceConflicts.Archived;
        }

        workspace.Archive(timeProvider);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return WorkspaceDto.From(workspace, tenant.Membership.Role, tenant.EffectiveScopes);
    }
}
