using Agcowork.Application.Activity;
using Agcowork.Application.Comments;
using Agcowork.Application.Records;
using Agcowork.Application.Tests.Comments;
using Agcowork.Domain.Activity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Agcowork.Application.Tests.Activity;

/// <summary>The history of one record including its comments (AP-035): <c>recordId</c> filter and backfill.</summary>
public sealed class RecordActivityTests(PostgresFixture postgres) : CommentTestBase(postgres)
{
    private async Task<ActivityPage> ListAsync(Discussion d, ListActivityRequest request)
    {
        Db.ChangeTracker.Clear();
        var result = await new ListActivity(Db, As(d, d.A.Seed.AlphaOwner)).HandleAsync(request, Ct);
        Assert.True(result.IsSuccess, Describe(result.Error));
        return result.Value;
    }

    /// <summary>The record of the discussion with an update, a thread with a reply and a resolve; another record with a comment.</summary>
    private async Task<(Discussion D, RecordDto Other)> ArrangeHistoryAsync()
    {
        var d = await ArrangeDiscussionAsync();
        Assert.True((await UpdateAsync(d.A, new UpdateRecordRequest(d.Record.Id, Title: "Datenbank gewählt"))).IsSuccess);
        var root = await AddAsync(d, "Warum Postgres?");
        await AddAsync(d, "Wegen jsonb.", root.Id);
        Assert.True((await TryUpdateAsync(As(d, d.A.Seed.AlphaOwner), new UpdateCommentRequest(root.Id, Resolved: true))).IsSuccess);
        var other = await CreateRecordAsync(d.A, "Anderer Record");
        Assert.True((await TryAddAsync(As(d, d.A.Seed.AlphaOwner), new AddCommentRequest(other.Id, "Woanders"))).IsSuccess);
        return (d, other);
    }

    [Fact]
    public async Task List_RecordId_RecordAndCommentEvents_NewestFirst()
    {
        var (d, _) = await ArrangeHistoryAsync();

        var page = await ListAsync(d, new ListActivityRequest(RecordId: d.Record.Id));

        Assert.Equal(
            [ActivityTypes.CommentResolved, ActivityTypes.CommentCreated, ActivityTypes.CommentCreated, ActivityTypes.RecordUpdated, ActivityTypes.RecordCreated],
            page.Items.Select(e => e.Type));
        Assert.All(page.Items.Where(e => e.TargetType == ActivityTargets.Comment), e => Assert.Equal(d.Record.Id.ToString(), e.Diff["recordId"]!.GetValue<string>()));
        Assert.All(page.Items.Where(e => e.TargetType == ActivityTargets.Record), e => Assert.Equal(d.Record.Id, e.TargetId));

        // Paging newest first keeps the filter.
        var first = await ListAsync(d, new ListActivityRequest(RecordId: d.Record.Id, Limit: 2));
        var second = await ListAsync(d, new ListActivityRequest(RecordId: d.Record.Id, Limit: 2, Cursor: first.NextCursor));
        Assert.Equal(page.Items.Take(4).Select(e => e.Id), first.Items.Concat(second.Items).Select(e => e.Id));
    }

    [Fact]
    public async Task List_RecordId_Forward_SameEventsInCommitOrder()
    {
        var (d, _) = await ArrangeHistoryAsync();
        var newestFirst = await ListAsync(d, new ListActivityRequest(RecordId: d.Record.Id));

        var forward = await ListAsync(d, new ListActivityRequest(RecordId: d.Record.Id, AfterSeq: 0, Limit: 3));
        var rest = await ListAsync(d, new ListActivityRequest(RecordId: d.Record.Id, AfterSeq: forward.LastSeq));

        Assert.Equal(newestFirst.Items.Reverse().Select(e => e.Id), forward.Items.Concat(rest.Items).Select(e => e.Id));

        // A new comment shows up when reading on from lastSeq.
        await AddAsync(d, "Nachtrag");
        var next = await ListAsync(d, new ListActivityRequest(RecordId: d.Record.Id, AfterSeq: rest.LastSeq));
        Assert.Equal(ActivityTypes.CommentCreated, Assert.Single(next.Items).Type);
    }

