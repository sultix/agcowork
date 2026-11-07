using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Records;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Tests.Records;

public sealed class UpdateRecordTests(PostgresFixture postgres) : RecordTestBase(postgres)
{
    [Fact]
    public async Task Update_Patch_OnlySentKeysChange_NullRemoves()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, properties: """{ "tags": ["db"], "estimate": 2, "notes": "**x**" }""", body: "Body");

        var result = await UpdateAsync(a, new UpdateRecordRequest(record.Id, Properties: Props("""{ "status": "doing", "estimate": null }"""), Reason: "Started"));

        Assert.True(result.IsSuccess, Describe(result.Error));
        Assert.Equal(2, result.Value.Revision);
        Assert.Equal(["status", "tags", "notes"], result.Value.Properties.Keys);
        Assert.Equal("doing", result.Value.Properties["status"].GetString());
        var stored = await Db.Records.AsNoTracking().SingleAsync(r => r.Id == record.Id, Ct);
        Assert.Equal(("Schema migrieren", "Body"), (stored.Title, stored.Body));
        var revision = await Db.Revisions.AsNoTracking().SingleAsync(r => r.RecordId == record.Id && r.No == 2, Ct);
        Assert.Equal((RevisionKind.Updated, "Started"), (revision.Kind, revision.Reason));
    }

    [Fact]
    public async Task Update_UnknownKey_UnknownPropertyWithAllowed_RequiredNull_Required()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a);

        var result = await UpdateAsync(a, new UpdateRecordRequest(record.Id, Properties: Props("""{ "colour": "red", "status": null }""")));

        var error = Invalid(result);
        var unknown = Field(error, "properties.colour");
        Assert.Equal(ErrorCodes.UnknownProperty, unknown.Code);
        Assert.Contains("status", unknown.Allowed!);
        Assert.Equal(ErrorCodes.Required, Field(error, "properties.status").Code);
        Assert.Equal(1, (await Db.Records.AsNoTracking().SingleAsync(r => r.Id == record.Id, Ct)).RevisionNo);
    }

    [Fact]
    public async Task Update_TitleAndBody_Replaced_EmptyTitle_Required()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, body: "Alt");

        var renamed = await UpdateAsync(a, new UpdateRecordRequest(record.Id, Title: " Neu ", Body: "Neu"));
        var empty = await UpdateAsync(a, new UpdateRecordRequest(record.Id, Title: " "));

        Assert.Equal("Neu", renamed.Value.Title);
        Assert.Equal("Neu", (await Db.Records.AsNoTracking().SingleAsync(r => r.Id == record.Id, Ct)).Body);
        Assert.Equal(ErrorCodes.Required, Field(Invalid(empty), "title").Code);
    }

    [Fact]
    public async Task Update_ExpectedRevisionMatches_NewRevision()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a);

        var result = await UpdateAsync(a, new UpdateRecordRequest(record.Id, Properties: Props("""{ "status": "done" }"""), ExpectedRevision: 1));

        Assert.True(result.IsSuccess, Describe(result.Error));
        Assert.Equal(2, result.Value.Revision);
    }

    [Fact]
    public async Task Update_ExpectedRevisionStale_RevisionMismatchWithCurrentAndChangedKeys()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, body: "B");
        Assert.True((await UpdateAsync(a, new UpdateRecordRequest(record.Id, Properties: Props("""{ "status": "doing" }"""), Body: "B2"))).IsSuccess);
        Assert.True((await UpdateAsync(a, new UpdateRecordRequest(record.Id, Properties: Props("""{ "tags": ["api"] }""")))).IsSuccess);

        var stale = await UpdateAsync(a, new UpdateRecordRequest(record.Id, Properties: Props("""{ "estimate": 3 }"""), ExpectedRevision: 1));
        var future = await UpdateAsync(a, new UpdateRecordRequest(record.Id, Properties: Props("""{ "estimate": 3 }"""), ExpectedRevision: 9));

        var conflict = Assert.IsType<ConflictError>(stale.Error);
        Assert.Equal((RecordConflicts.RevisionMismatchCode, "expectedRevision"), (conflict.Code, conflict.Field));
        Assert.Equal(3, conflict.Details!["currentRevision"]);
        Assert.Equal(["status", "tags", "body"], (IReadOnlyList<string>)conflict.Details["changedKeys"]!);
        Assert.Null(Assert.IsType<ConflictError>(future.Error).Details!["changedKeys"]);
        Assert.False((await Db.Records.AsNoTracking().SingleAsync(r => r.Id == record.Id, Ct)).Properties.ContainsKey("estimate"));
    }

    [Fact]
    public async Task Update_WithoutExpectedRevision_LastWriterWins()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a);

        Assert.True((await UpdateAsync(a, new UpdateRecordRequest(record.Id, Properties: Props("""{ "status": "doing" }""")))).IsSuccess);
        var second = await UpdateAsync(a, new UpdateRecordRequest(record.Id, Properties: Props("""{ "status": "done" }""")));

        Assert.Equal((3, "done"), (second.Value.Revision, second.Value.Properties["status"].GetString()));
    }

    [Fact]
    public async Task Update_NothingChanges_NoRevisionNoActivity()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, properties: """{ "status": "todo" }""");

        var result = await UpdateAsync(a, new UpdateRecordRequest(record.Id, Title: "Schema migrieren", Properties: Props("""{ "status": " todo " }""")));

        Assert.Equal(1, result.Value.Revision);
        Assert.Equal(1, await Db.Revisions.CountAsync(r => r.RecordId == record.Id, Ct));
        Assert.Equal(0, await Db.ActivityEvents.CountAsync(e => e.TargetId == record.Id && e.Type == ActivityTypes.RecordUpdated, Ct));
    }

    [Fact]
    public async Task Update_ActivityDiff_OldAndNewPerKey_BodyAsDelta()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, properties: """{ "estimate": 2 }""", body: "Hallo Welt");

        Assert.True((await UpdateAsync(a, new UpdateRecordRequest(record.Id, Title: "Neu", Properties: Props("""{ "status": "done", "estimate": null }"""), Body: "Hallo schöne Welt"))).IsSuccess);

        var activity = await Db.ActivityEvents.AsNoTracking().SingleAsync(e => e.TargetId == record.Id && e.Type == ActivityTypes.RecordUpdated, Ct);
        AssertChange(activity.Diff["title"], "\"Schema migrieren\"", "\"Neu\"");
        AssertChange(activity.Diff["properties"]!["estimate"], "2", "null");
        AssertChange(activity.Diff["properties"]!["status"], "\"todo\"", "\"done\"");
        Assert.Equal(2, activity.Diff["properties"]!.AsObject().Count);
        Assert.True(activity.Diff["bodyChanged"]!.GetValue<bool>());
        Assert.Equal((7, 0), (activity.Diff["bodyDelta"]!["added"]!.GetValue<int>(), activity.Diff["bodyDelta"]!["removed"]!.GetValue<int>()));
        Assert.Equal((a.Collection.Id, a.SpaceId), (activity.CollectionId, activity.SpaceId));
    }

    [Fact]
    public async Task Update_ArchivedRecord_Conflict_RestoreReopens()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a);
        var actor = TestActor.Agent(a.Seed.AlphaOwner.Id);
        Db.ChangeTracker.Clear();
        var archived = await new ArchiveRecord(Db, actor, a.Tenant, Activity, Clock).HandleAsync(new ArchiveRecordRequest(record.Id, "Erledigt"), Ct);
        var again = await new ArchiveRecord(Db, actor, a.Tenant, Activity, Clock).HandleAsync(new ArchiveRecordRequest(record.Id), Ct);

        var update = await UpdateAsync(a, new UpdateRecordRequest(record.Id, Title: "X"));
        Db.ChangeTracker.Clear();
        var restored = await new RestoreRecord(Db, actor, a.Tenant, Activity, Clock).HandleAsync(new RestoreRecordRequest(record.Id), Ct);
        var restoreAgain = await new RestoreRecord(Db, actor, a.Tenant, Activity, Clock).HandleAsync(new RestoreRecordRequest(record.Id), Ct);

        Assert.NotNull(archived.Value.ArchivedAt);
        Assert.Equal("record_archived", Assert.IsType<ConflictError>(again.Error).Code);
        Assert.Equal("record_archived", Assert.IsType<ConflictError>(update.Error).Code);
        Assert.Equal((3, null), (restored.Value.Revision, restored.Value.ArchivedAt));
        Assert.Equal("record_not_archived", Assert.IsType<ConflictError>(restoreAgain.Error).Code);
        var kinds = await Db.Revisions.AsNoTracking().Where(r => r.RecordId == record.Id).OrderBy(r => r.No).Select(r => r.Kind).ToListAsync(Ct);
        Assert.Equal([RevisionKind.Created, RevisionKind.Archived, RevisionKind.Restored], kinds);
    }

    [Fact]
    public async Task Update_ForeignRecord_NotFound()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a);
        var beta = Enter(a.Seed.Beta, a.Seed.BetaOwner);
        Db.ChangeTracker.Clear();

        var result = await UpdateUseCase(beta, a.Seed.BetaOwner).HandleAsync(new UpdateRecordRequest(record.Id, Title: "Gekapert"), Ct);

        Assert.IsType<NotFoundError>(result.Error);
        Enter(a.Seed.Alpha, a.Seed.AlphaOwner);
        Assert.Equal("Schema migrieren", (await Db.Records.AsNoTracking().SingleAsync(r => r.Id == record.Id, Ct)).Title);
    }
}
