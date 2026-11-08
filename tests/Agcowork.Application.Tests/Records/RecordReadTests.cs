using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Agcowork.Domain.Records;

namespace Agcowork.Application.Tests.Records;

public sealed class RecordReadTests(PostgresFixture postgres) : RecordTestBase(postgres)
{
    [Fact]
    public async Task Get_WithBodyAndHistory()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, body: "B");
        Assert.True((await UpdateAsync(a, new UpdateRecordRequest(record.Id, Title: "Zwei", Reason: "Umbenannt"))).IsSuccess);

        var plain = await new GetRecord(Db, a.Tenant, RecordTranslator).HandleAsync(new GetRecordRequest(record.Id), Ct);
        var full = await new GetRecord(Db, a.Tenant, RecordTranslator).HandleAsync(new GetRecordRequest(record.Id, IncludeBody: true, IncludeHistory: 5), Ct);
        var tooMuch = await new GetRecord(Db, a.Tenant, RecordTranslator).HandleAsync(new GetRecordRequest(record.Id, IncludeHistory: 101), Ct);

        Assert.Null(plain.Value.Body);
        Assert.Null(plain.Value.History);
        Assert.Equal("B", full.Value.Body);
        Assert.Equal([(2, RevisionKind.Updated, "Umbenannt"), (1, RevisionKind.Created, null)], full.Value.History!.Select(h => (h.No, h.Kind, h.Reason)));
        Assert.IsType<ValidationError>(tooMuch.Error);
    }

    [Fact]
    public async Task History_NewestFirst_Paginated_RevisionHasSnapshot()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, body: "v1");
        for (var i = 2; i <= 4; i++)
        {
            Assert.True((await UpdateAsync(a, new UpdateRecordRequest(record.Id, Body: $"v{i}"))).IsSuccess);
        }

        var first = await new GetRecordHistory(Db, a.Tenant).HandleAsync(new GetRecordHistoryRequest(record.Id, Limit: 3), Ct);
        var second = await new GetRecordHistory(Db, a.Tenant).HandleAsync(new GetRecordHistoryRequest(record.Id, Limit: 3, Cursor: first.Value.NextCursor), Ct);
        var revision = await new GetRevision(Db, a.Tenant).HandleAsync(new GetRevisionRequest(record.Id, 2), Ct);
        var missing = await new GetRevision(Db, a.Tenant).HandleAsync(new GetRevisionRequest(record.Id, 9), Ct);

        Assert.Equal([4, 3, 2], first.Value.Items.Select(r => r.No));
        Assert.Equal([1], second.Value.Items.Select(r => r.No));
        Assert.Null(second.Value.NextCursor);
        Assert.Equal(("v2", "Schema migrieren", "todo"), (revision.Value.Body, revision.Value.Title, revision.Value.Properties["status"].GetString()));
        Assert.IsType<NotFoundError>(missing.Error);
    }

    [Fact]
    public async Task List_ByCollection_CreationOrder_Cursor_ArchivedHidden()
    {
        var a = await ArrangeAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await CreateRecordAsync(a, $"R{i}")).Id);
            Clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.True((await new ArchiveRecord(Db, TestActor.Agent(a.Seed.AlphaOwner.Id), a.Tenant, Activity, Clock).HandleAsync(new ArchiveRecordRequest(ids[1]), Ct)).IsSuccess);
        var freePage = await TryCreateAsync(a, new CreateRecordRequest(SpaceId: a.SpaceId, Title: "Frei"));

        var listed = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await new ListRecords(Db, a.Tenant).HandleAsync(new ListRecordsRequest(a.Collection.Id, Limit: 2, Cursor: cursor), Ct);
            Assert.True(page.IsSuccess, Describe(page.Error));
            listed.AddRange(page.Value.Items.Select(r => r.Id));
            cursor = page.Value.NextCursor;
        }
        while (cursor is not null);

        var withArchived = await new ListRecords(Db, a.Tenant).HandleAsync(new ListRecordsRequest(a.Collection.Id, IncludeArchived: true), Ct);
        var free = await new ListRecords(Db, a.Tenant).HandleAsync(new ListRecordsRequest(SpaceId: a.SpaceId), Ct);
        var both = await new ListRecords(Db, a.Tenant).HandleAsync(new ListRecordsRequest(a.Collection.Id, a.SpaceId), Ct);
        var unknown = await new ListRecords(Db, a.Tenant).HandleAsync(new ListRecordsRequest(Guid.CreateVersion7()), Ct);

        Assert.Equal([ids[0], ids[2], ids[3], ids[4]], listed);
        Assert.Equal(ids, withArchived.Value.Items.Select(r => r.Id));
        Assert.Equal([freePage.Value.Id], free.Value.Items.Select(r => r.Id));
        Assert.IsType<ValidationError>(both.Error);
        Assert.IsType<NotFoundError>(unknown.Error);
    }
}
