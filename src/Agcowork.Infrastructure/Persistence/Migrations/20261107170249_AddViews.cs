using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agcowork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "views",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    filter = table.Column<string>(type: "jsonb", nullable: true),
                    sorts = table.Column<string>(type: "jsonb", nullable: false),
                    group_by_property = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    visible_properties = table.Column<string>(type: "jsonb", nullable: false),
                    card_cover_property = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_views", x => x.id);
                    table.ForeignKey(
                        name: "fk_views_collections_workspace_id_collection_id",
                        columns: x => new { x.workspace_id, x.collection_id },
                        principalTable: "collections",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_views_default",
                table: "views",
                columns: new[] { "workspace_id", "collection_id" },
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ix_views_workspace_id_collection_id_name",
                table: "views",
                columns: new[] { "workspace_id", "collection_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_views_workspace_id_collection_id_sort_order",
                table: "views",
                columns: new[] { "workspace_id", "collection_id", "sort_order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "views");
        }
    }
}
