using Agcowork.Domain.Comments;
using Agcowork.Domain.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agcowork.Infrastructure.Persistence.Configurations;

/// <remarks>
/// Tenant table (AP-029). Record and parent are referenced together with <c>workspace_id</c>, so the database rejects comments
/// across workspaces; deleting a record deletes its comments. The parent reference is NO ACTION (checked at statement end), so
/// cascading deletes of a whole record, space or workspace succeed. Threads are read by (workspace_id, record_id, created_at).
/// </remarks>
internal sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> builder)
    {
        builder.ToTable("comments");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasAlternateKey(c => new { c.WorkspaceId, c.Id });

        builder.HasOne<Record>().WithMany()
            .HasForeignKey(c => new { c.WorkspaceId, c.RecordId })
            .HasPrincipalKey(r => new { r.WorkspaceId, r.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Comment>().WithMany()
            .HasForeignKey(c => new { c.WorkspaceId, c.ParentCommentId })
            .HasPrincipalKey(c => new { c.WorkspaceId, c.Id })
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(c => new { c.WorkspaceId, c.RecordId, c.CreatedAt });
        builder.HasIndex(c => new { c.WorkspaceId, c.ParentCommentId });

        builder.Property(c => c.BodyMarkdown).IsRequired();
        // No FK on author_id/resolved_by: their indexes would not start with workspace_id; actors are never deleted.
        builder.Property(c => c.AuthorId).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Ignore(c => c.IsRoot);
        builder.Ignore(c => c.IsResolved);
        builder.Ignore(c => c.IsDeleted);
    }
}
