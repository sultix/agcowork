using Agcowork.Domain.Comments;
using Microsoft.Extensions.Time.Testing;
using Record = Agcowork.Domain.Records.Record;

namespace Agcowork.Domain.Tests.Comments;

public sealed class CommentTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();
    private static readonly Guid Author = Guid.CreateVersion7();
    private static readonly Guid Other = Guid.CreateVersion7();

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));

    private Record NewRecord(Guid? workspace = null) =>
        Record.Create(workspace ?? Workspace, Guid.CreateVersion7(), null, null, null, "Seite", null, null, Author, _clock);

    [Fact]
    public void Create_Root_TakesWorkspaceAndRecordOfTheRecord()
    {
        var record = NewRecord();

        var comment = Comment.Create(record, null, Author, "Frage?", _clock);

        Assert.Equal((Workspace, record.Id, Author, "Frage?"), (comment.WorkspaceId, comment.RecordId, comment.AuthorId, comment.BodyMarkdown));
        Assert.True(comment.IsRoot);
        Assert.Equal(_clock.GetUtcNow(), comment.CreatedAt);
        Assert.Null(comment.EditedAt);
    }

    [Fact]
    public void Create_ReplyToReply_Throws()
    {
        var record = NewRecord();
        var root = Comment.Create(record, null, Author, "Wurzel", _clock);
        var reply = Comment.Create(record, root, Other, "Antwort", _clock);

        Assert.Equal(root.Id, reply.ParentCommentId);
        Assert.Throws<ArgumentException>(() => Comment.Create(record, reply, Author, "Antwort auf Antwort", _clock));
    }

    [Fact]
    public void Create_ParentOnOtherRecordOrWorkspace_Throws()
    {
        var root = Comment.Create(NewRecord(), null, Author, "Wurzel", _clock);

        Assert.Throws<ArgumentException>(() => Comment.Create(NewRecord(), root, Author, "Falscher Record", _clock));
        Assert.Throws<ArgumentException>(() => Comment.Create(NewRecord(Guid.CreateVersion7()), root, Author, "Fremder Workspace", _clock));
    }

    [Fact]
    public void Create_ParentDeleted_Throws()
    {
        var record = NewRecord();
        var root = Comment.Create(record, null, Author, "Wurzel", _clock);
        root.Delete(_clock);

        Assert.Throws<ArgumentException>(() => Comment.Create(record, root, Author, "Spät", _clock));
    }

    [Fact]
    public void Create_BlankOrTooLongBody_Throws()
    {
        var record = NewRecord();

        Assert.Throws<ArgumentException>(() => Comment.Create(record, null, Author, "  ", _clock));
        Assert.Throws<ArgumentException>(() => Comment.Create(record, null, Author, new string('a', Comment.BodyMaxBytes + 1), _clock));
        Assert.Equal(Comment.BodyMaxBytes, Comment.Create(record, null, Author, new string('a', Comment.BodyMaxBytes), _clock).BodyMarkdown.Length);
        // Bytes, not characters: 3 bytes each.
        Assert.False(Comment.IsBodyWithinLimit(new string('€', (Comment.BodyMaxBytes / 3) + 1)));
    }

    [Fact]
    public void Edit_ChangedBody_SetsEditedAt_SameBody_NoChange()
    {
        var comment = Comment.Create(NewRecord(), null, Author, "Alt", _clock);
        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.False(comment.Edit("Alt", _clock));
        Assert.Null(comment.EditedAt);
        Assert.True(comment.Edit("Neu", _clock));
        Assert.Equal(("Neu", _clock.GetUtcNow()), (comment.BodyMarkdown, comment.EditedAt));
    }

    [Fact]
    public void ResolveAndReopen_Root_TogglesOnce()
    {
        var comment = Comment.Create(NewRecord(), null, Author, "Frage?", _clock);

        Assert.True(comment.Resolve(Other, _clock));
        Assert.False(comment.Resolve(Other, _clock));
        Assert.Equal((Other, _clock.GetUtcNow()), (comment.ResolvedBy, comment.ResolvedAt));
        Assert.True(comment.Reopen());
        Assert.False(comment.Reopen());
        Assert.Null(comment.ResolvedAt);
        Assert.Null(comment.ResolvedBy);
    }

    [Fact]
    public void ResolveOrReopen_Reply_Throws()
    {
        var record = NewRecord();
        var reply = Comment.Create(record, Comment.Create(record, null, Author, "Wurzel", _clock), Other, "Antwort", _clock);

        Assert.Throws<InvalidOperationException>(() => reply.Resolve(Author, _clock));
        Assert.Throws<InvalidOperationException>(() => reply.Reopen());
    }

    [Fact]
    public void Delete_EmptiesBody_CannotBeEditedAfterwards_SecondDeleteNoChange()
    {
        var comment = Comment.Create(NewRecord(), null, Author, "Weg damit", _clock);

        Assert.True(comment.Delete(_clock));
        Assert.False(comment.Delete(_clock));
        Assert.Equal((string.Empty, _clock.GetUtcNow()), (comment.BodyMarkdown, comment.DeletedAt));
        Assert.Throws<InvalidOperationException>(() => comment.Edit("Wieder da", _clock));
    }

    [Fact]
    public void Mention_Create_CopiesCommentAndRejectsSelfOrDeleted()
    {
        var comment = Comment.Create(NewRecord(), null, Author, "Hallo", _clock);

        var mention = Mention.Create(comment, Other, Author, _clock);

        Assert.Equal((comment.WorkspaceId, comment.Id, comment.RecordId, Other, Author), (mention.WorkspaceId, mention.CommentId, mention.RecordId, mention.MentionedActorId, mention.ByActorId));
        Assert.False(mention.IsRead);
        Assert.Throws<ArgumentException>(() => Mention.Create(comment, Author, Author, _clock));
        comment.Delete(_clock);
        Assert.Throws<ArgumentException>(() => Mention.Create(comment, Other, Author, _clock));
    }
}
