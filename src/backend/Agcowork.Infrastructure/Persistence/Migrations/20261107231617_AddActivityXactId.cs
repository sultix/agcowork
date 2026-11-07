using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agcowork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityXactId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing events get 0: final already, and among themselves in seq order (AP-024). New rows get the id of
            // their writing transaction.
            migrationBuilder.AddColumn<ulong>(
                name: "xact_id",
                table: "activity_events",
                type: "xid8",
                nullable: false,
                defaultValueSql: "'0'::xid8");
            migrationBuilder.Sql("ALTER TABLE activity_events ALTER COLUMN xact_id SET DEFAULT pg_current_xact_id();");

            migrationBuilder.CreateIndex(
                name: "ix_activity_events_workspace_id_xact_id_seq",
                table: "activity_events",
                columns: new[] { "workspace_id", "xact_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_activity_events_xact_id_seq",
                table: "activity_events",
                columns: new[] { "xact_id", "seq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_activity_events_workspace_id_xact_id_seq",
                table: "activity_events");

            migrationBuilder.DropIndex(
                name: "ix_activity_events_xact_id_seq",
                table: "activity_events");

            migrationBuilder.DropColumn(
                name: "xact_id",
                table: "activity_events");
        }
    }
}
