using Agcowork.Domain.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agcowork.Infrastructure.Persistence.Configurations;

/// <remarks>Tenant table. One row per record change; (workspace_id, record_id, no) is unique and the order.</remarks>
internal sealed class RevisionConfiguration : IEntityTypeConfiguration<Revision>
{
    public void Configure(EntityTypeBuilder<Revision> builder)
    {
        builder.ToTable("revisions");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.HasOne<Record>().WithMany()
            .HasForeignKey(r => new { r.WorkspaceId, r.RecordId })
            .HasPrincipalKey(r => new { r.WorkspaceId, r.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(r => new { r.WorkspaceId, r.RecordId, r.No }).IsUnique();

        builder.Property(r => r.No).IsRequired();
        builder.Property(r => r.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.Title).HasMaxLength(Record.TitleMaxLength).IsRequired();
        builder.Property(r => r.Properties)
            .HasConversion(JsonColumns.PropertiesConverter, JsonColumns.PropertiesComparer)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(r => r.Body).IsRequired();
        builder.Property(r => r.ActorId).IsRequired();
        builder.Property(r => r.At).IsRequired();
        builder.Property(r => r.Reason).HasMaxLength(Revision.ReasonMaxLength);
    }
}
