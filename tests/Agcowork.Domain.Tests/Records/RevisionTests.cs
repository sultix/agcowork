using System.Text.Json;
using Agcowork.Domain.Records;
using Microsoft.Extensions.Time.Testing;
using Record = Agcowork.Domain.Records.Record;

namespace Agcowork.Domain.Tests.Records;

public sealed class RevisionTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));

    private static Dictionary<string, JsonElement> Props(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private Record NewRecord() =>
        Record.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), null, null, null, "Titel", Props("""{ "status": "open" }"""), "## Kontext\n", Guid.CreateVersion7(), _clock);

    [Fact]
    public void Capture_IsFullSnapshotOfCurrentState()
    {
        var record = NewRecord();
        var editor = Guid.CreateVersion7();
        _clock.Advance(TimeSpan.FromMinutes(1));
        record.Change(new RecordSnapshot("Titel 2", Props("""{ "status": "done", "owner": "x" }"""), "Neu"), editor, _clock);

        var revision = Revision.Capture(record, RevisionKind.Updated, "  because  ");

        Assert.Equal((record.WorkspaceId, record.Id, 2, RevisionKind.Updated), (revision.WorkspaceId, revision.RecordId, revision.No, revision.Kind));
        Assert.Equal((editor, _clock.GetUtcNow(), "because"), (revision.ActorId, revision.At, revision.Reason));
        Assert.True(revision.Snapshot().SameContentAs(record.Snapshot()));
    }

    [Fact]
    public void Capture_LaterChanges_DoNotAffectSnapshot()
    {
        var record = NewRecord();
        var first = Revision.Capture(record, RevisionKind.Created, null);

        record.Append("more", null, record.CreatedBy, _clock);
        record.Change(record.Snapshot() with { Properties = Props("""{ "status": "done" }""") }, record.CreatedBy, _clock);

        Assert.Equal(1, first.No);
        Assert.Equal("## Kontext\n", first.Body);
        Assert.Equal("open", first.Properties["status"].GetString());
        Assert.Null(first.Reason);
    }

    [Fact]
    public void Capture_ReasonTooLong_Throws() =>
        Assert.Throws<ArgumentException>(() => Revision.Capture(NewRecord(), RevisionKind.Created, new string('r', Revision.ReasonMaxLength + 1)));
}
