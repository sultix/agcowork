using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Records;
using Microsoft.EntityFrameworkCore;
using Record = Agcowork.Domain.Records.Record;

namespace Agcowork.Application.Tests.Records;

public sealed class AppendToRecordTests(PostgresFixture postgres) : RecordTestBase(postgres)
{
    private async Task<Result<RecordDto>> AppendAsync(Arranged a, AppendToRecordRequest request)
    {
        Db.ChangeTracker.Clear();
        return await AppendUseCase(a.Tenant, a.Seed.AlphaOwner).HandleAsync(request, Ct);
    }

    private async Task<string> BodyAsync(Guid id) => (await Db.Records.AsNoTracking().SingleAsync(r => r.Id == id, Ct)).Body;

    [Fact]
    public async Task Append_WithoutSection_AtEnd_RevisionAppended()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, body: "## Kontext\nA\n");

        var result = await AppendAsync(a, new AppendToRecordRequest(record.Id, "- Eintrag", Reason: "Log"));

        Assert.Equal(2, result.Value.Revision);
        Assert.Equal("## Kontext\nA\n\n- Eintrag\n", await BodyAsync(record.Id));
        var revision = await Db.Revisions.AsNoTracking().SingleAsync(r => r.RecordId == record.Id && r.No == 2, Ct);
        Assert.Equal((RevisionKind.Appended, "Log", "## Kontext\nA\n\n- Eintrag\n"), (revision.Kind, revision.Reason, revision.Body));
    }

    [Fact]
    public async Task Append_WithSection_UnderHeading_ActivityNamesSection()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, body: "## Log\n- eins\n\n## Entscheidung\nE\n");

        Assert.True((await AppendAsync(a, new AppendToRecordRequest(record.Id, "- zwei", "log"))).IsSuccess);

        Assert.Equal("## Log\n- eins\n\n- zwei\n\n## Entscheidung\nE\n", await BodyAsync(record.Id));
        var activity = await Db.ActivityEvents.AsNoTracking().SingleAsync(e => e.TargetId == record.Id && e.Type == ActivityTypes.RecordAppended, Ct);
        Assert.Equal("log", activity.Diff["section"]!.GetValue<string>());
        Assert.Equal(0, activity.Diff["bodyDelta"]!["removed"]!.GetValue<int>());
    }

    [Fact]
    public async Task Append_EmptyMarkdown_Required()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a);

        Assert.Equal(ErrorCodes.Required, Field(Invalid(await AppendAsync(a, new AppendToRecordRequest(record.Id, "  "))), "markdown").Code);
    }

    [Fact]
    public async Task Append_BodyLimit_TooLong()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, body: new string('a', Record.BodyMaxBytes - 10));

        var result = await AppendAsync(a, new AppendToRecordRequest(record.Id, "more than ten bytes"));

        Assert.Equal(ErrorCodes.TooLong, Field(Invalid(result), "markdown").Code);
        Assert.Equal(1, (await Db.Records.AsNoTracking().SingleAsync(r => r.Id == record.Id, Ct)).RevisionNo);
    }

    [Fact]
    public async Task Append_ArchivedRecord_Conflict()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a);
        Assert.True((await new ArchiveRecord(Db, TestActor.Agent(a.Seed.AlphaOwner.Id), a.Tenant, Activity, Clock).HandleAsync(new ArchiveRecordRequest(record.Id), Ct)).IsSuccess);

        Assert.Equal("record_archived", Assert.IsType<ConflictError>((await AppendAsync(a, new AppendToRecordRequest(record.Id, "x"))).Error).Code);
    }
}
