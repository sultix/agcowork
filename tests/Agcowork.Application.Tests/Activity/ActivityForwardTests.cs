using System.Data.Common;
using Agcowork.Application.Activity;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Agcowork.Application.Tests.Records;
using Agcowork.Domain.Activity;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Agcowork.Application.Tests.Activity;

/// <summary>
/// Forward reading with <c>afterSeq</c> (AP-024): only final events, in commit-safe order, so polling with <c>lastSeq</c> never
/// skips an event, also when transactions commit in another order than their seq. Concurrency runs on real separate connections.
/// </summary>
public sealed class ActivityForwardTests(PostgresFixture postgres) : RecordTestBase(postgres)
{
    private async Task<ActivityPage> ListAsync(Arranged a, ListActivityRequest request)
    {
        var result = await new ListActivity(Db, a.Tenant).HandleAsync(request, Ct);
        Assert.True(result.IsSuccess, Describe(result.Error));
        return result.Value;
    }

    private Task<ActivityPage> ForwardAsync(Arranged a, long afterSeq, int limit = 200) =>
        ListAsync(a, new ListActivityRequest(AfterSeq: afterSeq, Limit: limit));

    /// <summary>Another context on the test database with its own connection: a concurrent request.</summary>
    private AgcoworkDbContext OpenConnection() =>
        new((DbContextOptions<AgcoworkDbContext>)Db.GetService<IDbContextOptions>(), Tenant);

    private static async Task<ActivityEvent> WriteEventAsync(AgcoworkDbContext db, Arranged a)
    {
        var activity = ActivityEvent.Create(
            a.Seed.Alpha.Id, ActivityTypes.RecordUpdated, a.Seed.AlphaOwner.Id, ActivityTargets.Record, Guid.CreateVersion7(), null, null, null, TimeProvider.System);
        db.ActivityEvents.Add(activity);
        await db.SaveChangesAsync(Ct);
        return activity;
    }

    [Fact]
    public async Task Forward_EarlierTransactionStillOpen_LaterCommitHeldBackUntilItCommits()
    {
        var a = await ArrangeAsync();
        var start = (await ForwardAsync(a, 0)).LastSeq;

        await using var dbA = OpenConnection();
        await using var transactionA = await dbA.Database.BeginTransactionAsync(Ct);
        var eventA = await WriteEventAsync(dbA, a);
        await using var dbB = OpenConnection();
        var eventB = await WriteEventAsync(dbB, a);
        Assert.Equal(eventA.Seq + 1, eventB.Seq);

        var whileOpen = await ForwardAsync(a, start);
        var newestFirst = await ListAsync(a, new ListActivityRequest());

        Assert.Empty(whileOpen.Items);
        Assert.Equal(start, whileOpen.LastSeq);
        // The newest-first listing shows B already, but does not offer it as the place to continue from.
        Assert.Equal(eventB.Seq, newestFirst.Items[0].Seq);
        Assert.Equal(start, newestFirst.LastSeq);

        await transactionA.CommitAsync(Ct);
        var afterCommit = await ForwardAsync(a, start);

        Assert.Equal([eventA.Seq, eventB.Seq], afterCommit.Items.Select(e => e.Seq));
        Assert.Equal(eventB.Seq, afterCommit.LastSeq);
    }

    [Fact]
    public async Task Forward_EarlierTransactionRolledBack_LaterCommitDelivered()
    {
        var a = await ArrangeAsync();
        var start = (await ForwardAsync(a, 0)).LastSeq;

        await using var dbA = OpenConnection();
        await using var transactionA = await dbA.Database.BeginTransactionAsync(Ct);
        await WriteEventAsync(dbA, a);
        await using var dbB = OpenConnection();
        var eventB = await WriteEventAsync(dbB, a);
        Assert.Empty((await ForwardAsync(a, start)).Items);

        await transactionA.RollbackAsync(Ct);
        var afterRollback = await ForwardAsync(a, start);

        Assert.Equal([eventB.Seq], afterRollback.Items.Select(e => e.Seq));
    }