    [Fact]
    public async Task List_RecordId_CombinesWithOtherFilters()
    {
        var (d, other) = await ArrangeHistoryAsync();

        var commentsOnly = await ListAsync(d, new ListActivityRequest(RecordId: d.Record.Id, Types: [ActivityTypes.CommentCreated]));
        var recordOnly = await ListAsync(d, new ListActivityRequest(RecordId: d.Record.Id, TargetId: d.Record.Id));
        var mismatch = await ListAsync(d, new ListActivityRequest(RecordId: d.Record.Id, TargetId: other.Id));
        var otherRecord = await ListAsync(d, new ListActivityRequest(RecordId: other.Id));

        Assert.Equal(2, commentsOnly.Items.Count);
        Assert.Equal([ActivityTypes.RecordUpdated, ActivityTypes.RecordCreated], recordOnly.Items.Select(e => e.Type));
        Assert.Empty(mismatch.Items);
        Assert.Equal([ActivityTypes.CommentCreated, ActivityTypes.RecordCreated], otherRecord.Items.Select(e => e.Type));
    }

    [Fact]
    public async Task List_RecordIdOfOtherWorkspace_Empty()
    {
        var (d, _) = await ArrangeHistoryAsync();
        var beta = Enter(d.A.Seed.Beta, d.A.Seed.BetaOwner);
        var betaSpace = await CreateSpaceAsync(beta, d.A.Seed.BetaOwner, "Beta-Geheim");
        Db.ChangeTracker.Clear();
        var betaRecord = await CreateUseCase(beta, d.A.Seed.BetaOwner).HandleAsync(new CreateRecordRequest(SpaceId: betaSpace.Id, Title: "Geheim"), Ct);
        Assert.True(betaRecord.IsSuccess);

        var newestFirst = await ListAsync(d, new ListActivityRequest(RecordId: betaRecord.Value.Id));
        var forward = await ListAsync(d, new ListActivityRequest(RecordId: betaRecord.Value.Id, AfterSeq: 0));

        Assert.Empty(newestFirst.Items);
        Assert.Empty(forward.Items);
    }

    [Fact]
    public async Task Migration_BackfillsRelatedRecordOfExistingEvents()
    {
        var (d, other) = await ArrangeHistoryAsync();
        var spoiled = await Db.ActivityEvents.AsNoTracking()
            .Where(e => e.TargetType == ActivityTargets.Comment && e.CollectionId == d.A.Collection.Id)
            .OrderBy(e => e.Seq).Select(e => e.Id).FirstAsync(Ct);
        // Migrations run as the owner, like at startup.
        await using var owner = PostgresFixture.OpenOwner(Db);
        var migrator = owner.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20261108082350_AddComments", Ct);

        // Existing data before the migration, including a comment event whose diff has no usable record id.
        await owner.Database.ExecuteSqlAsync($"UPDATE activity_events SET diff = jsonb_set(diff, '{{recordId}}', '\"kaputt\"') WHERE id = {spoiled}", Ct);
        await migrator.MigrateAsync(cancellationToken: Ct);
        Db.ChangeTracker.Clear();

        var events = await Db.ActivityEvents.AsNoTracking().ToListAsync(Ct);
        Assert.All(events.Where(e => e.TargetType == ActivityTargets.Record), e => Assert.Equal(e.TargetId, e.RelatedRecordId));
        Assert.All(
            events.Where(e => e.TargetType == ActivityTargets.Comment && e.Id != spoiled),
            e => Assert.Equal(Guid.Parse(e.Diff["recordId"]!.GetValue<string>()), e.RelatedRecordId));
        Assert.Null(events.Single(e => e.Id == spoiled).RelatedRecordId);
        Assert.All(events.Where(e => e.TargetType is not (ActivityTargets.Record or ActivityTargets.Comment)), e => Assert.Null(e.RelatedRecordId));
        Assert.Contains(events, e => e.TargetType == ActivityTargets.Comment && e.RelatedRecordId == other.Id);

        // The filter finds the backfilled events like new ones.
        var page = await ListAsync(d, new ListActivityRequest(RecordId: d.Record.Id));
        Assert.Equal(4, page.Items.Count);
        Assert.Equal(3, await Db.ActivityEvents.CountAsync(e => e.TargetType == ActivityTargets.Comment && e.RelatedRecordId != null, Ct));
    }
}
