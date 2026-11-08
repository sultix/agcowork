using Agcowork.Application.Activity;
using Agcowork.Application.Comments;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Tests.Comments;

public sealed class CommentUseCaseTests(PostgresFixture postgres) : CommentTestBase(postgres)
{
    [Fact]
    public async Task Add_RootAndReply_ListedAsThreadWithAuthors_NoRecordRevision()
    {
        var d = await ArrangeDiscussionAsync();
        var root = await AddAsync(d, "Postgres oder Mongo?");
        var reply = await AddAsync(d, "Postgres.", root.Id, d.A.Seed.MemberOfBoth);

        var threads = await ListAsync(As(d, d.A.Seed.AlphaOwner), d.Record.Id);

        var thread = Assert.Single(threads.Threads);
        Assert.Equal((root.Id, "Postgres oder Mongo?", "Alpha owner", ActorKind.Agent), (thread.Id, thread.Markdown, thread.AuthorName, thread.AuthorKind));
        var answer = Assert.Single(thread.Replies!);
        Assert.Equal((reply.Id, root.Id, d.A.Seed.MemberOfBoth.Id, "Both"), (answer.Id, answer.ParentCommentId, answer.AuthorId, answer.AuthorName));
        Assert.Null(answer.Replies);
        Assert.Equal(0, threads.OmittedThreads);
        Assert.Equal(1, (await Db.Records.AsNoTracking().SingleAsync(r => r.Id == d.Record.Id, Ct)).RevisionNo);
    }

    [Fact]
    public async Task Add_ReplyToReply_NotRoot_UnknownOrForeignParent_NotFoundField()
    {
        var d = await ArrangeDiscussionAsync();
        var root = await AddAsync(d, "Wurzel");
        var reply = await AddAsync(d, "Antwort", root.Id);
        var otherRecord = await CreateRecordAsync(d.A, "Anderer Record");
        var tenant = As(d, d.A.Seed.AlphaOwner);

        var nested = await TryAddAsync(tenant, new AddCommentRequest(d.Record.Id, "Tiefer", reply.Id));
        var unknown = await TryAddAsync(tenant, new AddCommentRequest(d.Record.Id, "?", Guid.CreateVersion7()));
        var elsewhere = await TryAddAsync(tenant, new AddCommentRequest(otherRecord.Id, "Falscher Record", root.Id));

        Assert.Equal(ErrorCodes.NotRoot, FieldOf(nested, "parentCommentId").Code);
        Assert.Equal(ErrorCodes.NotFound, FieldOf(unknown, "parentCommentId").Code);
        Assert.Equal(ErrorCodes.NotFound, FieldOf(elsewhere, "parentCommentId").Code);
    }

    [Fact]
    public async Task Add_UnknownRecord_NotFound_ReadOnlyKey_Forbidden()
    {
        var d = await ArrangeDiscussionAsync();

        Assert.IsType<NotFoundError>((await TryAddAsync(As(d, d.A.Seed.AlphaOwner), new AddCommentRequest(Guid.CreateVersion7(), "Hallo"))).Error);
        Assert.IsType<ForbiddenError>((await TryAddAsync(As(d, d.A.Seed.AlphaOwner, "read"), new AddCommentRequest(d.Record.Id, "Hallo"))).Error);
    }

    [Fact]
    public async Task Add_ArchivedRecord_Conflict_ListStillWorks()
    {
        var d = await ArrangeDiscussionAsync();
        await AddAsync(d, "Vorher");
        Db.ChangeTracker.Clear();
        Assert.True((await new ArchiveRecord(Db, TestActor.Agent(d.A.Seed.AlphaOwner.Id), d.A.Tenant, Activity, Clock).HandleAsync(new ArchiveRecordRequest(d.Record.Id), Ct)).IsSuccess);
        var tenant = As(d, d.A.Seed.AlphaOwner);

        var result = await TryAddAsync(tenant, new AddCommentRequest(d.Record.Id, "Nachher"));

        Assert.Equal("record_archived", Assert.IsType<ConflictError>(result.Error).Code);
        Assert.Single((await ListAsync(tenant, d.Record.Id)).Threads);
    }

