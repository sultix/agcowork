using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Workspaces;

/// <param name="Slug">URL segment, unique, immutable: lowercase letters, digits, single hyphens, max. 63 characters.</param>
/// <param name="Name">Display name, max. 200 characters.</param>
/// <param name="Description">What the workspace is for, max. 2000 characters. Agents read it.</param>
/// <param name="TemplateId">Structure to start with. Only <c>empty</c> for now (also the default when omitted).</param>
public sealed record CreateWorkspaceRequest(string? Slug, string? Name, string? Description = null, string? TemplateId = null);

/// <summary>
/// Creates a workspace; the caller becomes its owner. Requires <c>workspaces:create</c> (implied by
/// <c>instance:admin</c>). Workspace and owner membership are stored in one SaveChanges, i.e. one transaction.
/// </summary>
public sealed class CreateWorkspace(IAgcoworkDbContext db, ICurrentActor currentActor, TimeProvider timeProvider)
    : IUseCase<CreateWorkspaceRequest, WorkspaceDto>
{
    /// <summary>Template ids accepted so far; <c>software-projekt</c> follows in AP-010.</summary>
    public static IReadOnlyList<string> Templates { get; } = ["empty"];

    public async Task<Result<WorkspaceDto>> HandleAsync(CreateWorkspaceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (currentActor.RequireInstanceScope(InstanceScope.WorkspacesCreate) is { } denied)
        {
            return denied;
        }

        var errors = new FieldErrors();
        WorkspaceInputValidation.Slug(errors, request.Slug);
        WorkspaceInputValidation.Name(errors, request.Name);
        WorkspaceInputValidation.Description(errors, request.Description);
        if (request.TemplateId is not null)
        {
            errors.OneOf("templateId", request.TemplateId, Templates);
        }

        if (errors.Any)
        {
            return errors.ToError();
        }

        if (await db.Workspaces.AnyAsync(w => w.Slug == request.Slug, cancellationToken).ConfigureAwait(false))
        {
            return WorkspaceConflicts.Slug.Error;
        }

        var workspace = Workspace.Create(request.Slug!, request.Name!, request.Description, currentActor.Id, timeProvider);
        var owner = Membership.Create(workspace.Id, currentActor.Id, WorkspaceRole.Owner, scopes: null, currentActor.Id, timeProvider);
        db.Workspaces.Add(workspace);
        // Outside a tenant context the tenant interceptor accepts this insert only because the workspace is new in the same save.
        db.Memberships.Add(owner);

        if (await db.SaveChangesOrConflictAsync(cancellationToken, WorkspaceConflicts.Slug).ConfigureAwait(false) is { } conflict)
        {
            return conflict;
        }

        return WorkspaceDto.From(workspace, owner.Role, EffectiveScopes.Compute(owner.Scopes, currentActor.KeyScopes));
    }
}