    [Fact]
    public async Task Forward_TransactionWithEarlierIdCommitsHigherSeqFirst_LowerSeqStillFollows()
    {
        // A gets its transaction id first (like a use case taking a row lock), B then writes seq n and stays open, A writes
        // n + 1 and commits. A is final while B runs: it is delivered first, and B's lower seq must still come afterwards.
        // Reading "seq > lastSeq" would lose it; the commit-safe order does not.
        var a = await ArrangeAsync();
        var start = (await ForwardAsync(a, 0)).LastSeq;

        await using var dbA = OpenConnection();
        await using var transactionA = await dbA.Database.BeginTransactionAsync(Ct);
        await dbA.Database.ExecuteSqlAsync($"SELECT pg_current_xact_id()", Ct);
        await using var dbB = OpenConnection();
        await using var transactionB = await dbB.Database.BeginTransactionAsync(Ct);
        var eventB = await WriteEventAsync(dbB, a);
        var eventA = await WriteEventAsync(dbA, a);
        await transactionA.CommitAsync(Ct);
        Assert.True(eventA.Seq > eventB.Seq);
        Assert.True(eventA.XactId < eventB.XactId);

        var first = await ForwardAsync(a, start);
        Assert.Equal([eventA.Seq], first.Items.Select(e => e.Seq));
        Assert.Equal(eventA.Seq, first.LastSeq);

        await transactionB.CommitAsync(Ct);
        var second = await ForwardAsync(a, first.LastSeq);

        Assert.Equal([eventB.Seq], second.Items.Select(e => e.Seq));
        Assert.Equal(eventB.Seq, second.LastSeq);
        Assert.Empty((await ForwardAsync(a, second.LastSeq)).Items);
    }

    [Fact]
    public async Task Forward_AfterSeqOfEventNotFinalYet_ContinuesFromNewestFinalEvent()
    {
        // B is visible in the newest-first listing while the older A is still open. Continuing after B would skip A.
        var a = await ArrangeAsync();
        var start = (await ForwardAsync(a, 0)).LastSeq;
        await using var dbA = OpenConnection();
        await using var transactionA = await dbA.Database.BeginTransactionAsync(Ct);
        var eventA = await WriteEventAsync(dbA, a);
        await using var dbB = OpenConnection();
        var eventB = await WriteEventAsync(dbB, a);

        var fromB = await ForwardAsync(a, eventB.Seq);
        Assert.Empty(fromB.Items);
        Assert.Equal(start, fromB.LastSeq);

        await transactionA.CommitAsync(Ct);

        Assert.Equal([eventA.Seq, eventB.Seq], (await ForwardAsync(a, fromB.LastSeq)).Items.Select(e => e.Seq));
    }

    [Fact]
    public async Task Forward_Ascending_RespectsLimit_LastSeqContinues()
    {
        var a = await ArrangeAsync();
        for (var i = 0; i < 4; i++)
        {
            await CreateRecordAsync(a, $"R{i}");
        }

        var newestFirst = await ListAsync(a, new ListActivityRequest(Limit: 200));
        var seen = new List<long>();
        var lastSeq = 0L;
        while (true)
        {
            var page = await ForwardAsync(a, lastSeq, limit: 2);
            Assert.True(page.Items.Count <= 2);
            Assert.Null(page.NextCursor);
            if (page.Items.Count == 0)
            {
                Assert.Equal(lastSeq, page.LastSeq);
                break;
            }

            Assert.Equal(page.Items[^1].Seq, page.LastSeq);
            seen.AddRange(page.Items.Select(e => e.Seq));
            lastSeq = page.LastSeq;
        }

        Assert.Equal(newestFirst.Items.Select(e => e.Seq).Reverse(), seen);
        Assert.Equal(6, seen.Count);
        Assert.Equal(seen[^1], newestFirst.LastSeq);
        Assert.Equal("R3", (await ForwardAsync(a, seen[^2])).Items.Single().TargetTitle);
    }

    [Fact]
    public async Task List_TargetId_OnlyEventsOfThatTarget_BothModes()
    {
        var a = await ArrangeAsync();
        var first = await CreateRecordAsync(a, "Eins");
        var second = await CreateRecordAsync(a, "Zwei");
        Assert.True((await UpdateAsync(a, new UpdateRecordRequest(first.Id, Title: "Eins neu"))).IsSuccess);

        var newestFirst = await ListAsync(a, new ListActivityRequest(TargetId: first.Id));
        var forward = await ListAsync(a, new ListActivityRequest(AfterSeq: 0, TargetId: first.Id));
        var collection = await ListAsync(a, new ListActivityRequest(TargetId: a.Collection.Id));

        Assert.Equal([ActivityTypes.RecordUpdated, ActivityTypes.RecordCreated], newestFirst.Items.Select(e => e.Type));
        Assert.All(newestFirst.Items, e => Assert.Equal((first.Id, "Eins neu"), (e.TargetId, e.TargetTitle)));
        Assert.Equal(newestFirst.Items.Select(e => e.Seq).Reverse(), forward.Items.Select(e => e.Seq));
        Assert.DoesNotContain(forward.Items, e => e.TargetId == second.Id);
        Assert.Equal([ActivityTypes.CollectionCreated], collection.Items.Select(e => e.Type));
    }

