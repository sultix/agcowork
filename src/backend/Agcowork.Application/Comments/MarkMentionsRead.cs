using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Comments;

/// <param name="Ids">Mentions to mark read (ids from the inbox). Either this or <paramref name="All"/>.</param>
/// <param name="All">true marks all of the caller's unread mentions in this workspace read.</param>
public sealed record MarkMentionsReadRequest(IReadOnlyList<Guid>? Ids = null, bool? All = null);

/// <summary>
/// Marks the caller's own mentions read. Ids of other actors' mentions, unknown and already read ones are ignored without a
/// hint (nothing about other inboxes leaks); the answer counts what was marked now. Requires <c>read</c>: the inbox is the
/// caller's own state, not workspace content, so viewers can clear it, also in an archived workspace.
/// </summary>
public sealed class MarkMentionsRead(IAgcoworkDbContext db, ICurrentActor currentActor, ITenantContext tenant, TimeProvider timeProvider)
    : IUseCase<MarkMentionsReadRequest, MarkMentionsReadResult>
{
    public const int MaxIds = 500;

    public async Task<Result<MarkMentionsReadResult>> HandleAsync(MarkMentionsReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var errors = new FieldErrors();
        var all = request.All == true;
        var ids = request.Ids?.Distinct().ToList() ?? [];
        if (all == (ids.Count > 0))
        {
            errors.Add("ids", ErrorCodes.Required, "Send either 'ids' (mention ids from the inbox) or 'all': true.");
        }
        else if (ids.Count > MaxIds)
        {
            errors.Add("ids", ErrorCodes.OutOfRange, $"At most {MaxIds} ids per request; use 'all': true to mark everything read.");
        }

        if (errors.Any)
        {
            return errors.ToError();
        }

        var me = currentActor.Id;
        var now = timeProvider.GetUtcNow();
        var unread = db.Mentions.Where(m => m.MentionedActorId == me && m.ReadAt == null);
        if (!all)
        {
            unread = unread.Where(m => ids.Contains(m.Id));
        }

        // Bulk update through the tenant filter: only the caller's mentions in this workspace.
        var marked = await unread.ExecuteUpdateAsync(setters => setters.SetProperty(m => m.ReadAt, now), cancellationToken).ConfigureAwait(false);
        var left = await db.Mentions.CountAsync(m => m.MentionedActorId == me && m.ReadAt == null, cancellationToken).ConfigureAwait(false);
        return new MarkMentionsReadResult(marked, left);
    }
}
