using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Tenancy;

/// <param name="Slug">Workspace slug from the route (<c>/w/{workspaceSlug}</c>).</param>
public sealed record ResolveTenantRequest(string Slug);

/// <summary>
/// Step 3 of architecture §2a: loads the workspace by slug and the caller's membership in it and
/// computes the effective scopes. Unknown workspace, no membership and a key restricted to another
/// workspace all yield the same <see cref="NotFoundError"/>, so the existence of foreign workspaces is
/// never revealed. Does not set the tenant; the adapter (middleware) does.
/// </summary>
public sealed class ResolveTenant(IAgcoworkDbContext db, ICurrentActor currentActor) : IUseCase<ResolveTenantRequest, TenantContext>
{
    public async Task<Result<TenantContext>> HandleAsync(ResolveTenantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (currentActor.RequireAuthenticated() is { } denied)
        {
            return denied;
        }

        // A malformed slug cannot exist; answer like an unknown one without touching the database.
        if (!Workspace.IsValidSlug(request.Slug))
        {
            return new NotFoundError("workspace");
        }

        // Runs before a tenant exists, so the tenant filter has to be bypassed. Bounded by the caller's
        // own actor id: the query can only ever return the caller's membership in this one workspace.
        var match = await (
                from workspace in db.Workspaces.AsNoTracking()
                join membership in db.Memberships.IgnoreQueryFilters([TenantQueryFilter.Name]).AsNoTracking()
                    on workspace.Id equals membership.WorkspaceId
                where workspace.Slug == request.Slug && membership.ActorId == currentActor.Id
                select new { workspace.Id, workspace.Slug, workspace.ArchivedAt, Membership = membership })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (match is null)
        {
            return new NotFoundError("workspace");
        }

        // A key restricted to one workspace is rejected everywhere else, indistinguishably from "no access".
        if (currentActor.KeyWorkspaceRestriction is { } restriction && restriction != match.Id)
        {
            return new NotFoundError("workspace");
        }

        var effective = EffectiveScopes.Compute(match.Membership.Scopes, currentActor.KeyScopes);
        return new TenantContext(match.Id, match.Slug, match.ArchivedAt is not null, match.Membership, effective);
    }
}
