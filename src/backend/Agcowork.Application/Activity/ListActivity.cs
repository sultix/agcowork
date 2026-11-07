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
/// event can become visible after a later poll whose <c>since</c> already lies past its <c>at</c>. For a one-off look back;
/// pollers use <paramref name="AfterSeq"/>.
/// </param>
/// <param name="Types">Only events of these types.</param>
/// <param name="Limit">Page size, 1..200, default 50.</param>
/// <param name="Cursor"><c>nextCursor</c> of the previous page (newest first). Not together with <paramref name="AfterSeq"/>.</param>
/// <param name="AfterSeq">
/// Forward reading: <c>lastSeq</c> of the previous response, or 0 for the whole log. Returns the events after that position in
/// commit-safe order (transaction order), only those that are final, so polling with <c>lastSeq</c> never skips an event. Only
/// <c>lastSeq</c> is a safe cursor; the <c>seq</c> of an item is not.
/// </param>
/// <param name="TargetId">Only events of this target (record, space, collection, view; for members the actor id).</param>
public sealed record ListActivityRequest(
    Guid? SpaceId = null,
    Guid? CollectionId = null,
    Guid? ActorId = null,
    DateTimeOffset? Since = null,
    IReadOnlyList<string>? Types = null,
    int? Limit = null,
    string? Cursor = null,
    long? AfterSeq = null,
    Guid? TargetId = null);

