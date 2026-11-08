using Agcowork.Application.Tenancy;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Application.Tests.Tenancy;

/// <summary>
/// Two workspaces "alpha" and "beta" in a test database, each with its owner, plus an actor that is an
/// editor in both. Seeded outside any tenant as the owner (no row-level security, like a migration): workspaces and
/// their memberships go in one save, which the tenant interceptor allows for newly created workspaces.
/// </summary>
internal sealed record TenancySeed(Workspace Alpha, Workspace Beta, Actor AlphaOwner, Actor BetaOwner, Actor MemberOfBoth)
{
    public static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    public static async Task<TenancySeed> CreateAsync(AgcoworkDbContext appDb)
    {
        await using var db = PostgresFixture.OpenOwner(appDb);
        var clock = new FakeTimeProvider(Now);
        var alphaOwner = Actor.CreateAgent("Alpha owner", null, null, [InstanceScope.WorkspacesCreate], clock);
        var betaOwner = Actor.CreateAgent("Beta owner", null, null, [InstanceScope.WorkspacesCreate], clock);
        var both = Actor.CreateAgent("Both", null, null, [], clock);
        db.Actors.AddRange(alphaOwner, betaOwner, both);

        var alpha = Workspace.Create("alpha", "Alpha", null, alphaOwner.Id, clock);
        var beta = Workspace.Create("beta", "Beta", null, betaOwner.Id, clock);
        db.Workspaces.AddRange(alpha, beta);
        db.Memberships.AddRange(
            Membership.Create(alpha.Id, alphaOwner.Id, WorkspaceRole.Owner, null, alphaOwner.Id, clock),
            Membership.Create(beta.Id, betaOwner.Id, WorkspaceRole.Owner, null, betaOwner.Id, clock),
            Membership.Create(alpha.Id, both.Id, WorkspaceRole.Editor, null, alphaOwner.Id, clock),
            Membership.Create(beta.Id, both.Id, WorkspaceRole.Editor, null, betaOwner.Id, clock));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new TenancySeed(alpha, beta, alphaOwner, betaOwner, both);
    }

    /// <summary>Tenant context of <paramref name="actor"/> in <paramref name="workspace"/>, as ResolveTenant would build it.</summary>
    public static TenantContext TenantFor(AgcoworkDbContext appDb, Workspace workspace, Actor actor, IReadOnlySet<string>? keyScopes = null, bool archived = false)
    {
        using var db = PostgresFixture.OpenOwner(appDb);
        var membership = db.Memberships.IgnoreQueryFilters([TenantQueryFilter.Name]).AsNoTracking()
            .Single(m => m.WorkspaceId == workspace.Id && m.ActorId == actor.Id);
        return new TenantContext(workspace.Id, workspace.Slug, archived, membership, EffectiveScopes.Compute(membership.Scopes, keyScopes ?? new HashSet<string>()));
    }
}
