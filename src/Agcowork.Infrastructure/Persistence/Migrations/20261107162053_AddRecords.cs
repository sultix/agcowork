using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Agcowork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gin", ",,");

            migrationBuilder.CreateTable(
                name: "activity_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: true),
                    collection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    diff = table.Column<string>(type: "jsonb", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_activity_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_activity_events_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    properties = table.Column<string>(type: "jsonb", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    revision_no = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_records", x => x.id);
                    table.UniqueConstraint("ak_records_workspace_id_id", x => new { x.workspace_id, x.id });
                    table.ForeignKey(
                        name: "fk_records_collections_workspace_id_collection_id",
                        columns: x => new { x.workspace_id, x.collection_id },
                        principalTable: "collections",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_records_records_workspace_id_parent_record_id",
                        columns: x => new { x.workspace_id, x.parent_record_id },
                        principalTable: "records",
                        principalColumns: new[] { "workspace_id", "id" });
                    table.ForeignKey(
                        name: "fk_records_spaces_workspace_id_space_id",
                        columns: x => new { x.workspace_id, x.space_id },
                        principalTable: "spaces",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_idempotency_keys_records_workspace_id_record_id",
                        columns: x => new { x.workspace_id, x.record_id },
                        principalTable: "records",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    no = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    properties = table.Column<string>(type: "jsonb", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_revisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_revisions_records_workspace_id_record_id",
                        columns: x => new { x.workspace_id, x.record_id },
                        principalTable: "records",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_activity_events_workspace_id_actor_id_seq",
                table: "activity_events",
                columns: new[] { "workspace_id", "actor_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_activity_events_workspace_id_collection_id_seq",
                table: "activity_events",
                columns: new[] { "workspace_id", "collection_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_activity_events_workspace_id_seq",
                table: "activity_events",
                columns: new[] { "workspace_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_activity_events_workspace_id_space_id_seq",
                table: "activity_events",
                columns: new[] { "workspace_id", "space_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_activity_events_workspace_id_target_id_seq",
                table: "activity_events",
                columns: new[] { "workspace_id", "target_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_workspace_id_actor_id_key",
                table: "idempotency_keys",
                columns: new[] { "workspace_id", "actor_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_workspace_id_record_id",
                table: "idempotency_keys",
                columns: new[] { "workspace_id", "record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_records_workspace_id_collection_id_created_at_id",
                table: "records",
                columns: new[] { "workspace_id", "collection_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_records_workspace_id_parent_record_id",
                table: "records",
                columns: new[] { "workspace_id", "parent_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_records_workspace_id_properties",
                table: "records",
                columns: new[] { "workspace_id", "properties" })
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "uuid_ops", "jsonb_path_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_records_workspace_id_space_id",
                table: "records",
                columns: new[] { "workspace_id", "space_id" });

            migrationBuilder.CreateIndex(
                name: "ix_revisions_workspace_id_record_id_no",
                table: "revisions",
                columns: new[] { "workspace_id", "record_id", "no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activity_events");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "revisions");

            migrationBuilder.DropTable(
                name: "records");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:btree_gin", ",,");
        }
    }
}
