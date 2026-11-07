using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agcowork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "spaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    icon = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_spaces", x => x.id);
                    table.UniqueConstraint("ak_spaces_workspace_id_id", x => new { x.workspace_id, x.id });
                    table.ForeignKey(
                        name: "fk_spaces_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "collections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    schema = table.Column<string>(type: "jsonb", nullable: false),
                    title_property_key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collections", x => x.id);
                    table.UniqueConstraint("ak_collections_workspace_id_id", x => new { x.workspace_id, x.id });
                    table.ForeignKey(
                        name: "fk_collections_spaces_workspace_id_space_id",
                        columns: x => new { x.workspace_id, x.space_id },
                        principalTable: "spaces",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    properties_preset = table.Column<string>(type: "jsonb", nullable: false),
                    body_markdown = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_templates", x => x.id);
                    table.ForeignKey(
                        name: "fk_templates_collections_workspace_id_collection_id",
                        columns: x => new { x.workspace_id, x.collection_id },
                        principalTable: "collections",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_collections_workspace_id_space_id_name",
                table: "collections",
                columns: new[] { "workspace_id", "space_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_spaces_workspace_id_sort_order",
                table: "spaces",
                columns: new[] { "workspace_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_templates_default",
                table: "templates",
                columns: new[] { "workspace_id", "collection_id" },
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ix_templates_workspace_id_collection_id_name",
                table: "templates",
                columns: new[] { "workspace_id", "collection_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "templates");

            migrationBuilder.DropTable(
                name: "collections");

            migrationBuilder.DropTable(
                name: "spaces");
        }
    }
}
