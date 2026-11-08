using Agcowork.Domain.Comments;
using Agcowork.Domain.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agcowork.Infrastructure.Persistence.Configurations;

/// <remarks>
/// Tenant table. (workspace_id, actor_id, key) is unique: the database decides concurrent retries. Keys of added comments
/// (AP-029) also reference the comment. <c>request_hash</c> (AP-035) is the SHA-256 of the normalized request; null for older keys.
/// </remarks>
internal sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.ToTable("idempotency_keys");
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).ValueGeneratedNever();

        builder.HasOne<Record>().WithMany()
            .HasForeignKey(k => new { k.WorkspaceId, k.RecordId })
            .HasPrincipalKey(r => new { r.WorkspaceId, r.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Comment>().WithMany()
            .HasForeignKey(k => new { k.WorkspaceId, k.CommentId })
            .HasPrincipalKey(c => new { c.WorkspaceId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(k => new { k.WorkspaceId, k.ActorId, k.Key }).IsUnique();

        builder.Property(k => k.Key).HasMaxLength(IdempotencyKey.KeyMaxLength).IsRequired();
        builder.Property(k => k.ActorId).IsRequired();
        builder.Property(k => k.RequestHash).HasMaxLength(IdempotencyKey.RequestHashLength);
        builder.Property(k => k.At).IsRequired();
        builder.Ignore(k => k.IsForComment);
    }
}
