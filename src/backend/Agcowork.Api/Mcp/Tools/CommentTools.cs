using System.ComponentModel;
using Agcowork.Application.Comments;
using Agcowork.Application.Common.Results;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agcowork.Api.Mcp.Tools;

/// <summary>
/// Comments and mentions (AP-029, <c>/w/{workspaceSlug}/mcp</c>): five tools; deleting is <c>comment_update(deleted: true)</c> to
/// keep the tool count small. Thin: bind, call the use case, render.
/// </summary>
[McpServerToolType]
internal static class CommentTools
{
    /// <summary>The use case names the field after the REST header; on MCP it is the argument <c>idempotencyKey</c>.</summary>
    private const string IdempotencyKeyRestField = "Idempotency-Key";

    [McpServerTool(Name = "comment_add", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Comments on a record to discuss it with humans and other agents, ask a question or answer one. Without parentCommentId it "
        + "starts a thread; with parentCommentId (the id of a thread's root, e.g. from comments_list or mentions_list) it replies. "
        + "Threads have one level: answer the root, not a reply ('not_root'). 'markdown' is Markdown, max. 64 KiB. To address someone, "
        + "mention them with a link to their actor id: [@Display name](actor:<uuid>), e.g. [@Tech-Lead](actor:0190f3a2-7c4e-7b1a-9d2e-3f4a5b6c7d8e). "
        + "Actor ids: the members section of workspace_overview, 'authorId' in comments_list, actor properties of records, "
        + "'actorId' in activity_recent. Every mentioned member is notified: it finds the comment in its mentions_list. Only "
        + "members can be mentioned ('not_member' with the id); links inside code are no mentions. A comment creates no record "
        + "revision and does not change the record. Send 'idempotencyKey' (e.g. a UUID) so a retry within 24 h returns the first "
        + "comment instead of a duplicate; a retry must repeat the call unchanged, the same key with another recordId, "
        + "parentCommentId or markdown is 'idempotency_key_reused'. Archived records cannot be commented (409). Requires write.")]
    public static async Task<CallToolResult> Add(
        AddComment useCase,
        McpOutput output,
        [Description("Record to comment on.")] Guid recordId,
        [Description("The comment as Markdown, max. 64 KiB. Mentions: [@Name](actor:<uuid>).")] string markdown,
        [Description("Root comment to reply to; omit to start a thread.")] Guid? parentCommentId = null,
        [Description("Makes retries safe: the same key by you within 24 h with the same arguments returns the first comment; with other arguments it is 'idempotency_key_reused'. 1-255 characters, e.g. a UUID.")] string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var result = await useCase.HandleAsync(new AddCommentRequest(recordId, markdown, parentCommentId, idempotencyKey), cancellationToken);
        return result.IsSuccess
            ? output.Success(result.Value, McpText.CommentChanged(result.Value.ParentCommentId is null ? "Started a thread" : "Replied", result.Value))
            : output.Failure(RenameField(result.Error, IdempotencyKeyRestField, "idempotencyKey"));
    }

    [McpServerTool(Name = "comments_list", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Reads the discussion of a record: threads oldest first, each root with its replies oldest first, every comment with id, "
        + "author (authorId, name, human or agent), Markdown, time, editedAt and, for roots, resolvedAt. Deleted comments are left "
        + "out, a deleted root with replies stays as placeholder with 'markdown': null. includeResolved: false hides resolved threads. "
        + "At most 500 threads (the newest; 'omittedThreads' counts older ones). Also available with record_get(includeComments: "
        + "true). Requires read.")]
    public static async Task<CallToolResult> List(
        ListComments useCase,
        McpOutput output,
        [Description("Record whose comments to read.")] Guid recordId,
        [Description("Also list resolved threads. Default true.")] bool? includeResolved = null,
        CancellationToken cancellationToken = default)
    {
        var result = await useCase.HandleAsync(new ListCommentsRequest(recordId, includeResolved ?? true), cancellationToken);
        return output.From(result, McpText.Comments);
    }

