using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agcowork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "archived_at",
                table: "workspaces",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "created_by",
                table: "workspaces",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "workspaces",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "settings",
                table: "workspaces",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.CreateTable(
                name: "memberships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_memberships", x => x.id);
                    table.ForeignKey(
                        name: "fk_memberships_actors_actor_id",
                        column: x => x.actor_id,
                        principalTable: "actors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_memberships_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_workspaces_created_by",
                table: "workspaces",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_workspace_id",
                table: "api_keys",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_memberships_actor_id",
                table: "memberships",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_memberships_workspace_id_actor_id",
                table: "memberships",
                columns: new[] { "workspace_id", "actor_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_api_keys_workspaces_workspace_id",
                table: "api_keys",
                column: "workspace_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_workspaces_actors_created_by",
                table: "workspaces",
                column: "created_by",
                principalTable: "actors",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_api_keys_workspaces_workspace_id",
                table: "api_keys");

            migrationBuilder.DropForeignKey(
                name: "fk_workspaces_actors_created_by",
                table: "workspaces");

            migrationBuilder.DropTable(
                name: "memberships");

            migrationBuilder.DropIndex(
                name: "ix_workspaces_created_by",
                table: "workspaces");

            migrationBuilder.DropIndex(
                name: "ix_api_keys_workspace_id",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "archived_at",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "description",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "settings",
                table: "workspaces");
        }
    }
}