    [Fact]
    public async Task AddAndUpdate_UnstorableText_InvalidValue()
    {
        // Built in code: theory data would replace the unpaired surrogate when it is serialized.
        var d = await ArrangeDiscussionAsync();
        var comment = await AddAsync(d, "Gut");
        var tenant = As(d, d.A.Seed.AlphaOwner);

        foreach (var markdown in new[] { "a\0b", "a\ud800b", "\udfff" })
        {
            Assert.Equal(ErrorCodes.InvalidValue, FieldOf(await TryAddAsync(tenant, new AddCommentRequest(d.Record.Id, markdown)), "markdown").Code);
            Assert.Equal(ErrorCodes.InvalidValue, FieldOf(await TryUpdateAsync(tenant, new UpdateCommentRequest(comment.Id, markdown)), "markdown").Code);
        }
    }

    [Fact]
    public async Task Add_EmptyOrTooLong_Rejected()
    {
        var d = await ArrangeDiscussionAsync();
        var tenant = As(d, d.A.Seed.AlphaOwner);

        Assert.Equal(ErrorCodes.Required, FieldOf(await TryAddAsync(tenant, new AddCommentRequest(d.Record.Id, "  ")), "markdown").Code);
        Assert.Equal(ErrorCodes.TooLong, FieldOf(await TryAddAsync(tenant, new AddCommentRequest(d.Record.Id, new string('x', (64 * 1024) + 1))), "markdown").Code);
    }

    [Fact]
    public async Task Add_Mentions_OneRowPerMember_SelfAndCodeIgnored()
    {
        var d = await ArrangeDiscussionAsync();
        var both = d.A.Seed.MemberOfBoth;
        var owner = d.A.Seed.AlphaOwner;

        var comment = await AddAsync(d, $"{Mention(both)} und nochmal {Mention(both)}, ich {Mention(owner)}, nicht `{Mention(both)}`");

        var mention = Assert.Single(await Db.Mentions.AsNoTracking().ToListAsync(Ct));
        Assert.Equal((comment.Id, d.Record.Id, both.Id, owner.Id), (mention.CommentId, mention.RecordId, mention.MentionedActorId, mention.ByActorId));
        Assert.Null(mention.ReadAt);
    }

