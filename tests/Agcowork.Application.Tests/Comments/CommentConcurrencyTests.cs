using Agcowork.Application.Activity;
using Agcowork.Application.Comments;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Tests.Collections;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Agcowork.Application.Tests.Comments;

/// <summary>
/// The lock order of comment writes (collection → record FOR SHARE → comment FOR UPDATE) under real concurrency: right before one
/// use case saves (it holds its locks), a second request starts on its own connection, blocks on a lock, and continues after the
/// first one committed. It then sees the committed state, never a stale one.
/// </summary>
public sealed class CommentConcurrencyTests(PostgresFixture postgres) : CommentTestBase(postgres)
{
    private static readonly TimeSpan BlockTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ReplyWhileRootIsDeleted_ReplyWaits_ThenConflict_NoOrphanReply()
    {
        var d = await ArrangeDiscussionAsync();
        var root = await AddAsync(d, "Wurzel");
        var owner = As(d, d.A.Seed.AlphaOwner);
        Task<Result<CommentDto>>? reply = null;

        Db.ChangeTracker.Clear();
        var racing = new RacingDbContext(Db, async () =>
        {
            reply = Task.Run(() => OtherAsync(owner, (db, recorder) =>
                new AddComment(db, TestActor.Agent(owner.Membership.ActorId), owner, recorder, Clock).HandleAsync(new AddCommentRequest(d.Record.Id, "Zu spät", root.Id), Ct)));
            await WaitUntilBlockedAsync();
        });
        var deleted = await new DeleteComment(racing, TestActor.Agent(owner.Membership.ActorId), owner, Activity, Clock).HandleAsync(new DeleteCommentRequest(root.Id), Ct);

        Assert.True(deleted.IsSuccess, Describe(deleted.Error));
        Assert.Equal("comment_deleted", Assert.IsType<ConflictError>((await reply!).Error).Code);
        Assert.Equal(1, await Db.Comments.CountAsync(Ct));
        Assert.Empty((await ListAsync(owner, d.Record.Id)).Threads);
    }

    [Fact]
    public async Task CommentWhileRecordIsArchived_CommentWaits_ThenConflict()
    {
        var d = await ArrangeDiscussionAsync();
        var owner = As(d, d.A.Seed.AlphaOwner);
        Task<Result<CommentDto>>? comment = null;

        Db.ChangeTracker.Clear();
        var racing = new RacingDbContext(Db, async () =>
        {
            comment = Task.Run(() => OtherAsync(owner, (db, recorder) =>
                new AddComment(db, TestActor.Agent(owner.Membership.ActorId), owner, recorder, Clock).HandleAsync(new AddCommentRequest(d.Record.Id, "Während des Archivierens"), Ct)));
            await WaitUntilBlockedAsync();
        });
        var archived = await new ArchiveRecord(racing, TestActor.Agent(owner.Membership.ActorId), owner, Activity, Clock).HandleAsync(new ArchiveRecordRequest(d.Record.Id), Ct);

        Assert.True(archived.IsSuccess, Describe(archived.Error));
        Assert.Equal("record_archived", Assert.IsType<ConflictError>((await comment!).Error).Code);
        Assert.Equal(0, await Db.Comments.CountAsync(Ct));
    }

