using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Common;
using Agcowork.Domain.Workspaces;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agcowork.Api.Tests.Tenancy;

/// <summary>
/// Query filter and SaveChanges interceptor against the real model, outside HTTP: a DI scope plays the
/// request, the tenant is set as the middleware would set it.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class TenantInterceptorTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task QueryFilter_TenantSet_ShowsOnlyOwnMemberships()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var scope = await EnterAsync(t.Alpha.Id, t.AlphaOwner.ActorId);
        var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();

        var memberships = await db.Memberships.ToListAsync(Ct);

        Assert.NotEmpty(memberships);
        Assert.All(memberships, m => Assert.Equal(t.Alpha.Id, m.WorkspaceId));
        Assert.False(await db.Memberships.AnyAsync(m => m.WorkspaceId == t.Beta.Id, Ct));
        Assert.Null(await db.Memberships.SingleOrDefaultAsync(m => m.ActorId == t.BetaOwner.ActorId, Ct));
    }

    [Fact]
    public async Task QueryFilter_NoTenant_ShowsNothingUnlessBypassedExplicitly()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();

        Assert.Empty(await db.Memberships.ToListAsync(Ct));
        Assert.True(await db.Memberships.IgnoreQueryFilters([TenantQueryFilter.Name]).AnyAsync(m => m.WorkspaceId == t.Beta.Id, Ct));
    }

    [Fact]
    public async Task Interceptor_AddForeignWorkspaceMembership_Throws()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var scope = await EnterAsync(t.Alpha.Id, t.AlphaOwner.ActorId);
        var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();

        db.Memberships.Add(Membership.Create(t.Beta.Id, t.Outsider.ActorId, WorkspaceRole.Owner, null, t.AlphaOwner.ActorId, TimeProvider.System));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        Assert.Contains("Tenant isolation violated", exception.Message, StringComparison.Ordinal);
        Assert.False(await BetaHasMemberAsync(t, t.Outsider.ActorId));
    }

    [Fact]
    public async Task Interceptor_ModifyOrDeleteForeignMembership_Throws()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var scope = await EnterAsync(t.Alpha.Id, t.AlphaOwner.ActorId);
        var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();
        var foreign = await db.Memberships.IgnoreQueryFilters([TenantQueryFilter.Name]).SingleAsync(m => m.WorkspaceId == t.Beta.Id && m.ActorId == t.BetaOwner.ActorId, Ct);

        foreign.ChangeRole(WorkspaceRole.Viewer, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));

        db.ChangeTracker.Clear();
        db.Attach(foreign);
        db.Memberships.Remove(foreign);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));

        Assert.True(await BetaHasMemberAsync(t, t.BetaOwner.ActorId));
    }

    [Fact]
    public async Task Interceptor_MoveMembershipToOtherWorkspace_Throws()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var scope = await EnterAsync(t.Alpha.Id, t.AlphaOwner.ActorId);
        var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();
        var own = await db.Memberships.SingleAsync(m => m.ActorId == t.RestrictedToAlpha.ActorId, Ct);

        db.Entry(own).Property(m => m.WorkspaceId).CurrentValue = t.Beta.Id;

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Interceptor_NoTenant_AddToExistingWorkspace_Throws()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();

        db.Memberships.Add(Membership.Create(t.Alpha.Id, t.Outsider.ActorId, WorkspaceRole.Viewer, null, t.AlphaOwner.ActorId, TimeProvider.System));

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Interceptor_AddedWithoutWorkspaceId_GetsCurrentTenant()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "fill");
        var newcomer = await factory.CreateAgentAsync(cancellationToken: Ct);
        await using var scope = await EnterAsync(workspace.Id, owner.ActorId);
        var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();

        var membership = Membership.Create(Guid.CreateVersion7(), newcomer.ActorId, WorkspaceRole.Viewer, null, owner.ActorId, TimeProvider.System);
        db.Memberships.Add(membership);
        // Simulates an entity created without a workspace id (the domain factory does not allow it).
        db.Entry(membership).Property(nameof(ITenantEntity.WorkspaceId)).CurrentValue = Guid.Empty;
        await db.SaveChangesAsync(Ct);

        Assert.Equal(workspace.Id, membership.WorkspaceId);
    }

    [Fact]
    public async Task Interceptor_ModifyOrDeleteForeignWorkspaceRoot_Throws()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var scope = await EnterAsync(t.Alpha.Id, t.AlphaOwner.ActorId);
        var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();
        var beta = await db.Workspaces.SingleAsync(w => w.Id == t.Beta.Id, Ct);

        beta.Rename("Hijacked");
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));

        db.ChangeTracker.Clear();
        db.Workspaces.Remove(await db.Workspaces.SingleAsync(w => w.Id == t.Beta.Id, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));

        await using var check = factory.Services.CreateAsyncScope();
        var stored = await check.ServiceProvider.GetRequiredService<AgcoworkDbContext>().Workspaces.AsNoTracking().SingleAsync(w => w.Id == t.Beta.Id, Ct);
        Assert.Equal(t.Beta.Name, stored.Name);
    }

    [Fact]
    public void Model_TenantTableIndexesStartWithWorkspaceId()
    {
        using var scope = factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>().Model;

        var tenantTypes = model.GetEntityTypes().Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType)).ToList();
        Assert.Contains(tenantTypes, t => t.ClrType == typeof(Membership));
        foreach (var entityType in tenantTypes)
        {
            Assert.NotEmpty(entityType.GetDeclaredQueryFilters());
            foreach (var index in entityType.GetIndexes())
            {
                // Documented exception: the convention FK index memberships.actor_id serves "my workspaces" (instance level).
                if (entityType.ClrType == typeof(Membership) && index.Properties is [{ Name: nameof(Membership.ActorId) }])
                {
                    continue;
                }

                // Documented exception (AP-024): the realtime dispatcher reads all workspaces forward by (xact_id, seq).
                if (entityType.ClrType == typeof(ActivityEvent) && index.Properties is [{ Name: nameof(ActivityEvent.XactId) }, { Name: nameof(ActivityEvent.Seq) }])
                {
                    continue;
                }

                Assert.Equal(nameof(ITenantEntity.WorkspaceId), index.Properties[0].Name);
            }
        }
    }

    /// <summary>
    /// The database catalog, not only the EF model: indexes created by raw SQL in migrations (full-text search, AP-026) must
    /// start with workspace_id too. Primary keys and the documented exceptions of the model test are skipped.
    /// </summary>
    [Fact]
    public async Task Database_TenantTableIndexesStartWithWorkspaceId()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();
        var tables = db.Model.GetEntityTypes()
            .Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType))
            .Select(t => t.GetTableName()!)
            .ToHashSet(StringComparer.Ordinal);
        string[] exceptions = ["ix_memberships_actor_id", "ix_activity_events_xact_id_seq"];

        var indexes = await db.Database
            .SqlQuery<CatalogIndex>($"""
                SELECT c.relname AS table_name, i.relname AS index_name, a.attname AS first_column
                FROM pg_index x
                JOIN pg_class c ON c.oid = x.indrelid
                JOIN pg_class i ON i.oid = x.indexrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = x.indkey[0]
                WHERE n.nspname = current_schema() AND NOT x.indisprimary
                """)
            .ToListAsync(Ct);

        var tenantIndexes = indexes.Where(i => tables.Contains(i.TableName) && !exceptions.Contains(i.IndexName)).ToList();
        Assert.Contains(tenantIndexes, i => i.IndexName == "ix_records_workspace_id_search_vector");
        Assert.All(tenantIndexes, i => Assert.True(i.FirstColumn == "workspace_id", $"{i.IndexName} starts with {i.FirstColumn}"));
    }

    private sealed class CatalogIndex
    {
        public string TableName { get; init; } = string.Empty;

        public string IndexName { get; init; } = string.Empty;

        public string FirstColumn { get; init; } = string.Empty;
    }

    private async Task<AsyncServiceScope> EnterAsync(Guid workspaceId, Guid actorId)
    {
        var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == workspaceId, Ct);
        var membership = await db.Memberships.IgnoreQueryFilters([TenantQueryFilter.Name]).AsNoTracking()
            .SingleAsync(m => m.WorkspaceId == workspaceId && m.ActorId == actorId, Ct);
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(
            new TenantContext(workspace.Id, workspace.Slug, workspace.IsArchived, membership, EffectiveScopes.Compute(membership.Scopes, new HashSet<string>())));
        return scope;
    }

    private async Task<bool> BetaHasMemberAsync(TenancyFixture t, Guid actorId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();
        return await db.Memberships.IgnoreQueryFilters([TenantQueryFilter.Name]).AnyAsync(m => m.WorkspaceId == t.Beta.Id && m.ActorId == actorId, Ct);
    }
}
