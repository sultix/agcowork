using Agcowork.Api.Endpoints.Records;
using Agcowork.Api.Http;
using Agcowork.Application.Comments;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Agcowork.Api.Endpoints.Comments;

/// <summary>
/// Comments on records and the caller's mentions (AP-029, architecture §2 "Kommentare und Erwähnungen"). Descriptions are
/// written so an agent can use them as tool descriptions. Thin: bind, call the use case, map the result.
/// </summary>
internal static class CommentEndpoints
{
    /// <summary>How mentions are written; shared by the REST descriptions and the MCP tools.</summary>
    public const string MentionSyntax =
        "Mention a member with a Markdown link to its actor id: [@Display name](actor:<uuid>), e.g. "
        + "[@Tech-Lead](actor:0190f3a2-7c4e-7b1a-9d2e-3f4a5b6c7d8e). Only the actor id counts; the link text is free. Actor ids come "
        + "from the workspace members, from 'authorId' of comments, from actor properties of records and from the activity feed. "
        + "Every mentioned member gets an entry in its inbox (mentions), nobody for mentioning themselves; links inside code spans or "
        + "code blocks are no mentions. Mentioning a non-member: 400 'not_member' on 'markdown' with the id.";

    public static RouteGroupBuilder MapCommentEndpoints(this RouteGroupBuilder workspace)
    {
        var records = workspace.MapGroup("/records/{recordId:guid}/comments").WithTags("Comments");

        records.MapGet("/", ListAsync)
            .WithName("ListComments")
            .WithSummary("List the comments of a record")
            .WithDescription(
                "The discussion of a record as threads: roots oldest first, each with its 'replies' oldest first, authors with name and "
                + "kind. Deleted comments are left out; a deleted root that still has replies stays as placeholder with 'markdown': null. "
                + "includeResolved=false hides resolved threads. At most 500 threads (the newest); 'omittedThreads' counts older ones "
                + "left out. Archived records can be read. Requires read.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        records.MapPost("/", AddAsync)
            .WithName("AddComment")
            .WithSummary("Comment on a record")
            .WithDescription(
                "Adds a comment: a new thread, or with 'parentCommentId' a reply to the root of a thread on the same record (threads "
                + "have one level: answering a reply is 400 'not_root'). 'markdown' max. 64 KiB. " + MentionSyntax + " Comments create "
                + "no record revision. Send an 'Idempotency-Key' header (e.g. a UUID) to make retries safe: the same key by the same "
                + "actor within 24 h with the same request returns the first comment; the same key with a different request (other "
                + "record, parentCommentId or markdown) is 409 'idempotency_key_reused'. Requires write. Archived record, collection or space: 409; deleted "
                + "parent: 409 'comment_deleted'.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var comments = workspace.MapGroup("/comments/{commentId:guid}").WithTags("Comments");

        comments.MapPatch("/", UpdateAsync)
            .WithName("UpdateComment")
            .WithSummary("Edit a comment or resolve its thread")
            .WithDescription(
                "'markdown' replaces the text (author only, else 403 'not_author'; sets 'editedAt', no revision). Mentions follow the "
                + "text: new links create mentions, removed links delete theirs, kept ones keep their read state. 'resolved' true "
                + "resolves, false reopens the thread (roots only, else 400 'not_root'). Both can be sent together. Requires write. "
                + "Deleted comment: 409 'comment_deleted'; archived record: 409.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        comments.MapDelete("/", DeleteAsync)
            .WithName("DeleteComment")
            .WithSummary("Delete a comment")
            .WithDescription(
                "Deletes softly: the text is removed and the comment's mentions leave the inboxes; a root with replies stays as "
                + "placeholder so the thread keeps its replies. Author or a member with admin, else 403 'not_author'. Deleting a "
                + "deleted comment changes nothing. Requires write. Archived record: 409.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        comments.MapPost("/resolve", ResolveAsync)
            .WithName("ResolveComment")
            .WithSummary("Resolve a thread")
            .WithDescription(
                "Marks the thread of this root comment as resolved ('resolvedAt', 'resolvedBy'); it stays readable. Replies cannot be "
                + "resolved: 400 'not_root'. Resolving a resolved thread changes nothing. Requires write. Archived record: 409.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        comments.MapPost("/reopen", ReopenAsync)
            .WithName("ReopenComment")
            .WithSummary("Reopen a thread")
            .WithDescription(
                "Opens a resolved thread again. Roots only: 400 'not_root'. Reopening an open thread changes nothing. Requires write. "
                + "Archived record: 409.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var mentions = workspace.MapGroup("/mentions").WithTags("Mentions");

        mentions.MapGet("/", ListMentionsAsync)
            .WithName("ListMentions")
            .WithSummary("List my mentions")
            .WithDescription(
                "Where you (the authenticated actor) were mentioned in comments of this workspace, newest first: record title, a "
                + "plain-text excerpt of the comment (max. 200 characters), who mentioned you, 'readAt'. unreadOnly=true lists only "
                + "unread ones. 'unreadCount' counts all your unread mentions, independent of filter and page. Paginated: 'limit' "
                + "(1..200, default 50) and 'cursor' (the 'nextCursor' of the previous page). Mark them read with POST /mentions/read. "
                + "Requires read.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        mentions.MapPost("/read", MarkReadAsync)
            .WithName("MarkMentionsRead")
            .WithSummary("Mark my mentions read")
            .WithDescription(
                "Marks your mentions read: 'ids' (max. 500, from GET /mentions) or 'all': true. Ids that are not yours, unknown or "
                + "already read are ignored; 'marked' counts what changed now, 'unreadCount' what is left. Requires read.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return workspace;
    }

    private static async Task<Results<Ok<CommentThreadsDto>, ProblemHttpResult>> ListAsync(
        Guid recordId,
        ListComments useCase,
        [FromQuery] bool? includeResolved,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ListCommentsRequest(recordId, includeResolved ?? true), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Created<CommentDto>, ProblemHttpResult>> AddAsync(
        Guid recordId,
        AddCommentBody body,
        [FromHeader(Name = RecordEndpoints.IdempotencyKeyHeader)] string? idempotencyKey,
        AddComment useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new AddCommentRequest(recordId, body.Markdown, body.ParentCommentId, idempotencyKey), cancellationToken);
        return result.IsSuccess ? TypedResults.Created((string?)null, result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<CommentDto>, ProblemHttpResult>> UpdateAsync(
        Guid commentId,
        UpdateCommentBody body,
        UpdateComment useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new UpdateCommentRequest(commentId, body.Markdown, body.Resolved), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid commentId, DeleteComment useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new DeleteCommentRequest(commentId), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<CommentDto>, ProblemHttpResult>> ResolveAsync(Guid commentId, UpdateComment useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new UpdateCommentRequest(commentId, Resolved: true), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<CommentDto>, ProblemHttpResult>> ReopenAsync(Guid commentId, UpdateComment useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new UpdateCommentRequest(commentId, Resolved: false), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<MentionPage>, ProblemHttpResult>> ListMentionsAsync(
        ListMentions useCase,
        [FromQuery] bool? unreadOnly,
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ListMentionsRequest(unreadOnly ?? false, limit, cursor), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<MarkMentionsReadResult>, ProblemHttpResult>> MarkReadAsync(
        MarkMentionsReadRequest body,
        MarkMentionsRead useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(body, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}

/// <summary>Body of <c>POST /records/{recordId}/comments</c>; the idempotency key comes as header.</summary>
/// <param name="Markdown">The comment, Markdown, max. 64 KiB; mentions as <c>[@Name](actor:&lt;uuid&gt;)</c>.</param>
/// <param name="ParentCommentId">Root comment to answer; omitted: a new thread.</param>
internal sealed record AddCommentBody(string? Markdown, Guid? ParentCommentId = null);

/// <summary>Body of <c>PATCH /comments/{commentId}</c>; null leaves a field unchanged.</summary>
/// <param name="Markdown">New text (author only); mentions are synchronized with it.</param>
/// <param name="Resolved">true resolves the thread, false reopens it (roots only).</param>
internal sealed record UpdateCommentBody(string? Markdown = null, bool? Resolved = null);
