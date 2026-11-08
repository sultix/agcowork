using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Comments;

/// <param name="RecordId">Record whose discussion to read.</param>
/// <param name="IncludeResolved">Also list resolved threads. Default true.</param>
public sealed record ListCommentsRequest(Guid RecordId, bool IncludeResolved = true);

/// <summary>
/// The discussion of a record (archived ones included): threads oldest first, each root with its replies oldest first, authors
/// with name and kind. Deleted comments are left out, a deleted root with replies stays as placeholder. Requires <c>read</c>.
/// At most <see cref="MaxThreads"/> threads, the newest ones (<c>omittedThreads</c> counts the older ones left out), each with at
/// most <see cref="MaxRepliesPerThread"/> replies, the newest ones (<c>omittedReplies</c> on the root).
/// </summary>
public sealed class ListComments(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<ListCommentsRequest, CommentThreadsDto>
{
    /// <summary>Threads per response. Not paginated: a discussion is read as a whole, and this many threads is already a lot.</summary>
    public const int MaxThreads = 500;

    /// <summary>Replies per thread: the newest ones; <c>omittedReplies</c> of the root counts the older ones left out.</summary>
    public const int MaxRepliesPerThread = 100;

    public async Task<Result<CommentThreadsDto>> HandleAsync(ListCommentsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        if (!await db.Records.AnyAsync(r => r.Id == request.RecordId, cancellationToken).ConfigureAwait(false))
        {
            return new NotFoundError("record");
        }

        return await CommentReads.ThreadsAsync(db, request.RecordId, request.IncludeResolved, cancellationToken).ConfigureAwait(false);
    }
}
