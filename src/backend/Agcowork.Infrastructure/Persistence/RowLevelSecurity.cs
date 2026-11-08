using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Agcowork.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL row-level security, the second net of tenant isolation behind the EF tenant filter (architecture §2a, step 5).
/// The only place (besides <see cref="DatabaseSessionInterceptor"/>) that may spell out role switches and the session settings;
/// an architecture test enforces that.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The application never works as the login it connects with (a superuser under Compose and Aspire, and superusers bypass
/// every policy): <see cref="DatabaseSessionInterceptor"/> switches each connection to <see cref="AppRole"/> when it opens and
/// keeps <see cref="WorkspaceSetting"/> and <see cref="ScopeSetting"/> in line with the tenant and the <c>InstanceAccess</c> of
/// the DI scope. Returning the connection to the pool (<c>DISCARD ALL</c>) resets all three. This needs one server session per
/// open connection: no Npgsql multiplexing (<see cref="EnsureSupported"/>), no PgBouncer in transaction or statement mode.</item>
/// <item>Every tenant table has two policies (<see cref="EnableTenantPolicy"/>): <see cref="TenantPolicyName"/> lets every command
/// see and write only rows of the session's workspace (nothing without one: fail closed), <see cref="InstancePolicyName"/> lets
/// the instance area read, never write, every row.</item>
/// <item>Migrations and <c>dotnet ef</c> run as the login (owner) without switching
/// (<see cref="AgcoworkDbContextOptions.ConfigureOwner"/>). Default privileges grant <see cref="AppRole"/> DML on tables that
/// later migrations create; a new tenant table still needs <see cref="EnableTenantPolicy"/> in its migration. Catalog tests
/// (<c>RowLevelSecurityCatalogTests</c>) fail otherwise. Every start re-applies role, privileges and LEAKPROOF marks
/// (<see cref="RepairAsync"/>).</item>
/// </list>
/// </remarks>
internal static class RowLevelSecurity
{
    /// <summary>Role the application works as: no superuser, no BYPASSRLS, DML on all tables (migration <c>AddRowLevelSecurity</c>).</summary>
    public const string AppRole = "agcowork_app";

    /// <summary>Session setting with the tenant's workspace id; empty without a tenant.</summary>
    public const string WorkspaceSetting = "agcowork.workspace_id";

    /// <summary>Session setting that is <see cref="InstanceScope"/> inside an instance block, empty otherwise.</summary>
    public const string ScopeSetting = "agcowork.scope";

    /// <summary>Value of <see cref="ScopeSetting"/> in the instance area.</summary>
    public const string InstanceScope = "instance";

    /// <summary>Policy for all commands: rows of the session's workspace only.</summary>
    public const string TenantPolicyName = "tenant_isolation";

    /// <summary>Policy for SELECT only: every row inside the instance area.</summary>
    public const string InstancePolicyName = "instance_read";

    /// <summary>
    /// Condition of <see cref="TenantPolicyName"/> (USING and WITH CHECK). <c>current_setting(…, true)</c> yields NULL for a
    /// setting that was never set and '' for one that was reset; <c>nullif</c> keeps both from failing the uuid cast.
    /// </summary>
    public const string TenantCondition = $"workspace_id = nullif(current_setting('{WorkspaceSetting}', true), '')::uuid";

    /// <summary>Condition of <see cref="InstancePolicyName"/> (USING, SELECT only).</summary>
    public const string InstanceReadCondition = $"current_setting('{ScopeSetting}', true) = '{InstanceScope}'";

    /// <summary>
    /// The statement that puts a session into the state of a context: role <see cref="AppRole"/>, workspace and scope as
    /// parameters <c>@agcowork_workspace_id</c> and <c>@agcowork_scope</c> (session-level, undone by a rollback).
    /// </summary>
    public const string SetSessionSql =
        $"SELECT set_config('role', '{AppRole}', false), " +
        $"set_config('{WorkspaceSetting}', @agcowork_workspace_id, false), " +
        $"set_config('{ScopeSetting}', @agcowork_scope, false)";

    /// <summary>
    /// Role and privileges, idempotent: run by the migration <c>AddRowLevelSecurity</c> and again on every start
    /// (<see cref="RepairAsync"/>). Roles belong to the cluster, so the role may exist already (other databases, tests); then
    /// only its attributes are set. The login of the connection string becomes a member, so it may switch to the role. DML on
    /// every table, sequences (activity_events.seq) and functions (search); the migration history stays with the owner.
    /// Default privileges cover what later migrations of the same owner create.
    /// </summary>
    public static readonly IReadOnlyList<string> RoleAndPrivilegesSql =
    [
        $"""
        DO $$
        BEGIN
          IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{AppRole}') THEN
            CREATE ROLE {AppRole} NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION;
          ELSE
            ALTER ROLE {AppRole} NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION;
          END IF;
        END
        $$;
        """,
        $"GRANT {AppRole} TO CURRENT_USER;",
        $"GRANT USAGE ON SCHEMA public TO {AppRole};",
        $"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {AppRole};",
        $"REVOKE ALL ON TABLE __ef_migrations_history FROM {AppRole};",
        $"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {AppRole};",
        $"GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO {AppRole};",
        $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {AppRole};",
        $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO {AppRole};",
        $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO {AppRole};",
    ];

