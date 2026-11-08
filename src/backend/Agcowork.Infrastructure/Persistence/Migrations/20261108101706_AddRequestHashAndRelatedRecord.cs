using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agcowork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestHashAndRelatedRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Keys stored before AP-035 keep request_hash null and replay without a content check; they expire within 24 h.
            migrationBuilder.AddColumn<string>(
                name: "request_hash",
                table: "idempotency_keys",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "related_record_id",
                table: "activity_events",
                type: "uuid",
                nullable: true);

            // Backfill before the index: record events belong to their target, comment events to the record named in their diff
            // (AP-029 writes "recordId" into every comment event). Only well-formed ids are taken, so the cast cannot fail.
            // xact_id is a default on insert only, so the update leaves the forward-reading order untouched.
            migrationBuilder.Sql("UPDATE activity_events SET related_record_id = target_id WHERE target_type = 'record';");
            migrationBuilder.Sql("""
                UPDATE activity_events
                SET related_record_id = (diff ->> 'recordId')::uuid
                WHERE target_type = 'comment'
                  AND diff ->> 'recordId' ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$';
                """);

            migrationBuilder.CreateIndex(
                name: "ix_activity_events_workspace_id_related_record_id_seq",
                table: "activity_events",
                columns: new[] { "workspace_id", "related_record_id", "seq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_activity_events_workspace_id_related_record_id_seq",
                table: "activity_events");

            migrationBuilder.DropColumn(
                name: "request_hash",
                table: "idempotency_keys");

            migrationBuilder.DropColumn(
                name: "related_record_id",
                table: "activity_events");
        }
    }
}