    [Fact]
    public async Task Forward_InvalidInput_FieldErrors()
    {
        var a = await ArrangeAsync();
        var beta = Enter(a.Seed.Beta, a.Seed.BetaOwner);
        var betaSpace = await CreateSpaceAsync(beta, a.Seed.BetaOwner, "Beta");
        var betaSeq = (await new ListActivity(Db, beta).HandleAsync(new ListActivityRequest(SpaceId: betaSpace.Id), Ct)).Value.Items[0].Seq;
        Enter(a.Seed.Alpha, a.Seed.AlphaOwner);

        async Task<ValidationError> InvalidAsync(ListActivityRequest request) =>
            Assert.IsType<ValidationError>((await new ListActivity(Db, a.Tenant).HandleAsync(request, Ct)).Error);

        var negative = await InvalidAsync(new ListActivityRequest(AfterSeq: -1));
        var both = await InvalidAsync(new ListActivityRequest(AfterSeq: 0, Cursor: "MQ"));
        var unknown = await InvalidAsync(new ListActivityRequest(AfterSeq: long.MaxValue));
        var foreign = await InvalidAsync(new ListActivityRequest(AfterSeq: betaSeq));

        Assert.Equal(("afterSeq", ErrorCodes.OutOfRange), (negative.Errors[0].Field, negative.Errors[0].Code));
        Assert.Equal(("cursor", ErrorCodes.NotAllowed), (both.Errors[0].Field, both.Errors[0].Code));
        Assert.Equal(("afterSeq", ErrorCodes.InvalidCursor), (unknown.Errors[0].Field, unknown.Errors[0].Code));
        Assert.Equal(unknown.Errors, foreign.Errors);
    }

    [Fact]
    public async Task NewestFirst_EventBecomesFinalDuringTheCall_LastSeqDoesNotSkipIt()
    {
        // A (older) is open, B committed: B is visible but not final. A commits between the two queries of the newest-first
        // call. Whatever the page shows, forward reading from its lastSeq must deliver A, which the page may not contain.
        var a = await ArrangeAsync();
        await using var dbA = OpenConnection();
        var transactionA = await dbA.Database.BeginTransactionAsync(Ct);
        var eventA = await WriteEventAsync(dbA, a);
        await using var dbB = OpenConnection();
        var eventB = await WriteEventAsync(dbB, a);

        var commitAfterFirstActivityQuery = new AfterFirstActivityQuery(async () =>
        {
            await transactionA.CommitAsync(Ct);
            await transactionA.DisposeAsync();
        });
        await using var reader = new AgcoworkDbContext(
            new DbContextOptionsBuilder<AgcoworkDbContext>((DbContextOptions<AgcoworkDbContext>)Db.GetService<IDbContextOptions>())
                .AddInterceptors(commitAfterFirstActivityQuery)
                .Options,
            Tenant);
        var newestFirst = await new ListActivity(reader, a.Tenant).HandleAsync(new ListActivityRequest(), Ct);
        Assert.True(commitAfterFirstActivityQuery.Fired);

        var forward = await ForwardAsync(a, newestFirst.Value.LastSeq);
        var seen = newestFirst.Value.Items.Select(e => e.Seq).Concat(forward.Items.Select(e => e.Seq)).ToHashSet();

        Assert.Contains(eventA.Seq, seen);
        Assert.Contains(eventB.Seq, seen);
        Assert.Contains(eventA.Seq, forward.Items.Select(e => e.Seq));
    }

    /// <summary>Runs <paramref name="action"/> once, right after the first query on <c>activity_events</c> has been read.</summary>
    private sealed class AfterFirstActivityQuery(Func<Task> action) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Fired || !command.CommandText.Contains("activity_events", StringComparison.Ordinal))
            {
                return result;
            }

            // The query's snapshot is taken; the commit runs on another connection while this result is still open.
            Fired = true;
            await action();
            return result;
        }
    }
}