    [McpServerTool(Name = "comment_update", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Changes a comment. 'markdown' replaces its text (only your own comments: 'not_author'); mentions follow the text: new "
        + "[@Name](actor:<uuid>) links notify those members, removed links take the mention back. 'resolved': true marks the "
        + "thread as resolved (e.g. once a question is answered), false reopens it; only the root of a thread ('not_root'), any "
        + "member with write. markdown and resolved can be sent together. 'deleted': true deletes the comment (your own, or any "
        + "with the admin scope; its mentions are removed, a root with replies stays as placeholder); send it alone. Comments keep "
        + "no revisions; editedAt shows an edit. Archived records: 409. Requires write.")]
    public static async Task<CallToolResult> Update(
        UpdateComment update,
        DeleteComment delete,
        McpOutput output,
        [Description("Comment to change.")] Guid commentId,
        [Description("New text (replaces the old one), Markdown. Your own comments only.")] string? markdown = null,
        [Description("true resolves the thread, false reopens it. Roots only.")] bool? resolved = null,
        [Description("true deletes the comment. Send it without markdown and resolved.")] bool? deleted = null,
        CancellationToken cancellationToken = default)
    {
        if (deleted is { } deleteFlag)
        {
            if (!deleteFlag)
            {
                return output.InvalidInput("deleted", ErrorCodes.NotAllowed, "Deleted comments cannot be restored; 'deleted' only accepts true.");
            }

            if (markdown is not null || resolved is not null)
            {
                return output.InvalidInput("deleted", ErrorCodes.NotAllowed, "Send 'deleted': true alone, without 'markdown' or 'resolved'.");
            }

            var removed = await delete.HandleAsync(new DeleteCommentRequest(commentId), cancellationToken);
            return removed.IsSuccess
                ? output.Success(new CommentDeletedResult(commentId, Deleted: true), $"Deleted comment {commentId}.")
                : output.Failure(removed.Error);
        }

        var result = await update.HandleAsync(new UpdateCommentRequest(commentId, markdown, resolved), cancellationToken);
        return output.From(result, comment => McpText.CommentChanged("Updated", comment));
    }

    [McpServerTool(Name = "mentions_list", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Your inbox: where you were mentioned in comments of this workspace, i.e. the open questions and requests addressed to "
        + "you. Newest first, each with mention id, record (recordId, title), commentId and parentCommentId (reply with "
        + "comment_add using parentCommentId, or the commentId for a root), who mentioned you and a plain-text excerpt (max. 200 "
        + "characters; read the full thread with comments_list). unreadOnly: true lists only unread ones; 'unreadCount' counts all "
        + "your unread mentions. Paginated with 'limit' (1..200, default 50) and 'cursor' (nextCursor of the previous page). Mark "
        + "handled ones with mentions_mark_read. Requires read.")]
    public static async Task<CallToolResult> Mentions(
        ListMentions useCase,
        McpOutput output,
        [Description("Only unread mentions. Default false.")] bool? unreadOnly = null,
        [Description("Page size 1..200, default 50.")] int? limit = null,
        [Description("nextCursor of the previous page, unchanged.")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var result = await useCase.HandleAsync(new ListMentionsRequest(unreadOnly ?? false, limit, cursor), cancellationToken);
        return output.From(result, McpText.Mentions);
    }

    [McpServerTool(Name = "mentions_mark_read", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Marks your mentions as read once you handled them: 'mentionIds' (ids from mentions_list, max. 500) or 'all': true. Ids "
        + "that are not yours, unknown or already read are ignored. Returns 'marked' (changed now) and 'unreadCount' (left). "
        + "Requires read.")]
    public static async Task<CallToolResult> MarkRead(
        MarkMentionsRead useCase,
        McpOutput output,
        [Description("Mention ids from mentions_list. Either this or all.")] IReadOnlyList<Guid>? mentionIds = null,
        [Description("true marks all your unread mentions in this workspace read.")] bool? all = null,
        CancellationToken cancellationToken = default)
    {
        var result = await useCase.HandleAsync(new MarkMentionsReadRequest(mentionIds, all), cancellationToken);
        return result.IsSuccess
            ? output.Success(result.Value, $"Marked {result.Value.Marked} mention(s) read; {result.Value.UnreadCount} unread left.")
            : output.Failure(RenameField(result.Error, "ids", "mentionIds"));
    }

    /// <summary>Renames a field of a validation error (adapter naming); other errors pass unchanged.</summary>
    private static Error RenameField(Error error, string from, string to) => error switch
    {
        ValidationError validation => new ValidationError([.. validation.Errors.Select(e => e.Field == from
            ? e with { Field = to, Message = e.Message.Replace("The Idempotency-Key header", "'idempotencyKey'", StringComparison.Ordinal).Replace("'ids'", "'mentionIds'", StringComparison.Ordinal) }
            : e)]),
        ConflictError { Field: { } field } conflict when field == from => conflict with { Field = to },
        _ => error,
    };
}

/// <summary>Structured result of <c>comment_update(deleted: true)</c>.</summary>
/// <param name="Id">The deleted comment.</param>
/// <param name="Deleted">Always true.</param>
internal sealed record CommentDeletedResult(Guid Id, bool Deleted);
