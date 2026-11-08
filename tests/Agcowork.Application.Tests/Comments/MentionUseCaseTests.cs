using Agcowork.Application.Comments;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Tests.Comments;

public sealed class MentionUseCaseTests(PostgresFixture postgres) : CommentTestBase(postgres)
{
    [Fact]
    public async Task List_OnlyOwnMentions_NewestFirst_WithTitleExcerptAndMentioner()
    {
        var d = await ArrangeDiscussionAsync();
        var both = d.A.Seed.MemberOfBoth;
        var owner = d.A.Seed.AlphaOwner;
        var root = await AddAsync(d, $"## Frage\n\n{Mention(both)} **bitte** prüfen");
        await AddAsync(d, $"Nur für {Mention(owner)}", actor: both);
        var reply = await AddAsync(d, $"Nachtrag für {Mention(both)}", root.Id);

        var mine = await MentionsAsync(As(d, both));

        Assert.Equal([reply.Id, root.Id], mine.Items.Select(m => m.CommentId));
        Assert.Equal(2, mine.UnreadCount);
        var first = mine.Items[1];
        Assert.Equal(("Datenbank wählen", d.Record.Id, owner.Id, "Alpha owner"), (first.RecordTitle, first.RecordId, first.ByActorId, first.ByActorName));
        Assert.Equal("Frage @Both bitte prüfen", first.Excerpt);
        Assert.Null(first.ParentCommentId);
        Assert.Equal(root.Id, mine.Items[0].ParentCommentId);
        Assert.Single((await MentionsAsync(As(d, owner))).Items);
    }

