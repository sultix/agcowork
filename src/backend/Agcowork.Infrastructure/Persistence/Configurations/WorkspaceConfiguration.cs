using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agcowork.Infrastructure.Persistence.Configurations;

internal sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> builder)
    {
        builder.ToTable("workspaces");

        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedNever();

        builder.Property(w => w.Slug).HasMaxLength(Workspace.SlugMaxLength).IsRequired();
        builder.HasIndex(w => w.Slug).IsUnique();

        builder.Property(w => w.Name).HasMaxLength(Workspace.NameMaxLength).IsRequired();
        builder.Property(w => w.Description).HasMaxLength(Workspace.DescriptionMaxLength);
        builder.Property(w => w.Settings).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb").IsRequired();
        // Actors are never deleted (only deactivated); Restrict keeps the creator reference honest.
        builder.HasOne<Actor>().WithMany().HasForeignKey(w => w.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        builder.Property(w => w.CreatedAt).IsRequired();
        builder.Property(w => w.ArchivedAt);
        builder.Ignore(w => w.IsArchived);
    }
}
