using Agcowork.Domain.Comments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agcowork.Infrastructure.Persistence.Configurations;

/// <remarks>
/// Tenant table (AP-029): the inboxes. The comment is referenced by (workspace_id, comment_id) and takes its mentions along
/// when deleted; one mention per (comment, mentioned actor). The inbox reads by (workspace_id, mentioned_actor_id): unread ones
/// and their count over <c>read_at</c>, the newest-first page over <c>created_at</c>.
/// </remarks>
internal sealed class MentionConfiguration : IEntityTypeConfiguration<Mention>
{
    public void Configure(EntityTypeBuilder<Mention> builder)
    {
        builder.ToTable("mentions");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.HasOne<Comment>().WithMany()
            .HasForeignKey(m => new { m.WorkspaceId, m.CommentId })
            .HasPrincipalKey(c => new { c.WorkspaceId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(m => new { m.WorkspaceId, m.CommentId, m.MentionedActorId }).IsUnique();
        builder.HasIndex(m => new { m.WorkspaceId, m.MentionedActorId, m.ReadAt, m.CreatedAt });
        builder.HasIndex(m => new { m.WorkspaceId, m.MentionedActorId, m.CreatedAt, m.Id });

        // No FK on record_id (the comment's FK covers it), mentioned_actor_id and by_actor_id (actors are global, never deleted).
        builder.Property(m => m.RecordId).IsRequired();
        builder.Property(m => m.MentionedActorId).IsRequired();
        builder.Property(m => m.ByActorId).IsRequired();
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.Ignore(m => m.IsRead);
    }
}