    [Fact]
    public async Task ArchiveWhileCommentIsWritten_ArchiveWaits_CommentStoredBefore()
    {
        var d = await ArrangeDiscussionAsync();
        var owner = As(d, d.A.Seed.AlphaOwner);
        Task<Result<RecordDto>>? archive = null;

        Db.ChangeTracker.Clear();
        var racing = new RacingDbContext(Db, async () =>
        {
            archive = Task.Run(() => OtherAsync(owner, (db, recorder) =>
                new ArchiveRecord(db, TestActor.Agent(owner.Membership.ActorId), owner, recorder, Clock).HandleAsync(new ArchiveRecordRequest(d.Record.Id), Ct)));
            await WaitUntilBlockedAsync();
        });
        var comment = await new AddComment(racing, TestActor.Agent(owner.Membership.ActorId), owner, Activity, Clock).HandleAsync(new AddCommentRequest(d.Record.Id, "Gerade noch"), Ct);

        Assert.True(comment.IsSuccess, Describe(comment.Error));
        Assert.True((await archive!).IsSuccess);
        Assert.Equal("Gerade noch", Assert.Single((await ListAsync(owner, d.Record.Id)).Threads).Markdown);
        Assert.NotNull((await Db.Records.AsNoTracking().SingleAsync(r => r.Id == d.Record.Id, Ct)).ArchivedAt);
    }

    [Fact]
    public async Task TwoEditsWithMentionSync_SecondWaits_NoUniqueViolation_LastTextWins()
    {
        var d = await ArrangeDiscussionAsync();
        var x = d.A.Seed.MemberOfBoth;
        var y = await AddMemberAsync(d, "Ypsilon");
        var z = await AddMemberAsync(d, "Zett");
        var comment = await AddAsync(d, $"An {Mention(x)}");
        var owner = As(d, d.A.Seed.AlphaOwner);
        Task<Result<CommentDto>>? second = null;

        Db.ChangeTracker.Clear();
        var racing = new RacingDbContext(Db, async () =>
        {
            second = Task.Run(() => OtherAsync(owner, (db, recorder) =>
                new UpdateComment(db, TestActor.Agent(owner.Membership.ActorId), owner, recorder, Clock).HandleAsync(new UpdateCommentRequest(comment.Id, $"Zweiter: {Mention(y)} und {Mention(z)}"), Ct)));
            await WaitUntilBlockedAsync();
        });
        var first = await new UpdateComment(racing, TestActor.Agent(owner.Membership.ActorId), owner, Activity, Clock)
            .HandleAsync(new UpdateCommentRequest(comment.Id, $"Erster: {Mention(y)}"), Ct);

        Assert.True(first.IsSuccess, Describe(first.Error));
        var last = await second!;
        Assert.True(last.IsSuccess, Describe(last.Error));
        Db.ChangeTracker.Clear();
        Assert.Equal($"Zweiter: {Mention(y)} und {Mention(z)}", (await Db.Comments.AsNoTracking().SingleAsync(c => c.Id == comment.Id, Ct)).BodyMarkdown);
        var mentioned = await Db.Mentions.AsNoTracking().Where(m => m.CommentId == comment.Id).Select(m => m.MentionedActorId).ToListAsync(Ct);
        Assert.Equal(new[] { y.Id, z.Id }.Order(), mentioned.Order());
    }

    /// <summary>Runs a use case on its own connection (a concurrent request), in the tenant <paramref name="tenant"/>.</summary>
    private async Task<T> OtherAsync<T>(TenantContext tenant, Func<AgcoworkDbContext, IActivityRecorder, Task<T>> run)
    {
        var options = (DbContextOptions<AgcoworkDbContext>)Db.GetService<IDbContextOptions>();
        await using var other = new AgcoworkDbContext(options, new TestTenantAccessor { Current = tenant });
        return await run(other, new ActivityRecorder(other, TestActor.Agent(tenant.Membership.ActorId), tenant, Clock));
    }

    /// <summary>Waits until a session of this database waits for a lock (the concurrent request reached its lock).</summary>
    private async Task WaitUntilBlockedAsync()
    {
        // As the owner: the application role does not see the wait state of other sessions in pg_stat_activity.
        await using var probe = PostgresFixture.OpenOwner(Db);
        var deadline = DateTime.UtcNow + BlockTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var waiting = await probe.Database
                .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'")
                .SingleAsync(Ct);
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(20, Ct);
        }

        throw new TimeoutException("The concurrent request never waited for a lock: the lock it should take is missing.");
    }
}
