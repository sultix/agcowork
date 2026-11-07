using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Workspaces;

public sealed record GetWorkspaceRequest;

/// <summary>The current workspace with the caller's role and scopes. Requires <c>read</c>.</summary>
public sealed class GetWorkspace(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<GetWorkspaceRequest, WorkspaceDto>
{
    public async Task<Result<WorkspaceDto>> HandleAsync(GetWorkspaceRequest request, CancellationToken cancellationToken)
    {
        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var workspace = await db.Workspaces.AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == tenant.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);
        if (workspace is null)
        {
            return new NotFoundError("workspace");
        }

        return WorkspaceDto.From(workspace, tenant.Membership.Role, tenant.EffectiveScopes);
    }
}