    /// <summary>
    /// Index use under row-level security. On a table with a policy PostgreSQL turns a condition into an index condition only
    /// if its operator function is LEAKPROOF; otherwise it filters after the policy. Equality and comparisons of uuid, text,
    /// numbers and timestamps are leakproof, but JSONB containment (@&gt;, GIN on properties: Filter-DSL) and full-text match
    /// (@@, GIN on search_vector: search) are not: the search would read every record of the workspace. Neither function raises
    /// errors that depend on the row data, and the policies guard against programming errors, not against hostile SQL
    /// (architecture §2a). Only a superuser may mark them; elsewhere the queries stay correct, just slower (warning). pg_proc is
    /// per database: this affects this database only. A major-version upgrade (pg_upgrade) recreates the built-in functions
    /// without the mark; the next start marks them again.
    /// </summary>
    public const string LeakproofSql = """
        DO $$
        BEGIN
          IF (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN
            ALTER FUNCTION pg_catalog.jsonb_contains(jsonb, jsonb) LEAKPROOF;
            ALTER FUNCTION pg_catalog.ts_match_vq(tsvector, tsquery) LEAKPROOF;
          ELSE
            RAISE WARNING 'agcowork: % is no superuser and cannot mark jsonb_contains and ts_match_vq LEAKPROOF; JSONB filters and full-text search will not use their GIN indexes under row-level security.', current_user;
          END IF;
        END
        $$;
        """;

    /// <summary>Reverses <see cref="LeakproofSql"/> (migration Down).</summary>
    public const string NotLeakproofSql = """
        DO $$
        BEGIN
          IF (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN
            ALTER FUNCTION pg_catalog.jsonb_contains(jsonb, jsonb) NOT LEAKPROOF;
            ALTER FUNCTION pg_catalog.ts_match_vq(tsvector, tsquery) NOT LEAKPROOF;
          END IF;
        END
        $$;
        """;

    /// <summary>Turns on row-level security with both policies for <paramref name="table"/> (a table with <c>workspace_id</c>).</summary>
    public static void EnableTenantPolicy(this MigrationBuilder migrationBuilder, string table)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
        migrationBuilder.Sql($"CREATE POLICY {TenantPolicyName} ON {table} FOR ALL USING ({TenantCondition}) WITH CHECK ({TenantCondition});");
        migrationBuilder.Sql($"CREATE POLICY {InstancePolicyName} ON {table} FOR SELECT USING ({InstanceReadCondition});");
    }

    /// <summary>Reverses <see cref="EnableTenantPolicy"/>.</summary>
    public static void DisableTenantPolicy(this MigrationBuilder migrationBuilder, string table)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        migrationBuilder.Sql($"DROP POLICY IF EXISTS {InstancePolicyName} ON {table};");
        migrationBuilder.Sql($"DROP POLICY IF EXISTS {TenantPolicyName} ON {table};");
        migrationBuilder.Sql($"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
    }

    /// <summary>
    /// Throws for connection strings under which a session setting would not stay with its connection: Npgsql multiplexing
    /// sends commands of many connections over shared server sessions. (PgBouncer in transaction or statement mode does the same
    /// and cannot be detected here: do not use it.)
    /// </summary>
    public static void EnsureSupported(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        if (new NpgsqlConnectionStringBuilder(connectionString).Multiplexing)
        {
            throw new InvalidOperationException(
                "Multiplexing=true is not supported: row-level security keeps role and tenant in the server session of each "
                + "connection, which multiplexing shares between connections. Remove Multiplexing from the connection string.");
        }
    }

    /// <summary>
    /// Re-applies <see cref="RoleAndPrivilegesSql"/> and <see cref="LeakproofSql"/> as the owner, on every start after the
    /// migrations: repairs privileges lost to manual changes, restores and pg_upgrade. Logs a warning when the GIN operators are
    /// still not LEAKPROOF afterwards (owner is no superuser).
    /// </summary>
    public static async Task RepairAsync(DbContext ownerDb, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownerDb);
        ArgumentNullException.ThrowIfNull(logger);
        foreach (var sql in RoleAndPrivilegesSql.Append(LeakproofSql))
        {
            await ownerDb.Database.ExecuteSqlRawAsync(sql, cancellationToken).ConfigureAwait(false);
        }

        var leakproof = await ownerDb.Database.SqlQueryRaw<bool>("""
            SELECT bool_and(proleakproof) AS "Value" FROM pg_proc
            WHERE oid IN ('pg_catalog.jsonb_contains(jsonb, jsonb)'::regprocedure, 'pg_catalog.ts_match_vq(tsvector, tsquery)'::regprocedure)
            """).SingleAsync(cancellationToken).ConfigureAwait(false);
        if (!leakproof)
        {
            LogNotLeakproof(logger);
        }
    }

    private static readonly Action<ILogger, Exception?> NotLeakproof = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(3601, "RowLevelSecurityNotLeakproof"),
        "jsonb_contains and ts_match_vq are not LEAKPROOF (the database owner is no superuser): under row-level security, JSONB "
        + "filters and full-text search cannot use their GIN indexes and read every record of the workspace. Mark them as a "
        + "superuser: ALTER FUNCTION pg_catalog.jsonb_contains(jsonb, jsonb) LEAKPROOF; ALTER FUNCTION pg_catalog.ts_match_vq(tsvector, tsquery) LEAKPROOF;");

    private static void LogNotLeakproof(ILogger logger) => NotLeakproof(logger, null);
}
