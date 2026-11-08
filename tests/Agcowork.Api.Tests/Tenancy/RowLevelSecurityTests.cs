using System.Collections.Concurrent;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Workspaces;
using Agcowork.Domain.Workspaces;
using Agcowork.Infrastructure;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Agcowork.Api.Tests.Tenancy;

/// <summary>
/// Row-level security as the second net (AP-036, architecture §2a step 5): code that forgets the EF tenant filter
/// (<c>IgnoreQueryFilters</c>, raw SQL, <c>ExecuteUpdate</c>) still sees and changes only rows of the tenant, nothing without a
/// tenant, and the session settings never leak through the connection pool.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class RowLevelSecurityTests(AgcoworkApiFactory factory)
{
    private static readonly ConcurrentDictionary<AgcoworkApiFactory, Lazy<Task<Pair>>> Pairs = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IgnoreQueryFilters_InTenant_SeesNothingOfOtherWorkspace()
    {
        var p = await PairAsync();
        await using var scope = await EnterAsync(p.First);
        var db = Db(scope);

        var collections = await db.Collections.IgnoreQueryFilters([TenantQueryFilter.Name]).AsNoTracking().ToListAsync(Ct);
        var memberships = await db.Memberships.IgnoreQueryFilters([TenantQueryFilter.Name]).AsNoTracking().ToListAsync(Ct);

        Assert.NotEmpty(collections);
        Assert.All(collections, c => Assert.Equal(p.First.Workspace.Id, c.WorkspaceId));
        Assert.All(memberships, m => Assert.Equal(p.First.Workspace.Id, m.WorkspaceId));
        Assert.False(await db.ActivityEvents.IgnoreQueryFilters([TenantQueryFilter.Name]).AnyAsync(e => e.WorkspaceId == p.Second.Workspace.Id, Ct));
        Assert.False(await db.Spaces.IgnoreQueryFilters([TenantQueryFilter.Name]).AnyAsync(s => s.WorkspaceId == p.Second.Workspace.Id, Ct));
    }

    [Theory]
    [InlineData("memberships")]
    [InlineData("spaces")]
    [InlineData("collections")]
    [InlineData("views")]
    [InlineData("activity_events")]
    public async Task RawSql_WithoutWorkspaceCondition_SeesOnlyTenantRows(string table)
    {
        var p = await PairAsync();
        await using var scope = await EnterAsync(p.First);

        var workspaces = await WorkspaceIdsAsync(Db(scope), table);

        Assert.Equal([p.First.Workspace.Id], workspaces);
    }

    [Fact]
    public async Task RawUpdateAndDelete_OtherWorkspace_ChangeNothing()
    {
        var p = await PairAsync();
        await using var scope = await EnterAsync(p.First);
        var db = Db(scope);
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);

        var updated = await db.Database.ExecuteSqlAsync($"UPDATE collections SET name = 'hijacked' WHERE workspace_id = {p.Second.Workspace.Id}", Ct);
        var bulkUpdated = await db.Spaces.IgnoreQueryFilters([TenantQueryFilter.Name])
            .Where(s => s.WorkspaceId == p.Second.Workspace.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "hijacked"), Ct);
        var deleted = await db.Database.ExecuteSqlAsync($"DELETE FROM memberships WHERE workspace_id = {p.Second.Workspace.Id}", Ct);

        Assert.Equal(0, updated);
        Assert.Equal(0, bulkUpdated);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task RawInsert_ForeignWorkspaceId_RejectedByPolicy()
    {
        var p = await PairAsync();
        await using var scope = await EnterAsync(p.First);
        var db = Db(scope);
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync($"""
            INSERT INTO memberships (id, workspace_id, actor_id, role, scopes, created_by, created_at)
            VALUES ({Guid.CreateVersion7()}, {p.Second.Workspace.Id}, {p.First.Owner}, 'owner', ARRAY[]::text[], {p.First.Owner}, now())
            """, Ct));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        Assert.Contains("row-level security", exception.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawUpdate_MovingRowToForeignWorkspace_RejectedByPolicy()
    {
        var p = await PairAsync();
        await using var scope = await EnterAsync(p.First);
        var db = Db(scope);
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"UPDATE spaces SET workspace_id = {p.Second.Workspace.Id} WHERE workspace_id = {p.First.Workspace.Id}", Ct));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [Fact]
    public async Task NoTenantNoInstanceArea_TenantTablesEmpty_InstanceTablesReadable()
    {
        await PairAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = Db(scope);

        Assert.Empty(await WorkspaceIdsAsync(db, "memberships"));
        Assert.Empty(await WorkspaceIdsAsync(db, "collections"));
        Assert.Equal(0, await db.Memberships.IgnoreQueryFilters([TenantQueryFilter.Name]).CountAsync(Ct));
        Assert.True(await db.Actors.AnyAsync(Ct));
        Assert.True(await db.Workspaces.AnyAsync(Ct));
    }

    [Fact]
    public async Task InstanceArea_SeesEveryWorkspace_OnlyWhileOpen()
    {
        var p = await PairAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = Db(scope);
        var instance = scope.ServiceProvider.GetRequiredService<InstanceAccess>();

        IReadOnlyList<Guid> inside;
        using (instance.Enter("test: outer"))
        {
            using (instance.Enter("test: nested"))
            {
                Assert.True(instance.IsActive);
            }

            Assert.True(instance.IsActive);
            inside = await WorkspaceIdsAsync(db, "collections");
        }

        Assert.False(instance.IsActive);
        Assert.Contains(p.First.Workspace.Id, inside);
        Assert.Contains(p.Second.Workspace.Id, inside);
        Assert.Empty(await WorkspaceIdsAsync(db, "collections"));
    }

    [Fact]
    public async Task Session_RunsAsApplicationRole_WithTenantSetting()
    {
        var p = await PairAsync();
        await using var scope = await EnterAsync(p.First);

        var session = await SessionAsync(Db(scope));

        Assert.Equal(RowLevelSecurity.AppRole, session.CurrentUser);
        Assert.NotEqual(RowLevelSecurity.AppRole, session.SessionUser);
        Assert.Equal(p.First.Workspace.Id.ToString(), session.Workspace);
        Assert.Equal(string.Empty, session.Scope);
    }

    [Fact]
    public async Task Session_TenantSetInsideOpenTransaction_AppliesToNextCommand()
    {
        var p = await PairAsync();
        var tenant = await TenantOfAsync(p.First);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = Db(scope);
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        Assert.Empty(await WorkspaceIdsAsync(db, "memberships"));

        // Like CreateWorkspace: the tenant arrives while the connection and its transaction are open.
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(tenant);

        Assert.Equal([p.First.Workspace.Id], await WorkspaceIdsAsync(db, "memberships"));
        await transaction.CommitAsync(Ct);
        Assert.Equal([p.First.Workspace.Id], await WorkspaceIdsAsync(db, "memberships"));
    }

    [Theory]
    [InlineData("rollback")]
    [InlineData("dispose")]
    [InlineData("savepoint")]
    [InlineData("dispose-new-transaction")]
    public async Task Session_SettingUndoneByRollback_AppliedAgainBeforeNextCommand(string end)
    {
        var p = await PairAsync();
        var first = await TenantOfAsync(p.First);
        var second = await TenantOfAsync(p.Second);
        var tenant = new SwitchableTenant { Current = first };
        await using var db = OpenContext(ConnectionString(), tenant);
        await db.Database.OpenConnectionAsync(Ct);
        Assert.Equal([p.First.Workspace.Id], await WorkspaceIdsAsync(db, "memberships"));

        var transaction = await db.Database.BeginTransactionAsync(Ct);
        if (end == "savepoint")
        {
            await transaction.CreateSavepointAsync("before_switch", Ct);
        }

        tenant.Current = second;
        Assert.Equal([p.Second.Workspace.Id], await WorkspaceIdsAsync(db, "memberships"));

        // PostgreSQL resets the setting to the first workspace here; the context still wants the second one.
        switch (end)
        {
            case "rollback":
                await transaction.RollbackAsync(Ct);
                await transaction.DisposeAsync();
                break;
            case "dispose":
                await transaction.DisposeAsync();
                break;
            case "dispose-new-transaction":
                // Npgsql hands out the same transaction object again: only the transaction start tells the two apart.
                await transaction.DisposeAsync();
                transaction = await db.Database.BeginTransactionAsync(Ct);
                break;
            default:
                await transaction.RollbackToSavepointAsync("before_switch", Ct);
                break;
        }

        Assert.Equal([p.Second.Workspace.Id], await WorkspaceIdsAsync(db, "memberships"));
        Assert.Equal(p.Second.Workspace.Id.ToString(), (await SessionAsync(db)).Workspace);
        if (end is "savepoint" or "dispose-new-transaction")
        {
            await transaction.DisposeAsync();
        }
    }

    [Fact]
    public async Task Session_InstanceBlockOutlivesDiscardedTransaction_NextTransactionNotInInstanceArea()
    {
        var p = await PairAsync();
        var instance = new InstanceAccess();
        await using var db = OpenContext(ConnectionString(), new SwitchableTenant { Current = await TenantOfAsync(p.First) }, instance);
        await db.Database.OpenConnectionAsync(Ct);

        // Instance area applied outside a transaction: it stays in the session.
        var block = instance.Enter("test: instance area before the transaction");
        Assert.Contains(p.Second.Workspace.Id, await WorkspaceIdsAsync(db, "memberships"));

        // Inside a transaction the block ends: the setting is taken back there, and undone again by the discarded transaction.
        var first = await db.Database.BeginTransactionAsync(Ct);
        block.Dispose();
        Assert.Equal([p.First.Workspace.Id], await WorkspaceIdsAsync(db, "memberships"));
        await first.DisposeAsync();

        await using var second = await db.Database.BeginTransactionAsync(Ct);
        Assert.Equal([p.First.Workspace.Id], await WorkspaceIdsAsync(db, "memberships"));
        Assert.Equal(string.Empty, (await SessionAsync(db)).Scope);
    }

    [Fact]
    public async Task InstanceArea_ReadsOnly_WritesToForeignRowsFail()
    {
        var p = await PairAsync();
        await using var scope = await EnterAsync(p.First);
        var db = Db(scope);
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        using var instance = scope.ServiceProvider.GetRequiredService<InstanceAccess>().Enter("test: try to write across workspaces");
        Assert.Contains(p.Second.Workspace.Id, await WorkspaceIdsAsync(db, "collections"));

        var updated = await db.Database.ExecuteSqlAsync($"UPDATE collections SET name = 'hijacked' WHERE workspace_id = {p.Second.Workspace.Id}", Ct);
        var bulkUpdated = await db.Spaces.IgnoreQueryFilters([TenantQueryFilter.Name])
            .Where(s => s.WorkspaceId == p.Second.Workspace.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "hijacked"), Ct);
        var deleted = await db.Database.ExecuteSqlAsync($"DELETE FROM memberships WHERE workspace_id = {p.Second.Workspace.Id}", Ct);
        Assert.Equal(0, updated);
        Assert.Equal(0, bulkUpdated);
        Assert.Equal(0, deleted);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync($"""
            INSERT INTO memberships (id, workspace_id, actor_id, role, scopes, created_by, created_at)
            VALUES ({Guid.CreateVersion7()}, {p.Second.Workspace.Id}, {p.First.Owner}, 'owner', ARRAY[]::text[], {p.First.Owner}, now())
            """, Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [Fact]
    public void Configure_MultiplexingConnectionString_Refused()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString()) { Multiplexing = true }.ConnectionString;

        var exception = Assert.Throws<InvalidOperationException>(() => OpenContext(connectionString, new SwitchableTenant()));

        Assert.Contains("Multiplexing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_MultiplexingConnectionString_AbortsBeforeMigrating()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString()) { Multiplexing = true }.ConnectionString;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"ConnectionStrings:{DependencyInjection.ConnectionStringName}"] = connectionString })
            .Build();
        await using var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => services.ApplyMigrationsAsync(Ct));

        Assert.Contains("Multiplexing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pool_SamePhysicalConnection_NothingLeaksToTheNextUser()
    {
        var p = await PairAsync();
        var first = await TenantOfAsync(p.First);
        var second = await TenantOfAsync(p.Second);

        // A pool of exactly one connection: every context below gets the same server process.
        var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString())
        {
            MaxPoolSize = 1,
            ApplicationName = $"rls-pool-{Guid.NewGuid():N}",
        }.ConnectionString;

        int firstPid;
        await using (var db = OpenContext(connectionString, new SwitchableTenant { Current = first }))
        {
            firstPid = (await SessionAsync(db)).Pid;
            Assert.Equal([p.First.Workspace.Id], await WorkspaceIdsAsync(db, "memberships"));
        }

        await using (var db = OpenContext(connectionString, new SwitchableTenant { Current = second }))
        {
            Assert.Equal(firstPid, (await SessionAsync(db)).Pid);
            Assert.Equal([p.Second.Workspace.Id], await WorkspaceIdsAsync(db, "memberships"));
        }

        var instance = new InstanceAccess();
        await using (var db = OpenContext(connectionString, new SwitchableTenant(), instance))
        {
            using var open = instance.Enter("test: instance area on the pooled connection");
            Assert.Equal(firstPid, (await SessionAsync(db)).Pid);
            Assert.Contains(p.Second.Workspace.Id, await WorkspaceIdsAsync(db, "memberships"));
        }

        await using (var db = OpenContext(connectionString, new SwitchableTenant()))
        {
            Assert.Equal(firstPid, (await SessionAsync(db)).Pid);
            Assert.Empty(await WorkspaceIdsAsync(db, "memberships"));
        }

        // Past EF on the same pooled connection: Npgsql's reset (DISCARD ALL) took role and settings back.
        await using var raw = new NpgsqlConnection(connectionString);
        await raw.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            $"SELECT pg_backend_pid(), current_user::text, coalesce(current_setting('{RowLevelSecurity.WorkspaceSetting}', true), ''), coalesce(current_setting('{RowLevelSecurity.ScopeSetting}', true), '')",
            raw);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal(firstPid, reader.GetInt32(0));
        Assert.NotEqual(RowLevelSecurity.AppRole, reader.GetString(1));
        Assert.Equal(string.Empty, reader.GetString(2));
        Assert.Equal(string.Empty, reader.GetString(3));
    }

    private static AgcoworkDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();

    private string ConnectionString() =>
        factory.Services.GetRequiredService<IConfiguration>().GetConnectionString(DependencyInjection.ConnectionStringName)!;

    private static AgcoworkDbContext OpenContext(string connectionString, ITenantContextAccessor tenant, InstanceAccess? instance = null)
    {
        var options = new DbContextOptionsBuilder<AgcoworkDbContext>();
        AgcoworkDbContextOptions.Configure(options, connectionString);
        return new AgcoworkDbContext(options.Options, tenant, instance);
    }

    /// <summary>Distinct workspace ids of <paramref name="table"/>, read with raw SQL (no EF filter at all).</summary>
    private static async Task<List<Guid>> WorkspaceIdsAsync(AgcoworkDbContext db, string table)
    {
        // Table names come from this class only (no input); checked anyway before they become SQL text.
        Assert.Matches("^[a-z_]+$", table);
        var sql = "SELECT DISTINCT workspace_id AS \"Value\" FROM " + table;
        return await db.Database.SqlQueryRaw<Guid>(sql).OrderBy(id => id).ToListAsync(Ct);
    }

    private static async Task<SessionRow> SessionAsync(AgcoworkDbContext db) =>
        await db.Database.SqlQueryRaw<SessionRow>($"""
            SELECT pg_backend_pid() AS pid, current_user::text AS "current_user", session_user::text AS "session_user",
                   coalesce(current_setting('{RowLevelSecurity.WorkspaceSetting}', true), '') AS workspace,
                   coalesce(current_setting('{RowLevelSecurity.ScopeSetting}', true), '') AS scope
            """).SingleAsync(Ct);

    private async Task<AsyncServiceScope> EnterAsync(Member member)
    {
        var tenant = await TenantOfAsync(member);
        var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(tenant);
        return scope;
    }

    /// <summary>The owner's tenant context, resolved like the middleware does (ResolveTenant, which uses the instance area).</summary>
    private async Task<TenantContext> TenantOfAsync(Member member)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = Db(scope);
        Membership membership;
        using (scope.ServiceProvider.GetRequiredService<InstanceAccess>().Enter("test: membership before the tenant exists"))
        {
            membership = await db.Memberships.IgnoreQueryFilters([TenantQueryFilter.Name]).AsNoTracking()
                .SingleAsync(m => m.WorkspaceId == member.Workspace.Id && m.ActorId == member.Owner, Ct);
        }

        return new TenantContext(member.Workspace.Id, member.Workspace.Slug, false, membership, EffectiveScopes.Compute(membership.Scopes, new HashSet<string>()));
    }

    /// <summary>Two workspaces from the software project template (spaces, collections, views, activity), one owner each.</summary>
    private Task<Pair> PairAsync() =>
        Pairs.GetOrAdd(factory, f => new Lazy<Task<Pair>>(async () =>
        {
            var firstOwner = await f.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
            var secondOwner = await f.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
            var first = await TenancyFixture.CreateWorkspaceAsync(firstOwner.Client, "rls-a", "software-projekt");
            var second = await TenancyFixture.CreateWorkspaceAsync(secondOwner.Client, "rls-b", "software-projekt");
            return new Pair(new Member(first, firstOwner.ActorId), new Member(second, secondOwner.ActorId));
        })).Value;

    private sealed record Member(WorkspaceDto Workspace, Guid Owner);

    private sealed record Pair(Member First, Member Second);

    private sealed class SwitchableTenant : ITenantContextAccessor
    {
        public ITenantContext? Current { get; set; }

        public void SetTenant(ITenantContext tenant) => Current = tenant;
    }

    private sealed class SessionRow
    {
        public int Pid { get; init; }

        public string CurrentUser { get; init; } = string.Empty;

        public string SessionUser { get; init; } = string.Empty;

        public string Workspace { get; init; } = string.Empty;

        public string Scope { get; init; } = string.Empty;
    }
}
