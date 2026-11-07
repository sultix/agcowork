using Agcowork.Application.Collections;
using Agcowork.Application.Collections.Templates;
using Agcowork.Application.Records;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Tests.Records;

public sealed class RecordValuesSchemaHookTests(PostgresFixture postgres) : RecordTestBase(postgres)
{
    private async Task ChangeSchemaAsync(Arranged a, UpdateSchemaRequest request)
    {
        Db.ChangeTracker.Clear();
        var result = await new UpdateSchema(Db, a.Tenant, [new TemplatePresetSchemaHook(Db), new RecordValuesSchemaHook(Db)], Activity).HandleAsync(request, Ct);
        Assert.True(result.IsSuccess, Describe(result.Error));
        Db.ChangeTracker.Clear();
    }

    private async Task<IReadOnlyDictionary<string, System.Text.Json.JsonElement>> PropertiesAsync(Guid id) =>
        (await Db.Records.AsNoTracking().SingleAsync(r => r.Id == id, Ct)).Properties;

    [Fact]
    public async Task RemoveProperty_ValuesDeleted_RevisionUnchanged()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, properties: """{ "notes": "x", "estimate": 1 }""");

        await ChangeSchemaAsync(a, new UpdateSchemaRequest(a.Collection.Id, Remove: ["notes"], ConfirmRemoval: true));

        var properties = await PropertiesAsync(record.Id);
        Assert.Equal(["estimate", "status"], properties.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(1, (await Db.Records.AsNoTracking().SingleAsync(r => r.Id == record.Id, Ct)).RevisionNo);
    }

    [Fact]
    public async Task RemoveOptions_SelectValueDropped_MultiSelectItemDropped()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, properties: """{ "status": "doing", "tags": ["db", "api"] }""");
        var untouched = await CreateRecordAsync(a, "Andere", """{ "status": "done" }""");

        await ChangeSchemaAsync(a, new UpdateSchemaRequest(
            a.Collection.Id,
            Update: [new("status", Options: [new("todo"), new("done")]), new("tags", Options: [new("api")])],
            ConfirmRemoval: true));

        var properties = await PropertiesAsync(record.Id);
        Assert.False(properties.ContainsKey("status"));
        Assert.Equal("""["api"]""", properties["tags"].GetRawText());
        Assert.Equal("done", (await PropertiesAsync(untouched.Id))["status"].GetString());
    }

    [Fact]
    public async Task SelectToMultiSelect_Wrapped_LowerPrecision_Rounded_MarkdownToText_Cut()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, properties: $$"""{ "status": "doing", "estimate": 2.5, "notes": "{{new string('n', 10_005)}}" }""");

        await ChangeSchemaAsync(a, new UpdateSchemaRequest(
            a.Collection.Id,
            Update: [new("status", Type: "multi_select"), new("estimate", Precision: 0), new("notes", Type: "text")],
            ConfirmRemoval: true));

        var properties = await PropertiesAsync(record.Id);
        Assert.Equal("""["doing"]""", properties["status"].GetRawText());
        Assert.Equal(3, properties["estimate"].GetDecimal());
        Assert.Equal(10_000, properties["notes"].GetString()!.Length);
    }

    [Fact]
    public async Task OtherWorkspace_NotTouched()
    {
        var a = await ArrangeAsync();
        var beta = Enter(a.Seed.Beta, a.Seed.BetaOwner);
        var betaSpace = await CreateSpaceAsync(beta, a.Seed.BetaOwner);
        var betaCollection = await CreateCollectionAsync(beta, a.Seed.BetaOwner, betaSpace.Id, "Aufgaben",
            new Agcowork.Application.Schema.PropertyDefinitionInput("notes", "Notes", "markdown"));
        Db.ChangeTracker.Clear();
        var betaRecord = await new CreateRecord(Db, TestActor.Agent(a.Seed.BetaOwner.Id), beta, Validator(), Activity, Clock)
            .HandleAsync(new CreateRecordRequest(betaCollection.Id, Title: "B", Properties: Props("""{ "notes": "keep" }""")), Ct);
        Enter(a.Seed.Alpha, a.Seed.AlphaOwner);

        await ChangeSchemaAsync(a, new UpdateSchemaRequest(a.Collection.Id, Remove: ["notes"], ConfirmRemoval: true));

        Enter(a.Seed.Beta, a.Seed.BetaOwner);
        Assert.Equal("keep", (await PropertiesAsync(betaRecord.Value.Id))["notes"].GetString());
    }

    [Fact]
    public async Task MarkdownToText_CutDoesNotSplitSurrogatePair()
    {
        var a = await ArrangeAsync();
        var text = new string('n', 9_999) + "😀" + "tail";
        var record = await CreateRecordAsync(a, properties: System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["notes"] = text }));

        await ChangeSchemaAsync(a, new UpdateSchemaRequest(a.Collection.Id, Update: [new("notes", Type: "text")], ConfirmRemoval: true));

        var cut = (await PropertiesAsync(record.Id))["notes"].GetString()!;
        Assert.Equal(9_999, cut.Length);
        Assert.False(char.IsHighSurrogate(cut[^1]));
    }

    [Fact]
    public async Task SchemaChangedActivity_ReportsAffectedRecordsCleanedKeysAndRemovedOptions()
    {
        var a = await ArrangeAsync();
        await CreateRecordAsync(a, "Eins", """{ "status": "doing", "tags": ["db", "api"] }""");
        await CreateRecordAsync(a, "Zwei", """{ "status": "doing" }""");
        await CreateRecordAsync(a, "Drei", """{ "status": "done", "tags": ["api"] }""");

        await ChangeSchemaAsync(a, new UpdateSchemaRequest(
            a.Collection.Id,
            Update: [new("status", Options: [new("todo"), new("done")]), new("tags", Options: [new("api")])],
            ConfirmRemoval: true));
        await ChangeSchemaAsync(a, new UpdateSchemaRequest(a.Collection.Id, Add: [new("due", "Due", "date")]));

        var events = await Db.ActivityEvents.AsNoTracking()
            .Where(e => e.Type == Domain.Activity.ActivityTypes.CollectionSchemaChanged)
            .OrderBy(e => e.Seq)
            .ToListAsync(Ct);
        var cleanup = events[0].Diff;
        Assert.Equal(2, cleanup["affectedRecords"]!.GetValue<int>());
        Assert.Equal("""["status","tags"]""", cleanup["cleanedKeys"]!.ToJsonString());
        Assert.Equal("""["doing"]""", cleanup["removedOptions"]!["status"]!.ToJsonString());
        Assert.Equal("""["db"]""", cleanup["removedOptions"]!["tags"]!.ToJsonString());
        Assert.Equal(0, events[1].Diff["affectedRecords"]!.GetValue<int>());
        Assert.Empty(events[1].Diff["cleanedKeys"]!.AsArray());
    }
}
