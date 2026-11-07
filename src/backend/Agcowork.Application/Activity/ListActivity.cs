using System.Globalization;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Paging;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Activity;

/// <param name="SpaceId">Only events in this space.</param>
/// <param name="CollectionId">Only events in this collection.</param>
/// <param name="ActorId">Only events by this actor.</param>
/// <param name="Since">
/// Only events at or after this time. <c>at</c> is the application's clock when the change was made, before its commit: an
/// event can become visible after a later poll whose <c>since</c> already lies past its <c>at</c>. Pollers should not rely on
/// <c>since</c> alone but read page 1 and follow <c>nextCursor</c> until they reach the newest <c>seq</c> they already have
/// (AP-011 puts this into the tool description).
/// </param>
/// <param name="Types">Only events of these types.</param>
/// <param name="Limit">Page size, 1..200, default 50.</param>
/// <param name="Cursor"><c>nextCursor</c> of the previous page.</param>
public sealed record ListActivityRequest(
    Guid? SpaceId = null,
    Guid? CollectionId = null,
    Guid? ActorId = null,
    DateTimeOffset? Since = null,
    IReadOnlyList<string>? Types = null,
    int? Limit = null,
    string? Cursor = null);

/// <summary>
/// The activity feed of the workspace, newest first (by <c>seq</c>), filterable and paginated by cursor.
/// Requires <c>read</c>. "What happened since yesterday?" (<c>activity_recent</c>).
/// </summary>
public sealed class ListActivity(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<ListActivityRequest, Page<ActivityEventDto>>
{
    public async Task<Result<Page<ActivityEventDto>>> HandleAsync(ListActivityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var errors = new FieldErrors();
        var limit = Paging.Limit(errors, request.Limit);
        errors.AllOneOf("types", request.Types, ActivityTypes.All);
        var before = Paging.Decode<long>(errors, request.Cursor, ParseSeq);
        if (errors.Any)
        {
            return errors.ToError();
        }

        var query = db.ActivityEvents.AsNoTracking();
        if (request.SpaceId is { } spaceId)
        {
            query = query.Where(e => e.SpaceId == spaceId);
        }

        if (request.CollectionId is { } collectionId)
        {
            query = query.Where(e => e.CollectionId == collectionId);
        }

        if (request.ActorId is { } actorId)
        {
            query = query.Where(e => e.ActorId == actorId);
        }

        if (request.Since is { } since)
        {
            var sinceUtc = since.ToUniversalTime();
            query = query.Where(e => e.At >= sinceUtc);
        }

        if (request.Types is [_, ..] types)
        {
            var list = types.Distinct(StringComparer.Ordinal).ToList();
            query = query.Where(e => list.Contains(e.Type));
        }

        if (before is { } seq)
        {
            query = query.Where(e => e.Seq < seq);
        }

        var events = await query.OrderByDescending(e => e.Seq).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var next = events.Count > limit ? Paging.Encode(events[limit - 1].Seq.ToString(CultureInfo.InvariantCulture)) : null;
        var page = events.Take(limit).ToList();
        var titles = await ResolveTitlesAsync(page, cancellationToken).ConfigureAwait(false);
        return new Page<ActivityEventDto>(
            [.. page.Select(e => ActivityEventDto.From(e, titles.GetValueOrDefault((e.TargetType, e.TargetId))))],
            next);
    }

    /// <summary>
    /// Current titles of the page's records, spaces and collections: at most one query per target type, never one per
    /// event. The queries run through the tenant filter, so a target outside the workspace stays unresolved (null), like
    /// a deleted one. Archived targets still exist and keep their title.
    /// </summary>
    private async Task<Dictionary<(string Type, Guid Id), string>> ResolveTitlesAsync(IReadOnlyList<ActivityEvent> events, CancellationToken cancellationToken)
    {
        var titles = new Dictionary<(string Type, Guid Id), string>();
        List<Guid> Ids(string targetType) => [.. events.Where(e => e.TargetType == targetType).Select(e => e.TargetId).Distinct()];

        if (Ids(ActivityTargets.Record) is [_, ..] recordIds)
        {
            foreach (var record in await db.Records.AsNoTracking()
                         .Where(r => recordIds.Contains(r.Id))
                         .Select(r => new { r.Id, r.Title })
                         .ToListAsync(cancellationToken)
                         .ConfigureAwait(false))
            {
                titles[(ActivityTargets.Record, record.Id)] = record.Title;
            }
        }

        if (Ids(ActivityTargets.Space) is [_, ..] spaceIds)
        {
            foreach (var space in await db.Spaces.AsNoTracking()
                         .Where(s => spaceIds.Contains(s.Id))
                         .Select(s => new { s.Id, s.Name })
                         .ToListAsync(cancellationToken)
                         .ConfigureAwait(false))
            {
                titles[(ActivityTargets.Space, space.Id)] = space.Name;
            }
        }

        if (Ids(ActivityTargets.Collection) is [_, ..] collectionIds)
        {
            foreach (var collection in await db.Collections.AsNoTracking()
                         .Where(c => collectionIds.Contains(c.Id))
                         .Select(c => new { c.Id, c.Name })
                         .ToListAsync(cancellationToken)
                         .ConfigureAwait(false))
            {
                titles[(ActivityTargets.Collection, collection.Id)] = collection.Name;
            }
        }

        return titles;
    }

    private static long? ParseSeq(string position) =>
        long.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) && seq > 0 ? seq : null;
}
