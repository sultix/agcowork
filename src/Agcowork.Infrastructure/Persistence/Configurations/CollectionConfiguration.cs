using Agcowork.Domain.Collections;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agcowork.Infrastructure.Persistence.Configurations;

/// <remarks>
/// Tenant table. The space is referenced by (workspace_id, space_id): a collection can only live in a space of its
/// own workspace. The schema is JSONB in the format of <see cref="JsonColumns"/>; <c>schema_version</c> is the
/// concurrency token of schema updates.
/// </remarks>
internal sealed class CollectionConfiguration : IEntityTypeConfiguration<Collection>
{
    public void Configure(EntityTypeBuilder<Collection> builder)
    {
        builder.ToTable("collections");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasAlternateKey(c => new { c.WorkspaceId, c.Id });

        builder.HasOne<Space>().WithMany()
            .HasForeignKey(c => new { c.WorkspaceId, c.SpaceId })
            .HasPrincipalKey(s => new { s.WorkspaceId, s.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => new { c.WorkspaceId, c.SpaceId, c.Name }).IsUnique();

        builder.Property(c => c.Name).HasMaxLength(Collection.NameMaxLength).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(Collection.DescriptionMaxLength).IsRequired();
        builder.Property(c => c.Schema)
            .HasConversion(JsonColumns.SchemaConverter, JsonColumns.SchemaComparer)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(c => c.TitlePropertyKey).HasMaxLength(PropertyDefinition.KeyMaxLength).IsRequired();
        builder.Property(c => c.SchemaVersion).IsRequired().IsConcurrencyToken();
        builder.Property(c => c.CreatedBy).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.ArchivedAt);
        builder.Ignore(c => c.IsArchived);
    }
}
