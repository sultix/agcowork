using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agcowork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "comment_id",
                table: "idempotency_keys",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "comments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_comment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    body_markdown = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comments", x => x.id);
                    table.UniqueConstraint("ak_comments_workspace_id_id", x => new { x.workspace_id, x.id });
                    table.ForeignKey(
                        name: "fk_comments_comments_workspace_id_parent_comment_id",
                        columns: x => new { x.workspace_id, x.parent_comment_id },
                        principalTable: "comments",
                        principalColumns: new[] { "workspace_id", "id" });
                    table.ForeignKey(
                        name: "fk_comments_records_workspace_id_record_id",
                        columns: x => new { x.workspace_id, x.record_id },
                        principalTable: "records",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mentions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    comment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mentioned_actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    by_actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mentions", x => x.id);
                    table.ForeignKey(
                        name: "fk_mentions_comments_workspace_id_comment_id",
                        columns: x => new { x.workspace_id, x.comment_id },
                        principalTable: "comments",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_workspace_id_comment_id",
                table: "idempotency_keys",
                columns: new[] { "workspace_id", "comment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_comments_workspace_id_parent_comment_id",
                table: "comments",
                columns: new[] { "workspace_id", "parent_comment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_comments_workspace_id_record_id_created_at",
                table: "comments",
                columns: new[] { "workspace_id", "record_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_mentions_workspace_id_comment_id_mentioned_actor_id",
                table: "mentions",
                columns: new[] { "workspace_id", "comment_id", "mentioned_actor_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mentions_workspace_id_mentioned_actor_id_created_at_id",
                table: "mentions",
                columns: new[] { "workspace_id", "mentioned_actor_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_mentions_workspace_id_mentioned_actor_id_read_at_created_at",
                table: "mentions",
                columns: new[] { "workspace_id", "mentioned_actor_id", "read_at", "created_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_idempotency_keys_comments_workspace_id_comment_id",
                table: "idempotency_keys",
                columns: new[] { "workspace_id", "comment_id" },
                principalTable: "comments",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_idempotency_keys_comments_workspace_id_comment_id",
                table: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "mentions");

            migrationBuilder.DropTable(
                name: "comments");

            migrationBuilder.DropIndex(
                name: "ix_idempotency_keys_workspace_id_comment_id",
                table: "idempotency_keys");

            migrationBuilder.DropColumn(
                name: "comment_id",
                table: "idempotency_keys");
        }
    }
}
