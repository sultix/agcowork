using System.Text.Json;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Records;
using Microsoft.Extensions.Time.Testing;
using Record = Agcowork.Domain.Records.Record;

namespace Agcowork.Domain.Tests.Records;

public sealed class RecordTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();
    private static readonly Guid Space = Guid.CreateVersion7();
    private static readonly Guid Actor = Guid.CreateVersion7();

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));

    private static Dictionary<string, JsonElement> Props(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private Record Create(Record? parent = null, IReadOnlyList<Guid>? ancestors = null, string title = "  Entscheidung  ", Guid? workspace = null) =>
        Record.Create(workspace ?? Workspace, Space, null, parent, ancestors, title, Props("""{ "status": "open", "gone": null }"""), "Body", Actor, _clock);

    [Fact]
    public void Create_SetsRevisionOneAndTrimsTitle_DropsNulls()
    {
        var record = Create();

        Assert.Equal("Entscheidung", record.Title);
        Assert.Equal(1, record.RevisionNo);
        Assert.Equal(["status"], record.Properties.Keys);
        Assert.Equal((Actor, _clock.GetUtcNow()), (record.CreatedBy, record.CreatedAt));
        Assert.Equal((Actor, _clock.GetUtcNow()), (record.UpdatedBy, record.UpdatedAt));
        Assert.Null(record.CollectionId);
    }

    [Fact]
    public void Create_InvalidTitle_Throws()
    {
        Assert.Throws<ArgumentException>(() => Create(title: "  "));
        Assert.Throws<ArgumentException>(() => Create(title: new string('a', Record.TitleMaxLength + 1)));
        Assert.Equal(Record.TitleMaxLength, Create(title: new string('a', Record.TitleMaxLength)).Title.Length);
    }

    [Fact]
    public void Create_TitleInProperties_Throws() =>
        Assert.Throws<ArgumentException>(() => Record.Create(Workspace, Space, null, null, null, "T", Props("""{ "title": "x" }"""), null, Actor, _clock));

    [Fact]
    public void Create_BodyOverLimit_Throws()
    {
        var tooLong = new string('ä', (Record.BodyMaxBytes / 2) + 1);

        Assert.False(Record.IsBodyWithinLimit(tooLong));
        Assert.Throws<ArgumentException>(() => Record.Create(Workspace, Space, null, null, null, "T", null, tooLong, Actor, _clock));
    }

    [Fact]
    public void Create_CollectionOfOtherWorkspaceOrSpace_Throws()
    {
        var otherSpace = Collection.Create(Workspace, Guid.CreateVersion7(), "Aufgaben", "Tasks.", null, Actor, _clock);
        var otherWorkspace = Collection.Create(Guid.CreateVersion7(), Space, "Aufgaben", "Tasks.", null, Actor, _clock);
        var own = Collection.Create(Workspace, Space, "Aufgaben", "Tasks.", null, Actor, _clock);

        Assert.Throws<ArgumentException>(() => Record.Create(Workspace, Space, otherSpace, null, null, "T", null, null, Actor, _clock));
        Assert.Throws<ArgumentException>(() => Record.Create(Workspace, Space, otherWorkspace, null, null, "T", null, null, Actor, _clock));
        Assert.Equal(own.Id, Record.Create(Workspace, Space, own, null, null, "T", null, null, Actor, _clock).CollectionId);
    }

    [Fact]
    public void Create_ParentOfOtherWorkspace_Throws()
    {
        var foreign = Create(workspace: Guid.CreateVersion7());

        Assert.Throws<ArgumentException>(() => Create(parent: foreign));
    }

    [Fact]
    public void Create_ParentChainWithCycle_Throws()
    {
        var parent = Create();

        Assert.Throws<ArgumentException>(() => Create(parent, [parent.Id]));
        var grandParent = Guid.CreateVersion7();
        Assert.Throws<ArgumentException>(() => Create(parent, [grandParent, grandParent]));
    }

    [Fact]
    public void Create_Depth_AtMost32()
    {
        var parent = Create();
        var maxAncestors = Enumerable.Range(0, Record.MaxDepth - 2).Select(_ => Guid.CreateVersion7()).ToList();

        Assert.Equal(parent.Id, Create(parent, maxAncestors).ParentRecordId);
        Assert.Throws<ArgumentException>(() => Create(parent, [.. maxAncestors, Guid.CreateVersion7()]));
    }

    [Fact]
    public void MoveTo_BelowItselfOrADescendant_Throws()
    {
        var record = Create();
        var child = Create(record);

        Assert.Throws<ArgumentException>(() => record.MoveTo(record, []));
        Assert.Throws<ArgumentException>(() => record.MoveTo(child, [record.Id]));

        var other = Create();
        record.MoveTo(other, []);
        Assert.Equal(other.Id, record.ParentRecordId);
        record.MoveTo(null, null);
        Assert.Null(record.ParentRecordId);
    }

    [Fact]
    public void Change_SameContent_ReturnsFalseAndKeepsRevision()
    {
        var record = Create();

        var changed = record.Change(new RecordSnapshot("Entscheidung", Props("""{ "status": "open" }"""), "Body"), Guid.CreateVersion7(), _clock);

        Assert.False(changed);
        Assert.Equal(1, record.RevisionNo);
        Assert.Equal(Actor, record.UpdatedBy);
    }

    [Fact]
    public void Change_NewContent_IncrementsRevisionAndSetsUpdater()
    {
        var record = Create();
        var editor = Guid.CreateVersion7();
        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(record.Change(new RecordSnapshot("Neu", Props("""{ "status": "done" }"""), "Body 2"), editor, _clock));

        Assert.Equal((2, editor, _clock.GetUtcNow()), (record.RevisionNo, record.UpdatedBy, record.UpdatedAt));
        Assert.Equal("Neu", record.Title);
        Assert.Equal("done", record.Properties["status"].GetString());
        Assert.Equal(Actor, record.CreatedBy);
    }

    [Fact]
    public void Append_AddsToBodyAndIncrementsRevision()
    {
        var record = Create();

        record.Append("- entry", null, Actor, _clock);

        Assert.Equal("Body\n\n- entry\n", record.Body);
        Assert.Equal(2, record.RevisionNo);
    }

    [Fact]
    public void Archive_MakesReadOnly_RestoreReopens()
    {
        var record = Create();

        record.Archive(Actor, _clock);

        Assert.True(record.IsArchived);
        Assert.Equal(2, record.RevisionNo);
        Assert.Throws<InvalidOperationException>(() => record.Append("x", null, Actor, _clock));
        Assert.Throws<InvalidOperationException>(() => record.Change(record.Snapshot() with { Title = "X" }, Actor, _clock));
        Assert.Throws<InvalidOperationException>(() => record.Archive(Actor, _clock));

        record.Restore(Actor, _clock);

        Assert.False(record.IsArchived);
        Assert.Equal(3, record.RevisionNo);
        Assert.Throws<InvalidOperationException>(() => record.Restore(Actor, _clock));
    }
}
