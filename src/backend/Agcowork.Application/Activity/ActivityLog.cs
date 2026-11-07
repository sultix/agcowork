using Agcowork.Domain.Activity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Activity;

/// <summary>Position of an event in the commit-safe order of the activity log: (<see cref="ActivityEvent.XactId"/>, <see cref="ActivityEvent.Seq"/>).</summary>
/// <param name="XactId">Transaction id of the event.</param>
/// <param name="Seq">Sequence number of the event.</param>
public readonly record struct ActivityPosition(ulong XactId, long Seq)
{
    /// <summary>Before the first event.</summary>
    public static ActivityPosition Start => default;
}

/// <summary>
/// Forward reading of the activity log without gaps (AP-024). <c>seq</c> is assigned on insert, not on commit: two transactions
/// can make seq 11 visible before seq 10. Forward readers therefore
/// <list type="number">
/// <item>read only <b>stable</b> events (<see cref="Stable"/>): their transaction id is below the oldest running transaction
/// (<c>xact_id &lt; pg_snapshot_xmin(pg_current_snapshot())</c>), so they are final;</item>
/// <item>order by (<c>xact_id</c>, <c>seq</c>) (<see cref="InCommitOrder"/>), not by <c>seq</c> alone, and continue after the
/// position of the last event they read (<see cref="After"/>).</item>
/// </list>
/// Every transaction that has not finished has an id of at least that horizon, so whatever becomes visible later sorts behind
/// everything a reader has passed. Ordering by <c>seq</c> alone would not be safe even with the stability check: a transaction can
/// get its id early (e.g. with a row lock), insert its event after a younger transaction did, and commit first; its higher
/// <c>seq</c> would be stable while the lower one is still running. A long-running writing transaction delays delivery, which is
/// accepted.
/// </summary>
public static class ActivityLog
{
    /// <summary>
    /// <c>pg_snapshot_xmin(pg_current_snapshot())</c>: the oldest transaction still running when the query's snapshot was taken.
    /// For LINQ queries only (translated by the persistence layer); throws when called in .NET.
    /// </summary>
    public static ulong StableHorizon() => throw new InvalidOperationException("StableHorizon can only be used in LINQ queries against the database.");

    /// <summary>Only events whose transaction is finished and older than every running one.</summary>
    public static IQueryable<ActivityEvent> Stable(this IQueryable<ActivityEvent> events) =>
        events.Where(e => e.XactId < StableHorizon());

    /// <summary>Events behind <paramref name="position"/> in the commit-safe order: <c>(xact_id, seq) &gt; (x, s)</c>.</summary>
    /// <remarks>
    /// Written as <c>xact_id &gt;= x AND (xact_id &gt; x OR seq &gt; s)</c>: the leading range condition is the index bound, so
    /// the scan of <c>(…, xact_id, seq)</c> starts at the position instead of the beginning of the log (a plain OR gives the
    /// planner no range, also not in generic plans). Provider-neutral, unlike a row-value comparison.
    /// </remarks>
    public static IQueryable<ActivityEvent> After(this IQueryable<ActivityEvent> events, ActivityPosition position)
    {
        var xactId = position.XactId;
        var seq = position.Seq;
        return events.Where(e => e.XactId >= xactId && (e.XactId > xactId || e.Seq > seq));
    }

    /// <summary>The commit-safe order (<c>xact_id</c>, <c>seq</c>), ascending.</summary>
    public static IOrderedQueryable<ActivityEvent> InCommitOrder(this IQueryable<ActivityEvent> events) =>
        events.OrderBy(e => e.XactId).ThenBy(e => e.Seq);

    /// <summary>
    /// The last stable event of <paramref name="events"/> in the commit-safe order, or <see cref="ActivityPosition.Start"/>:
    /// a forward reader starting there gets every event that becomes final from now on, and none of the old ones.
    /// </summary>
    public static async Task<ActivityPosition> HeadAsync(this IQueryable<ActivityEvent> events, CancellationToken cancellationToken)
    {
        var head = await events.Stable()
            .OrderByDescending(e => e.XactId).ThenByDescending(e => e.Seq)
            .Select(e => new { e.XactId, e.Seq })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return head is null ? ActivityPosition.Start : new ActivityPosition(head.XactId, head.Seq);
    }

    /// <summary>Position of an event.</summary>
    public static ActivityPosition PositionOf(ActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return new ActivityPosition(activity.XactId, activity.Seq);
    }
}
