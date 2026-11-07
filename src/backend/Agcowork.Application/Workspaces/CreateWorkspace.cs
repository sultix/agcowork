using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Workspaces.Templates;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Workspaces;

/// <param name="Slug">URL segment, unique, immutable: lowercase letters, digits, single hyphens, max. 63 characters.</param>
/// <param name="Name">Display name, max. 200 characters.</param>
/// <param name="Description">What the workspace is for, max. 2000 characters. Agents read it.</param>
/// <param name="TemplateId">Structure to start with: <c>software-projekt</c> (default when omitted) or <c>empty</c>. See GET /workspace-templates.</param>
public sealed record CreateWorkspaceRequest(string? Slug, string? Name, string? Description = null, string? TemplateId = null);

/// <summary>
/// Creates a workspace; the caller becomes its owner. Requires <c>workspaces:create</c> (implied by
/// <c>instance:admin</c>). Workspace, owner membership and the content of the template are stored in one
/// transaction: if the template fails, nothing is created.
/// </summary>
/// <remarks>
/// The template runs through the regular use cases in this very scope. For that the tenant of the scope is set to
/// the new workspace (owner membership, all scopes) after the workspace row exists; the scope keeps it afterwards,
/// so an adapter must not use the scope for anything but answering this request.
/// </remarks>
public sealed class CreateWorkspace(
    IAgcoworkDbContext db,
    ICurrentActor currentActor,
    TimeProvider timeProvider,
    ITenantContextAccessor tenantAccessor,
    IServiceProvider services)
    : IUseCase<CreateWorkspaceRequest, WorkspaceDto>
{
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
            errors.OneOf("templateId", request.TemplateId, WorkspaceTemplates.Ids);
        }

        if (errors.Any)
        {
            return errors.ToError();
        }

        if (await db.Workspaces.AnyAsync(w => w.Slug == request.Slug, cancellationToken).ConfigureAwait(false))
        {
            return WorkspaceConflicts.Slug.Error;
        }

        var template = WorkspaceTemplates.Find(request.TemplateId ?? WorkspaceTemplates.DefaultId)
            ?? throw new InvalidOperationException("Template ids are validated against the registry above.");

        var workspace = Workspace.Create(request.Slug!, request.Name!, request.Description, currentActor.Id, timeProvider);
        var owner = Membership.Create(workspace.Id, currentActor.Id, WorkspaceRole.Owner, scopes: null, currentActor.Id, timeProvider);
        db.Workspaces.Add(workspace);
        // Outside a tenant context the tenant interceptor accepts this insert only because the workspace is new in the same save.
        db.Memberships.Add(owner);

        await using var transaction = await db.BeginOrJoinTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await db.SaveChangesOrConflictAsync(cancellationToken, WorkspaceConflicts.Slug).ConfigureAwait(false) is { } conflict)
        {
            return conflict;
        }

        // The template's use cases need a tenant. The creator is the owner, so the full membership scopes apply
        // (the key restrictions of the request only limit what the caller may do in existing workspaces).
        tenantAccessor.SetTenant(new TenantContext(
            workspace.Id, workspace.Slug, isArchived: false, owner, EffectiveScopes.Compute(owner.Scopes, new HashSet<string>())));
        var applied = await template.ApplyAsync(services, workspace.Id, currentActor.Id, cancellationToken).ConfigureAwait(false);
        if (applied.IsFailure)
        {
            // Built-in templates only use valid input, so a failure is a programming error (500, logged), not the caller's fault.
            // Leaving without commit rolls everything back.
            throw new InvalidOperationException(
                $"Applying workspace template '{template.Id}' to new workspace '{workspace.Slug}' failed: {applied.Error}.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return WorkspaceDto.From(workspace, owner.Role, EffectiveScopes.Compute(owner.Scopes, currentActor.KeyScopes));
    }
}