    [Fact]
    public async Task List_UnreadOnly_Pagination_NoDuplicates_UnreadCountIndependent()
    {
        var d = await ArrangeDiscussionAsync();
        var both = d.A.Seed.MemberOfBoth;
        for (var i = 0; i < 5; i++)
        {
            await AddAsync(d, $"#{i} {Mention(both)}");
        }

        var tenant = As(d, both);
        var all = (await MentionsAsync(tenant)).Items;
        Assert.True((await TryMarkReadAsync(tenant, new MarkMentionsReadRequest([all[0].Id, all[3].Id]))).IsSuccess);

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await MentionsAsync(tenant, new ListMentionsRequest(UnreadOnly: false, Limit: 2, Cursor: cursor));
            Assert.Equal(3, page.UnreadCount);
            seen.AddRange(page.Items.Select(m => m.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        var unread = await MentionsAsync(tenant, new ListMentionsRequest(UnreadOnly: true, Limit: 2));
        Assert.Equal(all.Select(m => m.Id), seen);
        Assert.Equal([all[1].Id, all[2].Id], unread.Items.Select(m => m.Id));
        Assert.NotNull(unread.NextCursor);
        Assert.Equal(3, unread.UnreadCount);
    }

    [Fact]
    public async Task List_InvalidCursorOrLimit_ValidationError()
    {
        var d = await ArrangeDiscussionAsync();

        Assert.Equal(ErrorCodes.InvalidFormat, FieldOf(await TryMentionsAsync(As(d, d.A.Seed.AlphaOwner), new ListMentionsRequest(Cursor: "kaputt")), "cursor").Code);
        Assert.Equal(ErrorCodes.OutOfRange, FieldOf(await TryMentionsAsync(As(d, d.A.Seed.AlphaOwner), new ListMentionsRequest(Limit: 0)), "limit").Code);
    }

    [Fact]
    public async Task List_MentionsOfOtherWorkspace_NeverAppear()
    {
        var d = await ArrangeDiscussionAsync();
        var both = d.A.Seed.MemberOfBoth;
        await AddAsync(d, $"Alpha an {Mention(both)}");

        // Beta: its owner mentions the same actor on a record of Beta.
        var beta = Enter(d.A.Seed.Beta, d.A.Seed.BetaOwner);
        Db.ChangeTracker.Clear();
        var betaRecord = await CreateUseCase(beta, d.A.Seed.BetaOwner).HandleAsync(new CreateRecordRequest(SpaceId: await BetaSpaceAsync(beta), Title: "Beta"), Ct);
        Assert.True(betaRecord.IsSuccess, Describe(betaRecord.Error));
        Db.ChangeTracker.Clear();
        var betaComment = await new AddComment(Db, TestActor.Agent(d.A.Seed.BetaOwner.Id), beta, Activity, Clock)
            .HandleAsync(new AddCommentRequest(betaRecord.Value.Id, $"Beta an {Mention(both)}"), Ct);
        Assert.True(betaComment.IsSuccess, Describe(betaComment.Error));

        var inAlpha = await MentionsAsync(As(d, both));
        var inBeta = await MentionsAsync(Enter(d.A.Seed.Beta, both));

        Assert.Equal("Datenbank wählen", Assert.Single(inAlpha.Items).RecordTitle);
        Assert.Equal(1, inAlpha.UnreadCount);
        Assert.Equal("Beta", Assert.Single(inBeta.Items).RecordTitle);

        // Marking Beta's mention through Alpha changes nothing.
        var foreign = await TryMarkReadAsync(As(d, both), new MarkMentionsReadRequest([inBeta.Items[0].Id]));
        Assert.Equal(0, foreign.Value.Marked);
        Assert.Equal(1, (await MentionsAsync(Enter(d.A.Seed.Beta, both))).UnreadCount);
    }

    [Fact]
    public async Task MarkRead_OwnIdsOnly_OthersIgnored_All_Validation()
    {
        var d = await ArrangeDiscussionAsync();
        var both = d.A.Seed.MemberOfBoth;
        var owner = d.A.Seed.AlphaOwner;
        await AddAsync(d, $"a {Mention(both)}");
        await AddAsync(d, $"b {Mention(both)}");
        await AddAsync(d, $"c {Mention(owner)}", actor: both);
        var ownerMention = Assert.Single((await MentionsAsync(As(d, owner))).Items);
        var bothTenant = As(d, both);
        var mine = (await MentionsAsync(bothTenant)).Items;

        var withForeign = await TryMarkReadAsync(bothTenant, new MarkMentionsReadRequest([mine[0].Id, ownerMention.Id, Guid.CreateVersion7()]));
        var again = await TryMarkReadAsync(bothTenant, new MarkMentionsReadRequest([mine[0].Id]));
        var all = await TryMarkReadAsync(bothTenant, new MarkMentionsReadRequest(All: true));
        var neither = await TryMarkReadAsync(bothTenant, new MarkMentionsReadRequest());
        var both_ = await TryMarkReadAsync(bothTenant, new MarkMentionsReadRequest([mine[0].Id], All: true));

        Assert.Equal((1, 1), (withForeign.Value.Marked, withForeign.Value.UnreadCount));
        Assert.Equal(0, again.Value.Marked);
        Assert.Equal((1, 0), (all.Value.Marked, all.Value.UnreadCount));
        Assert.Equal(ErrorCodes.Required, FieldOf(neither, "ids").Code);
        Assert.Equal(ErrorCodes.Required, FieldOf(both_, "ids").Code);
        Assert.Null((await Db.Mentions.AsNoTracking().SingleAsync(m => m.Id == ownerMention.Id, Ct)).ReadAt);
    }

    [Fact]
    public async Task MarkRead_ViewerAndArchivedWorkspace_Allowed()
    {
        var d = await ArrangeDiscussionAsync();
        var both = d.A.Seed.MemberOfBoth;
        await AddAsync(d, $"a {Mention(both)}");

        await AddAsync(d, $"b {Mention(both)}");
        var inbox = (await MentionsAsync(As(d, both))).Items;

        var readOnlyKey = await TryMarkReadAsync(As(d, both, "read"), new MarkMentionsReadRequest([inbox[0].Id]));
        var archived = Tests.Tenancy.TenancySeed.TenantFor(Db, d.A.Seed.Alpha, both, archived: true);
        Tenant.Current = archived;
        var inArchived = await TryMarkReadAsync(archived, new MarkMentionsReadRequest(All: true));
        var commentInArchived = await TryAddAsync(archived, new AddCommentRequest(d.Record.Id, "Nein"));

        Assert.Equal(1, readOnlyKey.Value.Marked);
        Assert.Equal((1, 0), (inArchived.Value.Marked, inArchived.Value.UnreadCount));
        Assert.Equal("workspace_archived", commentInArchived.Error!.Code);
    }

    [Fact]
    public async Task DeletedComment_LeavesInbox()
    {
        var d = await ArrangeDiscussionAsync();
        var both = d.A.Seed.MemberOfBoth;
        var comment = await AddAsync(d, $"a {Mention(both)}");

        Assert.True((await TryDeleteAsync(As(d, d.A.Seed.AlphaOwner), comment.Id)).IsSuccess);

        var inbox = await MentionsAsync(As(d, both));
        Assert.Empty(inbox.Items);
        Assert.Equal(0, inbox.UnreadCount);
    }

    private async Task<Guid> BetaSpaceAsync(Application.Tenancy.TenantContext beta) => (await CreateSpaceAsync(beta, (await Db.Actors.AsNoTracking().SingleAsync(a => a.Id == beta.Membership.ActorId, Ct)))).Id;
}
