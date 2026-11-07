using Agcowork.Domain.Collections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agcowork.Infrastructure.Persistence.Configurations;

/// <remarks>
/// Tenant table. The collection is referenced by (workspace_id, collection_id), like collections reference spaces.
/// At most one default template per collection (partial unique index <c>ix_templates_default</c>).
/// </remarks>
internal sealed class TemplateConfiguration : IEntityTypeConfiguration<Template>
{
    public void Configure(EntityTypeBuilder<Template> builder)
    {
        builder.ToTable("templates");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.HasOne<Collection>().WithMany()
            .HasForeignKey(t => new { t.WorkspaceId, t.CollectionId })
            .HasPrincipalKey(c => new { c.WorkspaceId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(t => new { t.WorkspaceId, t.CollectionId, t.Name }).IsUnique();
        builder.HasIndex(t => new { t.WorkspaceId, t.CollectionId })
            .IsUnique()
            .HasFilter("is_default")
            .HasDatabaseName("ix_templates_default");

        builder.Property(t => t.Name).HasMaxLength(Template.NameMaxLength).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(Template.DescriptionMaxLength);
        builder.Property(t => t.PropertiesPreset)
            .HasConversion(JsonColumns.PresetConverter, JsonColumns.PresetComparer)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(t => t.BodyMarkdown).HasMaxLength(Template.BodyMaxLength).IsRequired();
        builder.Property(t => t.IsDefault).IsRequired();
        builder.Property(t => t.CreatedBy).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();
    }
}
