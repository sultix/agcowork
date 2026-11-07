using Agcowork.Domain.Collections;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agcowork.Infrastructure.Persistence.Configurations;

/// <remarks>
/// Tenant table. The collection is referenced by (workspace_id, collection_id), like templates. Names are unique per collection;
/// at most one default view per collection (partial unique index <c>ix_views_default</c>). Filter, sorts and visible properties
/// are JSONB in the format of <see cref="JsonColumns"/>.
/// </remarks>
internal sealed class ViewConfiguration : IEntityTypeConfiguration<View>
{
    public void Configure(EntityTypeBuilder<View> builder)
    {
        builder.ToTable("views");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();

        builder.HasOne<Collection>().WithMany()
            .HasForeignKey(v => new { v.WorkspaceId, v.CollectionId })
            .HasPrincipalKey(c => new { c.WorkspaceId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(v => new { v.WorkspaceId, v.CollectionId, v.SortOrder });
        builder.HasIndex(v => new { v.WorkspaceId, v.CollectionId, v.Name }).IsUnique();
        builder.HasIndex(v => new { v.WorkspaceId, v.CollectionId })
            .IsUnique()
            .HasFilter("is_default")
            .HasDatabaseName("ix_views_default");

        builder.Property(v => v.Name).HasMaxLength(View.NameMaxLength).IsRequired();
        builder.Property(v => v.Type).HasConversion<string>(
            type => ViewTypes.ToWire(type),
            wire => ParseType(wire)).HasMaxLength(16).IsRequired();
        builder.Property(v => v.Filter)
            .HasConversion(JsonColumns.ViewFilterConverter, JsonColumns.ViewFilterComparer)
            .HasColumnType("jsonb");
        builder.Property(v => v.Sorts)
            .HasConversion(JsonColumns.ViewSortsConverter, JsonColumns.ViewSortsComparer)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(v => v.GroupByProperty).HasMaxLength(PropertyDefinition.KeyMaxLength);
        builder.Property(v => v.VisibleProperties)
            .HasConversion(JsonColumns.StringListConverter, JsonColumns.StringListComparer)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(v => v.CardCoverProperty).HasMaxLength(PropertyDefinition.KeyMaxLength);
        builder.Property(v => v.IsDefault).IsRequired();
        builder.Property(v => v.IsSystem).IsRequired();
        builder.Property(v => v.SortOrder).IsRequired();
        builder.Property(v => v.CreatedBy).IsRequired();
        builder.Property(v => v.CreatedAt).IsRequired();
        builder.Ignore(v => v.IsDeletable);
        builder.Ignore(v => v.NeedsAttention);
    }

    private static ViewType ParseType(string wire) =>
        ViewTypes.TryParse(wire, out var type) ? type : throw new InvalidOperationException($"Stored view type '{wire}' is unknown.");
}
