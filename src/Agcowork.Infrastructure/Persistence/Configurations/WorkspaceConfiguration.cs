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
        builder.Property(w => w.CreatedAt).IsRequired();
    }
}
