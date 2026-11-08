using System.Globalization;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Paging;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Comments;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Comments;

/// <param name="UnreadOnly">Only mentions not marked read yet.</param>
/// <param name="Limit">Page size, 1..200, default 50.</param>
/// <param name="Cursor"><c>nextCursor</c> of the previous page.</param>
public sealed record ListMentionsRequest(bool UnreadOnly = false, int? Limit = null, string? Cursor = null);

/// <summary>
/// The caller's inbox: where the caller (the authenticated actor, never a parameter) was mentioned in comments of this
/// workspace, newest first, with record title, a plain-text excerpt of the comment and who mentioned. Requires <c>read</c>.
/// Keyset-paginated; <c>unreadCount</c> counts all unread mentions regardless of filter and page.
/// </summary>
public sealed class ListMentions(IAgcoworkDbContext db, ICurrentActor currentActor, ITenantContext tenant) : IUseCase<ListMentionsRequest, MentionPage>
{
    /// <summary>Length of <see cref="MentionDto.Excerpt"/>.</summary>
    public const int ExcerptLength = 200;

    // Enough Markdown to fill the excerpt after links and markers are stripped, without loading 64 KiB per comment.
    private const int ExcerptSource = 2000;

    public async Task<Result<MentionPage>> HandleAsync(ListMentionsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var errors = new FieldErrors();
        var limit = Paging.Limit(errors, request.Limit);
        var after = Paging.Decode<(DateTimeOffset CreatedAt, Guid Id)>(errors, request.Cursor, ParsePosition);
        if (errors.Any)
        {
            return errors.ToError();
        }

        var me = currentActor.Id;
        var mentions = db.Mentions.AsNoTracking().Where(m => m.MentionedActorId == me);
        if (request.UnreadOnly)
        {
            mentions = mentions.Where(m => m.ReadAt == null);
        }

        if (after is { } position)
        {
            var (createdAt, id) = position;
            mentions = mentions.Where(m => m.CreatedAt < createdAt || (m.CreatedAt == createdAt && m.Id < id));
        }

        // Comments and records are tenant-filtered as well; the joins never leave the workspace.
        var rows = await (
                from m in mentions
                join c in db.Comments.AsNoTracking() on m.CommentId equals c.Id
                join r in db.Records.AsNoTracking() on m.RecordId equals r.Id
                orderby m.CreatedAt descending, m.Id descending
                select new
                {
                    m.Id,
                    m.CommentId,
                    c.ParentCommentId,
                    m.RecordId,
                    r.Title,
                    m.ByActorId,
                    Body = c.BodyMarkdown.Substring(0, ExcerptSource),
                    m.CreatedAt,
                    m.ReadAt,
                })
            .Take(limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var next = rows.Count > limit ? Paging.Encode(Position(rows[limit - 1].CreatedAt, rows[limit - 1].Id)) : null;
        var page = rows.Take(limit).ToList();

        var actors = await CommentReads.ActorsAsync(db, page.Select(r => r.ByActorId), cancellationToken).ConfigureAwait(false);
        var unread = await db.Mentions.CountAsync(m => m.MentionedActorId == me && m.ReadAt == null, cancellationToken).ConfigureAwait(false);
        return new MentionPage(
            [.. page.Select(r => new MentionDto(
                r.Id,
                r.CommentId,
                r.ParentCommentId,
                r.RecordId,
                r.Title,
                r.ByActorId,
                actors.GetValueOrDefault(r.ByActorId)?.Name ?? CommentDto.UnknownAuthor,
                CommentMarkdown.PlainText(r.Body, ExcerptLength),
                r.CreatedAt,
                r.ReadAt))],
            next,
            unread);
    }

    private static string Position(DateTimeOffset createdAt, Guid id) =>
        string.Create(CultureInfo.InvariantCulture, $"{createdAt.UtcTicks}:{id:N}");

    private static (DateTimeOffset, Guid)? ParsePosition(string position)
    {
        var parts = position.Split(':');
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks
            && Guid.TryParseExact(parts[1], "N", out var id)
                ? (new DateTimeOffset(ticks, TimeSpan.Zero), id)
                : null;
    }
}
