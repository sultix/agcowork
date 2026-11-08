using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agcowork.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Row-level security as the second net of tenant isolation (AP-036, architecture §2a step 5): the role
    /// <c>agcowork_app</c> the application switches to, its privileges (also for tables of later migrations), and the two
    /// policies (tenant_isolation, instance_read) on every tenant table. Runs as the owner (migrations never switch the role).
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        // Tables with workspace_id that belong to a tenant. Not here: workspaces (the tenant root, id instead of workspace_id),
        // api_keys (workspace_id is the optional key restriction of an instance-level key), actors, local_logins, the
        // migration history. Fixed for this migration; later tenant tables call EnableTenantPolicy in their own migration.
        private static readonly string[] TenantTables =
        [
            "memberships", "spaces", "collections", "templates", "views", "records", "revisions",
            "activity_events", "idempotency_keys", "comments", "mentions",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Role, membership of the login, privileges and default privileges (also re-applied on every start).
            foreach (var sql in RowLevelSecurity.RoleAndPrivilegesSql)
            {
                migrationBuilder.Sql(sql);
            }

            // Policies: tenant_isolation for all commands (rows of the session's workspace), instance_read for SELECT in the
            // instance area. Without a workspace setting the role sees and writes nothing. Not FORCEd: the owner (migrations)
            // keeps seeing every row.
            foreach (var table in TenantTables)
            {
                migrationBuilder.EnableTenantPolicy(table);
            }

            // GIN operators as index conditions under the policies (reason at RowLevelSecurity.LeakproofSql).
            migrationBuilder.Sql(RowLevelSecurity.LeakproofSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.NotLeakproofSql);

            foreach (var table in TenantTables)
            {
                migrationBuilder.DisableTenantPolicy(table);
            }

            // The role stays (it is cluster-wide and may serve other databases); this database no longer grants it anything.
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE EXECUTE ON FUNCTIONS FROM agcowork_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE USAGE, SELECT ON SEQUENCES FROM agcowork_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM agcowork_app;");
            migrationBuilder.Sql("REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA public FROM agcowork_app;");
            migrationBuilder.Sql("REVOKE USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public FROM agcowork_app;");
            migrationBuilder.Sql("REVOKE SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public FROM agcowork_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA public FROM agcowork_app;");
        }
    }
}
