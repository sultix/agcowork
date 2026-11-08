using Agcowork.Domain.Common;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agcowork.Api.Tests.Tenancy;

/// <summary>
/// The database catalog after all migrations (AP-036): a table that a later migration adds without policy or privileges fails
/// here. Rule for new tables: a tenant table (an <see cref="ITenantEntity"/>) calls <c>EnableTenantPolicy</c> in its migration;
/// privileges come from the default privileges as long as the same owner runs the migrations.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class RowLevelSecurityCatalogTests(AgcoworkApiFactory factory)
{
    private const string MigrationHistory = "__ef_migrations_history";

    // Tables with a workspace_id column that are no tenant tables: the key restriction of an instance-level API key.
    private static readonly string[] WorkspaceColumnWithoutTenant = ["api_keys"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TenantTables_HaveRowLevelSecurityAndTheTenantPolicy()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = Db(scope);
        var tenantTables = TenantTables(db);
        var tables = await TablesAsync(db);
        var policies = await db.Database.SqlQueryRaw<PolicyRow>("""
            SELECT tablename AS table_name, policyname AS policy_name, permissive, cmd AS command, coalesce(qual, '') AS using_expression,
                   coalesce(with_check, '') AS check_expression
            FROM pg_policies WHERE schemaname = current_schema()
            """).ToListAsync(Ct);

        Assert.Contains("records", tenantTables);
        Assert.Contains("mentions", tenantTables);
        foreach (var table in tenantTables)
        {
            var row = Assert.Single(tables, t => t.TableName == table);
            Assert.True(row.RowSecurity, $"{table}: row-level security is off");
            var own = policies.Where(p => p.TableName == table).OrderBy(p => p.PolicyName, StringComparer.Ordinal).ToList();
            Assert.Equal([RowLevelSecurity.InstancePolicyName, RowLevelSecurity.TenantPolicyName], own.Select(p => p.PolicyName));
            Assert.All(own, p => Assert.Equal("PERMISSIVE", p.Permissive));

            // Tenant: every command, rows of the session's workspace only, nothing about the instance area.
            var tenant = own[1];
            Assert.Equal("ALL", tenant.Command);
            foreach (var expression in new[] { tenant.UsingExpression, tenant.CheckExpression })
            {
                Assert.Equal("(workspace_id = (NULLIF(current_setting('agcowork.workspace_id'::text, true), ''::text))::uuid)", expression);
            }

            // Instance area: reads only; a write never passes it (no other policy applies to INSERT/UPDATE/DELETE).
            var instance = own[0];
            Assert.Equal("SELECT", instance.Command);
            Assert.Equal("(current_setting('agcowork.scope'::text, true) = 'instance'::text)", instance.UsingExpression);
            Assert.Equal(string.Empty, instance.CheckExpression);
        }
    }

    [Fact]
    public async Task InstanceTables_HaveNoRowLevelSecurity_AndEveryWorkspaceColumnIsATenantTable()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = Db(scope);
        var tenantTables = TenantTables(db);
        var tables = await TablesAsync(db);

        Assert.Contains(tables, t => t.TableName == "actors");
        Assert.All(tables.Where(t => !tenantTables.Contains(t.TableName)), t => Assert.False(t.RowSecurity, $"{t.TableName}: unexpected row-level security"));
        Assert.All(tables.Where(t => t.HasWorkspaceId && !WorkspaceColumnWithoutTenant.Contains(t.TableName)), t =>
            Assert.True(tenantTables.Contains(t.TableName), $"{t.TableName} has workspace_id but is no tenant entity: map it as ITenantEntity with a policy, or list it here"));
    }

    [Fact]
    public async Task AppRole_HasDmlOnEveryTable_NothingOnTheMigrationHistory()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = Db(scope);
        var privileges = await db.Database.SqlQueryRaw<PrivilegeRow>($"""
            SELECT c.relname AS table_name,
                   has_table_privilege('{RowLevelSecurity.AppRole}', c.oid, 'SELECT') AS can_select,
                   has_table_privilege('{RowLevelSecurity.AppRole}', c.oid, 'INSERT') AS can_insert,
                   has_table_privilege('{RowLevelSecurity.AppRole}', c.oid, 'UPDATE') AS can_update,
                   has_table_privilege('{RowLevelSecurity.AppRole}', c.oid, 'DELETE') AS can_delete
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema() AND c.relkind IN ('r', 'p')
            """).ToListAsync(Ct);

        Assert.Contains(privileges, p => p.TableName == "records");
        Assert.All(privileges.Where(p => p.TableName != MigrationHistory), p =>
            Assert.True(p.CanSelect && p.CanInsert && p.CanUpdate && p.CanDelete, $"{p.TableName}: {RowLevelSecurity.AppRole} lacks DML privileges"));
        var history = Assert.Single(privileges, p => p.TableName == MigrationHistory);
        Assert.False(history.CanSelect || history.CanInsert || history.CanUpdate || history.CanDelete);
    }

    [Fact]
    public async Task AppRole_UsesEverySequenceAndFunction_NoFunctionIsSecurityDefiner()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = Db(scope);
        var sequences = await db.Database.SqlQueryRaw<NamedCheck>($"""
            SELECT c.relname AS name, has_sequence_privilege('{RowLevelSecurity.AppRole}', c.oid, 'USAGE') AS ok
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema() AND c.relkind = 'S'
            """).ToListAsync(Ct);
        var functions = await db.Database.SqlQueryRaw<NamedCheck>($"""
            SELECT p.proname AS name, has_function_privilege('{RowLevelSecurity.AppRole}', p.oid, 'EXECUTE') AND NOT p.prosecdef AS ok
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = current_schema()
            """).ToListAsync(Ct);

        Assert.Contains(sequences, s => s.Name.StartsWith("activity_events_seq", StringComparison.Ordinal));
        Assert.All(sequences, s => Assert.True(s.Ok, $"sequence {s.Name}: no USAGE for {RowLevelSecurity.AppRole}"));
        Assert.Contains(functions, f => f.Name == "agcowork_records_search_vector");
        Assert.All(functions, f => Assert.True(f.Ok, $"function {f.Name}: no EXECUTE for {RowLevelSecurity.AppRole}, or SECURITY DEFINER"));
    }

    [Fact]
    public async Task AppRole_NoLoginNoSuperuserNoBypass_DefaultPrivilegesForLaterTables()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = Db(scope);

        var role = await db.Database.SqlQueryRaw<RoleRow>($"""
            SELECT rolsuper AS superuser, rolbypassrls AS bypass_rls, rolcanlogin AS can_login, rolcreaterole AS create_role, rolcreatedb AS create_db
            FROM pg_roles WHERE rolname = '{RowLevelSecurity.AppRole}'
            """).SingleAsync(Ct);
        var defaults = await db.Database.SqlQueryRaw<string>($"""
            SELECT d.defaclobjtype::text || ':' || a.privilege_type AS "Value"
            FROM pg_default_acl d, aclexplode(d.defaclacl) a
            JOIN pg_roles r ON r.oid = a.grantee
            WHERE r.rolname = '{RowLevelSecurity.AppRole}'
            """).ToListAsync(Ct);

        Assert.False(role.Superuser || role.BypassRls || role.CanLogin || role.CreateRole || role.CreateDb);
        Assert.Contains("r:SELECT", defaults);
        Assert.Contains("r:INSERT", defaults);
        Assert.Contains("r:UPDATE", defaults);
        Assert.Contains("r:DELETE", defaults);
        Assert.Contains("S:USAGE", defaults);
        Assert.Contains("f:EXECUTE", defaults);
    }

    /// <summary>
    /// Without LEAKPROOF, PostgreSQL may not use JSONB containment and full-text match as index conditions on tables with a
    /// policy: Filter-DSL and search would scan every record of the workspace (migration AddRowLevelSecurity).
    /// </summary>
    [Fact]
    public async Task GinOperators_AreLeakproof()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var leakproof = await Db(scope).Database.SqlQueryRaw<NamedCheck>("""
            SELECT p.oid::regprocedure::text AS name, p.proleakproof AS ok FROM pg_proc p
            WHERE p.oid IN ('pg_catalog.jsonb_contains(jsonb, jsonb)'::regprocedure, 'pg_catalog.ts_match_vq(tsvector, tsquery)'::regprocedure)
            """).ToListAsync(Ct);

        Assert.Equal(2, leakproof.Count);
        Assert.All(leakproof, f => Assert.True(f.Ok, $"{f.Name} is not LEAKPROOF"));
    }

    private static AgcoworkDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();

    private static HashSet<string> TenantTables(AgcoworkDbContext db) =>
        db.Model.GetEntityTypes()
            .Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType))
            .Select(t => t.GetTableName()!)
            .ToHashSet(StringComparer.Ordinal);

    private static Task<List<TableRow>> TablesAsync(AgcoworkDbContext db) =>
        db.Database.SqlQueryRaw<TableRow>("""
            SELECT c.relname AS table_name, c.relrowsecurity AS row_security,
                   EXISTS (SELECT FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attname = 'workspace_id' AND NOT a.attisdropped) AS has_workspace_id
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema() AND c.relkind IN ('r', 'p')
            """).ToListAsync(Ct);

    private sealed class TableRow
    {
        public string TableName { get; init; } = string.Empty;

        public bool RowSecurity { get; init; }

        public bool HasWorkspaceId { get; init; }
    }

    private sealed class PolicyRow
    {
        public string TableName { get; init; } = string.Empty;

        public string PolicyName { get; init; } = string.Empty;

        public string Permissive { get; init; } = string.Empty;

        public string Command { get; init; } = string.Empty;

        public string UsingExpression { get; init; } = string.Empty;

        public string CheckExpression { get; init; } = string.Empty;
    }

    private sealed class PrivilegeRow
    {
        public string TableName { get; init; } = string.Empty;

        public bool CanSelect { get; init; }

        public bool CanInsert { get; init; }

        public bool CanUpdate { get; init; }

        public bool CanDelete { get; init; }
    }

    private sealed class NamedCheck
    {
        public string Name { get; init; } = string.Empty;

        public bool Ok { get; init; }
    }

    private sealed class RoleRow
    {
        public bool Superuser { get; init; }

        public bool BypassRls { get; init; }

        public bool CanLogin { get; init; }

        public bool CreateRole { get; init; }

        public bool CreateDb { get; init; }
    }
}
