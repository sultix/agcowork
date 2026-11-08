using Agcowork.Domain.Activity;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agcowork.Infrastructure.Persistence.Configurations;

/// <remarks>
/// Tenant table, append-only. <c>seq</c> is an identity column (bigint): the order of the newest-first feed and its cursor.
/// <c>xact_id</c> (<c>xid8</c>, default <c>pg_current_xact_id()</c>) is the writing transaction; (<c>xact_id</c>, <c>seq</c>) is
/// the commit-safe order of forward readers (<c>Application.Activity.ActivityLog</c>). The table is its own outbox: the realtime
/// dispatcher reads it directly. No foreign keys on targets: events outlive what they describe and point at different tables.
/// </remarks>
internal sealed class ActivityEventConfiguration : IEntityTypeConfiguration<ActivityEvent>
{
    public void Configure(EntityTypeBuilder<ActivityEvent> builder)
    {
        builder.ToTable("activity_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Seq).UseIdentityAlwaysColumn().ValueGeneratedOnAdd();

        builder.HasOne<Workspace>().WithMany().HasForeignKey(e => e.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(e => new { e.WorkspaceId, e.Seq });
        builder.HasIndex(e => new { e.WorkspaceId, e.CollectionId, e.Seq });
        builder.HasIndex(e => new { e.WorkspaceId, e.ActorId, e.Seq });
        builder.HasIndex(e => new { e.WorkspaceId, e.SpaceId, e.Seq });
        builder.HasIndex(e => new { e.WorkspaceId, e.TargetId, e.Seq });

        // The history of one record including its comments (AP-035): record events carry the record, comment events its record.
        builder.HasIndex(e => new { e.WorkspaceId, e.RelatedRecordId, e.Seq });

        // Forward reading in commit-safe order: per workspace (afterSeq) and instance-wide for the realtime dispatcher. The
        // second one is a documented exception to "tenant indexes start with workspace_id": the dispatcher polls all
        // workspaces at once and must not scan the table when nothing happened.
        builder.Property(e => e.XactId)
            .HasColumnType("xid8")
            .HasDefaultValueSql("pg_current_xact_id()")
            .ValueGeneratedOnAdd();
        builder.HasIndex(e => new { e.WorkspaceId, e.XactId, e.Seq });
        builder.HasIndex(e => new { e.XactId, e.Seq });

        builder.Property(e => e.Type).HasMaxLength(ActivityEvent.TypeMaxLength).IsRequired();
        builder.Property(e => e.ActorId).IsRequired();
        builder.Property(e => e.TargetType).HasMaxLength(ActivityEvent.TargetTypeMaxLength).IsRequired();
        builder.Property(e => e.TargetId).IsRequired();
        builder.Property(e => e.Diff)
            .HasConversion(JsonColumns.DiffConverter, JsonColumns.DiffComparer)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(e => e.At).IsRequired();
    }
}
