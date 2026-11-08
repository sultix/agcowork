using Agcowork.Domain.Collections;
using Agcowork.Domain.Records;
using Agcowork.Domain.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agcowork.Infrastructure.Persistence.Configurations;

/// <remarks>
/// Tenant table. Space, collection and parent are referenced together with <c>workspace_id</c>, so the database rejects
/// references across workspaces. <c>revision_no</c> is a concurrency token in addition to the row lock of the write use
/// cases. Properties are JSONB with a GIN index (<c>jsonb_path_ops</c>) behind <c>workspace_id</c> (btree_gin), for the
/// filter DSL (AP-009). Full-text search (AP-026): the column <c>search_vector tsvector</c>, its triggers and the GIN index
/// <c>ix_records_workspace_id_search_vector</c> exist only in the migration <c>AddRecordSearch</c>, deliberately not in this
/// model: a mapped column would be read with every record. Queries reach it through <c>RecordSearchFunctions</c>.
/// </remarks>
internal sealed class RecordConfiguration : IEntityTypeConfiguration<Record>
{
    public void Configure(EntityTypeBuilder<Record> builder)
    {
        builder.ToTable("records");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.HasAlternateKey(r => new { r.WorkspaceId, r.Id });

        builder.HasOne<Space>().WithMany()
            .HasForeignKey(r => new { r.WorkspaceId, r.SpaceId })
            .HasPrincipalKey(s => new { s.WorkspaceId, s.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Collection>().WithMany()
            .HasForeignKey(r => new { r.WorkspaceId, r.CollectionId })
            .HasPrincipalKey(c => new { c.WorkspaceId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);
        // NO ACTION (checked at statement end), so cascading deletes of a whole space or workspace succeed.
        builder.HasOne<Record>().WithMany()
            .HasForeignKey(r => new { r.WorkspaceId, r.ParentRecordId })
            .HasPrincipalKey(r => new { r.WorkspaceId, r.Id })
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(r => new { r.WorkspaceId, r.CollectionId, r.CreatedAt, r.Id });
        builder.HasIndex(r => new { r.WorkspaceId, r.SpaceId });
        builder.HasIndex(r => new { r.WorkspaceId, r.ParentRecordId });
        builder.HasIndex(r => new { r.WorkspaceId, r.Properties })
            .HasMethod("gin")
            .HasOperators("uuid_ops", "jsonb_path_ops")
            .HasDatabaseName("ix_records_workspace_id_properties");

        builder.Property(r => r.Title).HasMaxLength(Record.TitleMaxLength).IsRequired();
        builder.Property(r => r.Properties)
            .HasConversion(JsonColumns.PropertiesConverter, JsonColumns.PropertiesComparer)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(r => r.Body).IsRequired();
        builder.Property(r => r.RevisionNo).IsRequired().IsConcurrencyToken();
        // No FK on created_by/updated_by: their indexes would not start with workspace_id; actors are never deleted.
        builder.Property(r => r.CreatedBy).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedBy).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();
        builder.Property(r => r.ArchivedAt);
        builder.Ignore(r => r.IsArchived);
    }
}