    [Fact]
    public async Task Add_MentionOfNonMember_NotMemberWithId_NothingStored()
    {
        var d = await ArrangeDiscussionAsync();
        var outsider = d.A.Seed.BetaOwner;

        var result = await TryAddAsync(As(d, d.A.Seed.AlphaOwner), new AddCommentRequest(d.Record.Id, $"Hallo {Mention(outsider)}"));

        var error = FieldOf(result, "markdown");
        Assert.Equal(ErrorCodes.NotMember, error.Code);
        Assert.Contains(outsider.Id.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Equal(0, await Db.Comments.CountAsync(Ct));
    }

    [Fact]
    public async Task Add_MalformedActorLink_InvalidValue()
    {
        var d = await ArrangeDiscussionAsync();

        var result = await TryAddAsync(As(d, d.A.Seed.AlphaOwner), new AddCommentRequest(d.Record.Id, "Hallo [@x](actor:alice)"));

        Assert.Contains("actor:alice", FieldOf(result, "markdown").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_SyncsMentions_NewRemovedKept_KeepsReadAt()
    {
        var d = await ArrangeDiscussionAsync();
        var owner = d.A.Seed.AlphaOwner;
        var both = d.A.Seed.MemberOfBoth;
        var human = await AddMemberAsync(d, "Mensch");
        var comment = await AddAsync(d, $"{Mention(both)} und {Mention(human)}");
        var kept = await Db.Mentions.AsNoTracking().SingleAsync(m => m.MentionedActorId == both.Id, Ct);
        Assert.True((await TryMarkReadAsync(As(d, both), new MarkMentionsReadRequest([kept.Id]))).IsSuccess);
        var third = await AddMemberAsync(d, "Dritter");

        var updated = await TryUpdateAsync(As(d, owner), new UpdateCommentRequest(comment.Id, $"Nur noch {Mention(both)} und {Mention(third)}"));

        Assert.True(updated.IsSuccess, Describe(updated.Error));
        Assert.NotNull(updated.Value.EditedAt);
        var mentions = await Db.Mentions.AsNoTracking().OrderBy(m => m.CreatedAt).ToListAsync(Ct);
        Assert.Equal([both.Id, third.Id], mentions.Select(m => m.MentionedActorId));
        var still = mentions[0];
        Assert.Equal(kept.Id, still.Id);
        Assert.NotNull(still.ReadAt);
        Assert.Null(mentions[1].ReadAt);

        var events = await EventsAsync(ActivityTypes.CommentUpdated);
        Assert.Equal($"[\"{third.Id}\"]", Assert.Single(events).Diff["mentionedActorIds"]!.ToJsonString());
    }

    [Fact]
    public async Task Update_NotAuthor_Forbidden_DeletedComment_Conflict_Nothing_Required()
    {
        var d = await ArrangeDiscussionAsync();
        var comment = await AddAsync(d, "Mein Text");

        var foreign = await TryUpdateAsync(As(d, d.A.Seed.MemberOfBoth), new UpdateCommentRequest(comment.Id, "Fremd"));
        var nothing = await TryUpdateAsync(As(d, d.A.Seed.AlphaOwner), new UpdateCommentRequest(comment.Id));
        Assert.True((await TryDeleteAsync(As(d, d.A.Seed.AlphaOwner), comment.Id)).IsSuccess);
        var deleted = await TryUpdateAsync(As(d, d.A.Seed.AlphaOwner), new UpdateCommentRequest(comment.Id, "Wieder"));

        Assert.Equal(ErrorCodes.NotAuthor, Assert.IsType<ForbiddenError>(foreign.Error).Code);
        Assert.Equal(ErrorCodes.Required, FieldOf(nothing, "markdown").Code);
        Assert.Equal("comment_deleted", Assert.IsType<ConflictError>(deleted.Error).Code);
    }

    [Fact]
    public async Task Update_UnchangedText_NoEditNoEvent()
    {
        var d = await ArrangeDiscussionAsync();
        var comment = await AddAsync(d, "Gleich");

        var result = await TryUpdateAsync(As(d, d.A.Seed.AlphaOwner), new UpdateCommentRequest(comment.Id, "Gleich"));

        Assert.Null(result.Value.EditedAt);
        Assert.Empty(await EventsAsync(ActivityTypes.CommentUpdated));
    }

    [Fact]
    public async Task ResolveReopen_RootByAnyWriter_ReplyNotRoot_Events()
    {
        var d = await ArrangeDiscussionAsync();
        var root = await AddAsync(d, "Frage");
        var reply = await AddAsync(d, "Antwort", root.Id);
        var editor = As(d, d.A.Seed.MemberOfBoth);

        var resolved = await TryUpdateAsync(editor, new UpdateCommentRequest(root.Id, Resolved: true));
        var again = await TryUpdateAsync(editor, new UpdateCommentRequest(root.Id, Resolved: true));
        var onReply = await TryUpdateAsync(editor, new UpdateCommentRequest(reply.Id, Resolved: true));
        var reopened = await TryUpdateAsync(editor, new UpdateCommentRequest(root.Id, Resolved: false));

        Assert.Equal(d.A.Seed.MemberOfBoth.Id, resolved.Value.ResolvedBy);
        Assert.NotNull(again.Value.ResolvedAt);
        Assert.Equal(ErrorCodes.NotRoot, FieldOf(onReply, "resolved").Code);
        Assert.Null(reopened.Value.ResolvedAt);
        Assert.Single(await EventsAsync(ActivityTypes.CommentResolved));
        Assert.Single(await EventsAsync(ActivityTypes.CommentReopened));
    }

    [Fact]
    public async Task Update_TextAndResolveTogether_OneTransaction_TwoEvents()
    {
        var d = await ArrangeDiscussionAsync();
        var root = await AddAsync(d, "Frage");

        var result = await TryUpdateAsync(As(d, d.A.Seed.AlphaOwner), new UpdateCommentRequest(root.Id, "Frage (erledigt)", Resolved: true));

        Assert.Equal(("Frage (erledigt)", true), (result.Value.Markdown, result.Value.ResolvedAt is not null));
        Assert.Single(await EventsAsync(ActivityTypes.CommentUpdated));
        Assert.Single(await EventsAsync(ActivityTypes.CommentResolved));
    }

    [Fact]
    public async Task Delete_AuthorOrAdmin_OtherEditorForbidden_RemovesMentions()
    {
        var d = await ArrangeDiscussionAsync();
        var both = d.A.Seed.MemberOfBoth;
        var byOwner = await AddAsync(d, $"Frage an {Mention(both)}");
        var byEditor = await AddAsync(d, "Vom Editor", actor: both);
        var byOwnerToo = await AddAsync(d, "Noch einer");

        var editorOnOwner = await TryDeleteAsync(As(d, both), byOwner.Id);
        var editorOwn = await TryDeleteAsync(As(d, both), byEditor.Id);
        var adminOnEditor = await TryDeleteAsync(As(d, d.A.Seed.AlphaOwner), byOwnerToo.Id);
        var authorOwn = await TryDeleteAsync(As(d, d.A.Seed.AlphaOwner), byOwner.Id);
        var twice = await TryDeleteAsync(As(d, d.A.Seed.AlphaOwner), byOwner.Id);

        Assert.Equal(ErrorCodes.NotAuthor, Assert.IsType<ForbiddenError>(editorOnOwner.Error).Code);
        Assert.True(editorOwn.IsSuccess && adminOnEditor.IsSuccess && authorOwn.IsSuccess && twice.IsSuccess);
        Assert.Equal(0, await Db.Mentions.CountAsync(Ct));
        var stored = await Db.Comments.AsNoTracking().SingleAsync(c => c.Id == byOwner.Id, Ct);
        Assert.Equal(string.Empty, stored.BodyMarkdown);
        Assert.Equal(3, (await EventsAsync(ActivityTypes.CommentDeleted)).Count);
    }

    [Fact]
    public async Task Delete_AdminWithoutAuthorship_ByAdminScope()
    {
        var d = await ArrangeDiscussionAsync();
        var byEditor = await AddAsync(d, "Vom Editor", actor: d.A.Seed.MemberOfBoth);

        var ownerWithoutAdminScope = await TryDeleteAsync(As(d, d.A.Seed.AlphaOwner, "read", "write"), byEditor.Id);
        var owner = await TryDeleteAsync(As(d, d.A.Seed.AlphaOwner), byEditor.Id);

        Assert.Equal(ErrorCodes.NotAuthor, Assert.IsType<ForbiddenError>(ownerWithoutAdminScope.Error).Code);
        Assert.True(owner.IsSuccess);
    }

    [Fact]
    public async Task List_DeletedRootWithReplies_Placeholder_DeletedLeafOmitted_ResolvedFilter_Order()
    {
        var d = await ArrangeDiscussionAsync();
        var first = await AddAsync(d, "Erster Thread");
        var reply1 = await AddAsync(d, "Antwort 1", first.Id);
        var reply2 = await AddAsync(d, "Antwort 2", first.Id);
        var second = await AddAsync(d, "Zweiter Thread, gleich gelöscht");
        var third = await AddAsync(d, "Dritter Thread");
        var owner = As(d, d.A.Seed.AlphaOwner);
        Assert.True((await TryDeleteAsync(owner, first.Id)).IsSuccess);
        Assert.True((await TryDeleteAsync(owner, second.Id)).IsSuccess);
        Assert.True((await TryDeleteAsync(owner, reply2.Id)).IsSuccess);
        Assert.True((await TryUpdateAsync(owner, new UpdateCommentRequest(third.Id, Resolved: true))).IsSuccess);

        var all = await ListAsync(owner, d.Record.Id);
        var open = await ListAsync(owner, d.Record.Id, includeResolved: false);

        Assert.Equal([first.Id, third.Id], all.Threads.Select(t => t.Id));
        var placeholder = all.Threads[0];
        Assert.Null(placeholder.Markdown);
        Assert.NotNull(placeholder.DeletedAt);
        Assert.Equal([reply1.Id], placeholder.Replies!.Select(r => r.Id));
        Assert.Equal([first.Id], open.Threads.Select(t => t.Id));
    }

    [Fact]
    public async Task List_ManyReplies_NewestPerThreadShownOldestFirst_OmittedCounted()
    {
        var d = await ArrangeDiscussionAsync();
        var root = await AddAsync(d, "Viel Diskussion");
        var record = await Db.Records.AsNoTracking().SingleAsync(r => r.Id == d.Record.Id, Ct);
        var parent = await Db.Comments.AsNoTracking().SingleAsync(c => c.Id == root.Id, Ct);
        var total = ListComments.MaxRepliesPerThread + 2;
        for (var i = 0; i < total; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            Db.Comments.Add(Domain.Comments.Comment.Create(record, parent, d.A.Seed.AlphaOwner.Id, $"Antwort {i}", Clock));
        }

        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();

        var thread = Assert.Single((await ListAsync(As(d, d.A.Seed.AlphaOwner), d.Record.Id)).Threads);

        Assert.Equal(ListComments.MaxRepliesPerThread, thread.Replies!.Count);
        Assert.Equal(2, thread.OmittedReplies);
        Assert.Equal("Antwort 2", thread.Replies[0].Markdown);
        Assert.Equal($"Antwort {total - 1}", thread.Replies[^1].Markdown);
    }

    [Fact]
    public async Task Add_ManyBadMentions_ErrorsCappedAt20WithHint()
    {
        var d = await ArrangeDiscussionAsync();
        var markdown = string.Join(' ', Enumerable.Range(0, 25).Select(i => $"[@x](actor:bad-{i})"));
        var outsiders = string.Join(' ', Enumerable.Range(0, 23).Select(_ => CommentMarkdownLink(Guid.CreateVersion7())));
        var tenant = As(d, d.A.Seed.AlphaOwner);

        var invalid = Invalid(await TryAddAsync(tenant, new AddCommentRequest(d.Record.Id, markdown))).Errors;
        var notMembers = Invalid(await TryAddAsync(tenant, new AddCommentRequest(d.Record.Id, outsiders))).Errors;

        Assert.Equal(20, invalid.Count);
        Assert.EndsWith("(and 5 more like this)", invalid[^1].Message, StringComparison.Ordinal);
        Assert.Equal(20, notMembers.Count);
        Assert.All(notMembers, e => Assert.Equal(ErrorCodes.NotMember, e.Code));
        Assert.EndsWith("(and 3 more like this)", notMembers[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRecordMarkdown_IncludeComments_AppendsCommentsSection()
    {
        var d = await ArrangeDiscussionAsync();
        await AddAsync(d, "Bitte \"prüfen\"");

        var with = await new GetRecordMarkdown(Db, As(d, d.A.Seed.AlphaOwner)).HandleAsync(new GetRecordMarkdownRequest(d.Record.Id, IncludeComments: true), Ct);
        var without = await new GetRecordMarkdown(Db, As(d, d.A.Seed.AlphaOwner)).HandleAsync(new GetRecordMarkdownRequest(d.Record.Id), Ct);

        Assert.StartsWith(without.Value.TrimEnd(), with.Value, StringComparison.Ordinal);
        Assert.Contains("\n---\n\n# Comments\n\n1 thread(s)", with.Value, StringComparison.Ordinal);
        Assert.Contains("by \"Alpha owner\" (agent", with.Value, StringComparison.Ordinal);
        Assert.Contains("  Bitte \"prüfen\"", with.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("# Comments", without.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_UnknownRecord_NotFound()
    {
        var d = await ArrangeDiscussionAsync();

        var result = await new ListComments(Db, As(d, d.A.Seed.AlphaOwner)).HandleAsync(new ListCommentsRequest(Guid.CreateVersion7()), Ct);

        Assert.IsType<NotFoundError>(result.Error);
    }

    [Fact]
    public async Task GetRecord_IncludeComments_AttachesThreads()
    {
        var d = await ArrangeDiscussionAsync();
        var root = await AddAsync(d, "Frage");
        await AddAsync(d, "Antwort", root.Id);

        var with = await new GetRecord(Db, As(d, d.A.Seed.AlphaOwner)).HandleAsync(new GetRecordRequest(d.Record.Id, IncludeComments: true), Ct);
        var without = await new GetRecord(Db, As(d, d.A.Seed.AlphaOwner)).HandleAsync(new GetRecordRequest(d.Record.Id), Ct);

        Assert.Single(Assert.Single(with.Value.Comments!.Threads).Replies!);
        Assert.Null(without.Value.Comments);
    }

    [Fact]
    public async Task Activity_CommentEvents_DiffSpaceCollectionAndRecordTitle()
    {
        var d = await ArrangeDiscussionAsync();
        var both = d.A.Seed.MemberOfBoth;
        var root = await AddAsync(d, $"Frage an {Mention(both)}");
        var reply = await AddAsync(d, "Antwort", root.Id, both);

        var page = await new ListActivity(Db, As(d, d.A.Seed.AlphaOwner)).HandleAsync(new ListActivityRequest(Types: [ActivityTypes.CommentCreated]), Ct);

        Assert.Equal([reply.Id, root.Id], page.Value.Items.Select(e => e.TargetId));
        var created = page.Value.Items[1];
        Assert.Equal((ActivityTargets.Comment, "Datenbank wählen", d.A.SpaceId, d.A.Collection.Id), (created.TargetType, created.TargetTitle, created.SpaceId, created.CollectionId));
        Assert.Equal(d.Record.Id.ToString(), created.Diff["recordId"]!.GetValue<string>());
        Assert.Null(created.Diff["parentCommentId"]);
        Assert.Equal($"[\"{both.Id}\"]", created.Diff["mentionedActorIds"]!.ToJsonString());
        Assert.Equal(root.Id.ToString(), page.Value.Items[0].Diff["parentCommentId"]!.GetValue<string>());
        Assert.Equal("[]", page.Value.Items[0].Diff["mentionedActorIds"]!.ToJsonString());
    }

    [Fact]
    public async Task Idempotency_SameKey_SameComment_RecordKeyReused_Conflict()
    {
        var d = await ArrangeDiscussionAsync();
        var tenant = As(d, d.A.Seed.AlphaOwner);

        var first = await TryAddAsync(tenant, new AddCommentRequest(d.Record.Id, "Einmal", IdempotencyKey: "k-1"));
        var second = await TryAddAsync(tenant, new AddCommentRequest(d.Record.Id, "Einmal", IdempotencyKey: "k-1"));
        Db.ChangeTracker.Clear();
        var record = await CreateUseCase(tenant, d.A.Seed.AlphaOwner).HandleAsync(new CreateRecordRequest(d.A.Collection.Id, Title: "R", IdempotencyKey: "k-1"), Ct);
        Db.ChangeTracker.Clear();
        var recordKey = await CreateUseCase(tenant, d.A.Seed.AlphaOwner).HandleAsync(new CreateRecordRequest(d.A.Collection.Id, Title: "R", IdempotencyKey: "k-2"), Ct);
        var commentWithRecordKey = await TryAddAsync(tenant, new AddCommentRequest(d.Record.Id, "Zweimal", IdempotencyKey: "k-2"));

        Assert.Equal(first.Value, second.Value);
        Assert.Equal(1, await Db.Comments.CountAsync(Ct));
        Assert.Equal("idempotency_key_reused", Assert.IsType<ConflictError>(record.Error).Code);
        Assert.True(recordKey.IsSuccess);
        Assert.Equal("idempotency_key_reused", Assert.IsType<ConflictError>(commentWithRecordKey.Error).Code);

        // The same key for a comment on another record is no retry: 409, not the first comment.
        var otherRecord = await CreateRecordAsync(d.A, "Anderer Record");
        var elsewhere = await TryAddAsync(tenant, new AddCommentRequest(otherRecord.Id, "Einmal", IdempotencyKey: "k-1"));
        Assert.Equal(("idempotency_key_reused", "Idempotency-Key"), (Assert.IsType<ConflictError>(elsewhere.Error).Code, ((ConflictError)elsewhere.Error).Field));
        Assert.Equal(0, await Db.Comments.CountAsync(c => c.RecordId == otherRecord.Id, Ct));
    }

    [Fact]
    public async Task Idempotency_SameKeyDifferentTextOrParent_Conflict_NothingStored()
    {
        var d = await ArrangeDiscussionAsync();
        var tenant = As(d, d.A.Seed.AlphaOwner);
        var root = await AddAsync(d, "Wurzel");
        var request = new AddCommentRequest(d.Record.Id, "Einmal", root.Id, IdempotencyKey: "k-1");
        var first = await TryAddAsync(tenant, request);

        var retry = await TryAddAsync(tenant, request);
        var otherText = await TryAddAsync(tenant, request with { Markdown = "Einmal!" });
        var otherWhitespace = await TryAddAsync(tenant, request with { Markdown = "Einmal\n" });
        var newThread = await TryAddAsync(tenant, request with { ParentCommentId = null });

        Assert.Equal(first.Value, retry.Value);
        foreach (var result in new[] { otherText, otherWhitespace, newThread })
        {
            var conflict = Assert.IsType<ConflictError>(result.Error);
            Assert.Equal(("idempotency_key_reused", "Idempotency-Key"), (conflict.Code, conflict.Field));
        }

        Assert.Equal(2, await Db.Comments.CountAsync(Ct));
    }

    [Fact]
    public async Task Idempotency_KeyStoredWithoutHash_ReplaysLikeBefore()
    {
        var d = await ArrangeDiscussionAsync();
        var tenant = As(d, d.A.Seed.AlphaOwner);
        var first = await TryAddAsync(tenant, new AddCommentRequest(d.Record.Id, "Einmal", IdempotencyKey: "alt"));
        await Db.Database.ExecuteSqlAsync($"UPDATE idempotency_keys SET request_hash = NULL", Ct);

        var second = await TryAddAsync(tenant, new AddCommentRequest(d.Record.Id, "Anderer Text", IdempotencyKey: "alt"));
        var otherRecord = await CreateRecordAsync(d.A, "Anderer Record");
        var elsewhere = await TryAddAsync(tenant, new AddCommentRequest(otherRecord.Id, "Einmal", IdempotencyKey: "alt"));

        Assert.Equal(first.Value, second.Value);
        Assert.Equal("idempotency_key_reused", Assert.IsType<ConflictError>(elsewhere.Error).Code);
        Assert.Equal(1, await Db.Comments.CountAsync(Ct));
    }

    private static string CommentMarkdownLink(Guid id) => Domain.Comments.CommentMarkdown.MentionLink("Fremd", id);

    private async Task<IReadOnlyList<ActivityEventDto>> EventsAsync(string type)
    {
        var tenant = Tenant.Current!;
        var result = await new ListActivity(Db, tenant).HandleAsync(new ListActivityRequest(Types: [type]), Ct);
        return result.Value.Items;
    }
}
