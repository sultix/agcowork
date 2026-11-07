using Agcowork.Domain.Activity;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agcowork.Infrastructure.Persistence.Configurations;

/// <remarks>
/// Tenant table, append-only. <c>seq</c> is an identity column (bigint): the order of the feed and its cursor. No foreign
/// keys on targets: events outlive what they describe and point at different tables.
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