/// <summary>
/// The activity feed of the workspace. Requires <c>read</c>. Two modes:
/// <list type="bullet">
/// <item>Without <c>afterSeq</c>: newest first (by <c>seq</c>), paginated by <c>cursor</c>; "what happened since yesterday?".</item>
/// <item>With <c>afterSeq</c>: forward in the commit-safe order (transaction order) of <see cref="ActivityLog"/>, only final events, continued with
/// <c>lastSeq</c>; for agents that keep up with changes.</item>
/// </list>
/// Both answer <c>lastSeq</c>: the position to continue forward reading from (newest-first mode: the newest final event).
/// </summary>
public sealed class ListActivity(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<ListActivityRequest, ActivityPage>
{
    public async Task<Result<ActivityPage>> HandleAsync(ListActivityRequest request, CancellationToken cancellationToken)
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
        if (request.AfterSeq is { } afterSeq)
        {
            if (afterSeq < 0)
            {
                errors.Add("afterSeq", ErrorCodes.OutOfRange, "'afterSeq' must be 0 or greater: 0 for the whole log, otherwise 'lastSeq' of the previous response.");
            }

            if (request.Cursor is not null)
            {
                errors.Add("cursor", ErrorCodes.NotAllowed, "'cursor' pages newest first; 'afterSeq' reads forward. Send one of them.");
            }
        }

        if (errors.Any)
        {
            return errors.ToError();
        }

        var query = Filter(db.ActivityEvents.AsNoTracking(), request);
        return request.AfterSeq is { } after
            ? await ForwardAsync(query, after, limit, cancellationToken).ConfigureAwait(false)
            : await NewestFirstAsync(query, before, limit, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<ActivityPage>> NewestFirstAsync(IQueryable<ActivityEvent> query, long? before, int limit, CancellationToken cancellationToken)
    {
        if (before is { } seq)
        {
            query = query.Where(e => e.Seq < seq);
        }

        // lastSeq is the newest final event, not the newest seq of the page: that one may not be final yet, and continuing
        // after it could skip an older transaction. Read before the page (separate snapshots): everything final at this point
        // is visible to the page query, so what becomes final in between is repeated by forward reading, never skipped.
        var head = await db.ActivityEvents.AsNoTracking().HeadAsync(cancellationToken).ConfigureAwait(false);
        var events = await query.OrderByDescending(e => e.Seq).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var next = events.Count > limit ? Paging.Encode(events[limit - 1].Seq.ToString(CultureInfo.InvariantCulture)) : null;
        var page = events.Take(limit).ToList();
        return new ActivityPage(await ToDtosAsync(page, cancellationToken).ConfigureAwait(false), next, head.Seq);
    }

    private async Task<Result<ActivityPage>> ForwardAsync(IQueryable<ActivityEvent> query, long afterSeq, int limit, CancellationToken cancellationToken)
    {
        var start = ActivityPosition.Start;
        if (afterSeq > 0)
        {
            // The position of an event is looked up without the request's filters: lastSeq stays valid when filters change.
            var known = await db.ActivityEvents.AsNoTracking()
                .Where(e => e.Seq == afterSeq)
                .Select(e => new { e.XactId, e.Seq, Stable = e.XactId < ActivityLog.StableHorizon() })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (known is null)
            {
                var errors = new FieldErrors();
                errors.Add(
                    "afterSeq",
                    ErrorCodes.InvalidCursor,
                    "'afterSeq' is no event of this workspace. Send 0 for the whole log, otherwise 'lastSeq' of the previous response unchanged.");
                return errors.ToError();
            }

            // Only lastSeq is a safe cursor. A seq taken from an item (e.g. of the newest-first listing) can belong to an event
            // that is not final yet; continuing after it would skip an older transaction that commits later. Such a seq is
            // answered from the newest final event instead (events repeat) and its lastSeq is the safe position. Sending the
            // item's seq again later, once it is final, can still skip events: the description tells agents to use lastSeq.
            start = known.Stable
                ? new ActivityPosition(known.XactId, known.Seq)
                : await db.ActivityEvents.AsNoTracking().HeadAsync(cancellationToken).ConfigureAwait(false);
        }

        var events = await query.Stable().After(start).InCommitOrder().Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var lastSeq = events.Count > 0 ? events[^1].Seq : start.Seq;
        return new ActivityPage(await ToDtosAsync(events, cancellationToken).ConfigureAwait(false), NextCursor: null, lastSeq);
    }

    private static IQueryable<ActivityEvent> Filter(IQueryable<ActivityEvent> query, ListActivityRequest request)
    {
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

        if (request.TargetId is { } targetId)
        {
            query = query.Where(e => e.TargetId == targetId);
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

        return query;
    }

    private async Task<IReadOnlyList<ActivityEventDto>> ToDtosAsync(IReadOnlyList<ActivityEvent> events, CancellationToken cancellationToken)
    {
        var titles = await ActivityTitles.ResolveAsync(db, events, cancellationToken).ConfigureAwait(false);
        return [.. events.Select(e => ActivityEventDto.From(e, titles.GetValueOrDefault((e.TargetType, e.TargetId))))];
    }

    private static long? ParseSeq(string position) =>
        long.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) && seq > 0 ? seq : null;
}

/// <summary>One page of the activity feed.</summary>
/// <param name="Items">The events of this page: newest first, or with <c>afterSeq</c> in commit-safe order (transaction order, oldest first).</param>
/// <param name="NextCursor">Newest first only: cursor of the next (older) page, null on the last page. Always null with <c>afterSeq</c>.</param>
/// <param name="LastSeq">
/// Where to continue reading forward: pass it unchanged as <c>afterSeq</c>; it is the only safe cursor (the <c>seq</c> of an item
/// is not). With <c>afterSeq</c>: the seq of the last event of this page, or where the read started when there was nothing new.
/// Newest first: the newest final event of the workspace. It is not always the highest seq: events come in commit-safe order
/// (transaction order), and an event with a lower seq can follow one with a higher seq.
/// </param>
public sealed record ActivityPage(IReadOnlyList<ActivityEventDto> Items, string? NextCursor, long LastSeq);

/// <summary>Current titles of activity targets.</summary>
internal static class ActivityTitles
{
    /// <summary>
    /// Current titles of the events' records, spaces and collections: at most one query per target type, never one per
    /// event. The queries run through the tenant filter, so a target outside the workspace stays unresolved (null), like
    /// a deleted one. Archived targets still exist and keep their title.
    /// </summary>
    public static async Task<Dictionary<(string Type, Guid Id), string>> ResolveAsync(
        IAgcoworkDbContext db,
        IReadOnlyList<ActivityEvent> events,
        CancellationToken cancellationToken)
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
}
