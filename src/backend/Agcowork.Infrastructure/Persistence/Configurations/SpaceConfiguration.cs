using Agcowork.Domain.Spaces;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agcowork.Infrastructure.Persistence.Configurations;

/// <remarks>
/// Tenant table: indexes start with <c>workspace_id</c>. The alternate key (workspace_id, id) lets collections reference
/// a space together with their own workspace, so the database itself rejects cross-workspace references.
/// </remarks>
internal sealed class SpaceConfiguration : IEntityTypeConfiguration<Space>
{
    public void Configure(EntityTypeBuilder<Space> builder)
    {
        builder.ToTable("spaces");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.HasAlternateKey(s => new { s.WorkspaceId, s.Id });

        builder.HasOne<Workspace>().WithMany().HasForeignKey(s => s.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(s => new { s.WorkspaceId, s.SortOrder });

        builder.Property(s => s.Name).HasMaxLength(Space.NameMaxLength).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(Space.DescriptionMaxLength);
        builder.Property(s => s.Icon).HasMaxLength(Space.IconMaxLength);
        builder.Property(s => s.SortOrder).IsRequired();
        // No FK on created_by: its index would not start with workspace_id; actors are never deleted.
        builder.Property(s => s.CreatedBy).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.ArchivedAt);
        builder.Ignore(s => s.IsArchived);
    }
}
