using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Workspaces;

/// <summary>Partial update: null leaves a field unchanged.</summary>
/// <param name="Name">New display name.</param>
/// <param name="Description">New description; an empty string clears it.</param>
public sealed record UpdateWorkspaceRequest(string? Name = null, string? Description = null);

/// <summary>Renames or re-describes the current workspace. Requires <c>admin</c> (roles admin, owner).</summary>
public sealed class UpdateWorkspace(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<UpdateWorkspaceRequest, WorkspaceDto>
{
    public async Task<Result<WorkspaceDto>> HandleAsync(UpdateWorkspaceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireWritable() is { } archived)
        {
            return archived;
        }

        if (tenant.RequireScope(WorkspaceScope.Admin) is { } denied)
        {
            return denied;
        }

        var errors = new FieldErrors();
        if (request.Name is not null)
        {
            WorkspaceInputValidation.Name(errors, request.Name);
        }

        WorkspaceInputValidation.Description(errors, request.Description);
        if (errors.Any)
        {
            return errors.ToError();
        }

        var workspace = await db.Workspaces.SingleOrDefaultAsync(w => w.Id == tenant.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace is null)
        {
            return new NotFoundError("workspace");
        }

        if (request.Name is not null)
        {
            workspace.Rename(request.Name);
        }

        if (request.Description is not null)
        {
            workspace.Describe(request.Description);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return WorkspaceDto.From(workspace, tenant.Membership.Role, tenant.EffectiveScopes);
    }
}
