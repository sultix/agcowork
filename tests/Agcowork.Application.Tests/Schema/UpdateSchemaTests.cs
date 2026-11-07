using System.Text.Json;
using Agcowork.Application.Collections;
using Agcowork.Application.Collections.Templates;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Schema;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Tests.Collections;
using Agcowork.Application.Tests.Tenancy;
using Agcowork.Domain.Collections.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Tests.Schema;

public sealed class UpdateSchemaTests(PostgresFixture postgres) : SchemaTestBase(postgres)
{
    private async Task<(TenancySeed Seed, TenantContext Tenant, CollectionDto Collection)> ArrangeAsync()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);
        var collection = await CreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Aufgaben",
            Select("priority", "high", "low") with { Default = JsonSerializer.SerializeToElement("low") },
            new PropertyDefinitionInput("summary", "Summary", "text"),
            new PropertyDefinitionInput("estimate", "Estimate", "number"));
        return (seed, tenant, collection);
    }

    private Task<Result<CollectionSchemaDto>> UpdateAsync(TenantContext tenant, UpdateSchemaRequest request, params ISchemaChangedHook[] extraHooks)
    {
        Db.ChangeTracker.Clear();
        return new UpdateSchema(Db, tenant, [new TemplatePresetSchemaHook(Db), .. extraHooks], Activity).HandleAsync(request, Ct);
    }

    private async Task<Guid> CreateTemplateAsync(TenancySeed seed, TenantContext tenant, Guid collectionId, string preset)
    {
        var template = await new CreateTemplate(Db, TestActor.Agent(seed.AlphaOwner.Id), tenant, Validator(), Clock)
            .HandleAsync(new CreateTemplateRequest(collectionId, $"T{Guid.NewGuid():N}", PropertiesPreset: SchemaFixtures.Props(preset)), Ct);
        Assert.True(template.IsSuccess);
        return template.Value.Id;
    }

    private async Task<IReadOnlyDictionary<string, JsonElement>> PresetAsync(Guid templateId)
    {
        Db.ChangeTracker.Clear();
        return (await Db.Templates.AsNoTracking().SingleAsync(t => t.Id == templateId, Ct)).PropertiesPreset;
    }

    private async Task<Domain.Collections.Collection> ReloadAsync(Guid id)
    {
        Db.ChangeTracker.Clear();
        return await Db.Collections.AsNoTracking().SingleAsync(c => c.Id == id, Ct);
    }

    [Fact]
    public async Task UpdateSchema_Add_AppendsAndIncrementsVersion()
    {
        var (_, tenant, collection) = await ArrangeAsync();

        var result = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Add: [new("due", "Due", "date", "When it is due.")]));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.SchemaVersion);
        Assert.Equal(["title", "priority", "summary", "estimate", "due"], result.Value.Properties.Select(p => p.Key));
        Assert.Equal("date", result.Value.JsonSchema["properties"]!["due"]!["format"]!.GetValue<string>());
        Assert.Equal(2, (await ReloadAsync(collection.Id)).SchemaVersion);
    }

    [Fact]
    public async Task UpdateSchema_Update_ChangesOnlyGivenFields()
    {
        var (_, tenant, collection) = await ArrangeAsync();

        var result = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Update:
        [
            new("priority", Name: "Priorität", Required: true, Options: [new("high"), new("mid"), new("low")]),
            new("estimate", Description: "Hours.", Unit: "h"),
        ]));

        Assert.True(result.IsSuccess);
        var stored = await ReloadAsync(collection.Id);
        var priority = stored.Schema.Find("priority")!;
        Assert.Equal("Priorität", priority.Name);
        Assert.True(priority.Required);
        Assert.Equal(["high", "mid", "low"], priority.Options!.Select(o => o.Value));
        Assert.Equal("low", priority.Default!.Value.GetString());
        Assert.Equal(PropertyType.Select, priority.Type);
        var estimate = stored.Schema.Find("estimate")!;
        Assert.Equal(("Hours.", "h"), (estimate.Description, estimate.Unit));
        Assert.Equal(2, stored.SchemaVersion);
    }

    [Fact]
    public async Task UpdateSchema_RenameKey_KeyImmutable()
    {
        var (_, tenant, collection) = await ArrangeAsync();

        var result = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("summary", NewKey: "abstract")]));

        Assert.Equal(ErrorCodes.KeyImmutable, Field(Invalid(result), "update[0].newKey").Code);
        Assert.Equal(1, (await ReloadAsync(collection.Id)).SchemaVersion);
    }

    [Fact]
    public async Task UpdateSchema_DisallowedTypeChange_TypeChangeNotAllowedWithAllowed()
    {
        var (_, tenant, collection) = await ArrangeAsync();

        var result = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("summary", Type: "number")]));

        var error = Field(Invalid(result), "update[0].type");
        Assert.Equal(ErrorCodes.TypeChangeNotAllowed, error.Code);
        Assert.Equal(["text", "markdown"], error.Allowed);
    }

    [Fact]
    public async Task UpdateSchema_TextToMarkdown_Allowed()
    {
        var (_, tenant, collection) = await ArrangeAsync();

        var result = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("summary", Type: "markdown")]));

        Assert.Equal(PropertyType.Markdown, Assert.Single(result.Value.Properties, p => p.Key == "summary").Type);
    }

    [Fact]
    public async Task UpdateSchema_SelectToMultiSelect_WrapsDefaultAndTemplatePresets()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var template = await new CreateTemplate(Db, TestActor.Agent(seed.AlphaOwner.Id), tenant, Validator(), Clock)
            .HandleAsync(new CreateTemplateRequest(collection.Id, "Bug", PropertiesPreset: SchemaFixtures.Props("""{ "priority": "high", "summary": "x" }""")), Ct);
        Assert.True(template.IsSuccess);

        var result = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("priority", Type: "multi_select")]));

        Assert.True(result.IsSuccess);
        var priority = (await ReloadAsync(collection.Id)).Schema.Find("priority")!;
        Assert.Equal(PropertyType.MultiSelect, priority.Type);
        Assert.Equal(2, result.Value.SchemaVersion);
        Assert.Equal("""["low"]""", priority.Default!.Value.GetRawText());
        var stored = await Db.Templates.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("""["high"]""", stored.PropertiesPreset["priority"].GetRawText());
        Assert.Equal("\"x\"", stored.PropertiesPreset["summary"].GetRawText());
    }

    [Fact]
    public async Task UpdateSchema_RemoveWithoutConfirmation_RemovalNotConfirmed()
    {
        var (_, tenant, collection) = await ArrangeAsync();

        var result = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Remove: ["summary"]));

        Assert.Equal(ErrorCodes.RemovalNotConfirmed, Field(Invalid(result), "confirmRemoval").Code);
        var stored = await ReloadAsync(collection.Id);
        Assert.NotNull(stored.Schema.Find("summary"));
        Assert.Equal(1, stored.SchemaVersion);
    }

    [Fact]
    public async Task UpdateSchema_RemoveConfirmed_RemovesAndStripsPresets()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        Assert.True((await new CreateTemplate(Db, TestActor.Agent(seed.AlphaOwner.Id), tenant, Validator(), Clock)
            .HandleAsync(new CreateTemplateRequest(collection.Id, "Bug", PropertiesPreset: SchemaFixtures.Props("""{ "priority": "high", "summary": "x" }""")), Ct)).IsSuccess);

        var result = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Remove: ["summary"], ConfirmRemoval: true));

        Assert.True(result.IsSuccess);
        Assert.Equal(["title", "priority", "estimate"], result.Value.Properties.Select(p => p.Key));
        Assert.Equal(2, result.Value.SchemaVersion);
        Assert.Equal(["priority"], (await Db.Templates.AsNoTracking().SingleAsync(Ct)).PropertiesPreset.Keys);
    }

    [Fact]
    public async Task UpdateSchema_RemoveTitleOrUnknown_Errors()
    {
        var (_, tenant, collection) = await ArrangeAsync();

        var result = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Remove: ["title", "nope"], ConfirmRemoval: true));

        var error = Invalid(result);
        Assert.Equal(ErrorCodes.NotAllowed, Field(error, "remove[0]").Code);
        Assert.Equal(ErrorCodes.NotFound, Field(error, "remove[1]").Code);
    }

    [Fact]
    public async Task UpdateSchema_AddExistingKey_Duplicate_ReAddAfterRemovalNeedsSecondRequest()
    {
        var (_, tenant, collection) = await ArrangeAsync();

        var duplicate = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Add: [new("summary", "Summary", "markdown")]));
        var sameRequest = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Add: [new("summary", "Summary", "number")], Remove: ["summary"], ConfirmRemoval: true));
        var removed = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Remove: ["summary"], ConfirmRemoval: true));
        var readded = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Add: [new("summary", "Summary", "number")]));

        Assert.Equal(ErrorCodes.Duplicate, Field(Invalid(duplicate), "add[0].key").Code);
        Assert.Equal(ErrorCodes.Duplicate, Field(Invalid(sameRequest), "add[0].key").Code);
        Assert.True(removed.IsSuccess);
        Assert.Equal(PropertyType.Number, Assert.Single(readded.Value.Properties, p => p.Key == "summary").Type);
        Assert.Equal(3, readded.Value.SchemaVersion);
    }

    [Fact]
    public async Task UpdateSchema_UnknownKeyOrInvalidOptions_Errors()
    {
        var (_, tenant, collection) = await ArrangeAsync();

        var result = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Update:
        [
            new("nope", Name: "X"),
            new("priority", Options: [new("high"), new("high")]),
        ]));

        var error = Invalid(result);
        Assert.Equal(ErrorCodes.NotFound, Field(error, "update[0].key").Code);
        Assert.Equal(ErrorCodes.Duplicate, Field(error, "update[1].options[1].value").Code);
    }

    [Fact]
    public async Task UpdateSchema_OptionsChangeInvalidatesDefault_ErrorOnDefault()
    {
        var (_, tenant, collection) = await ArrangeAsync();

        var result = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("priority", Options: [new("high")])]));

        Assert.Equal(ErrorCodes.NotInOptions, Field(Invalid(result), "update[0].default").Code);
    }

    [Fact]
    public async Task UpdateSchema_ExpectedVersionMismatch_Conflict()
    {
        var (_, tenant, collection) = await ArrangeAsync();

        var result = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Add: [new("due", "Due", "date")], ExpectedSchemaVersion: 7));

        var error = Assert.IsType<ConflictError>(result.Error);
        Assert.Equal("schema_version_conflict", error.Code);
        Assert.Contains("version 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateSchema_ConcurrentChange_ConflictAndNothingSaved()
    {
        var (_, tenant, collection) = await ArrangeAsync();
        Db.ChangeTracker.Clear();
        var racing = new RacingDbContext(Db, RacingDbContext.BumpSchemaVersionInTransaction(Db, collection.Id));

        var result = await new UpdateSchema(racing, tenant, [], Activity).HandleAsync(new UpdateSchemaRequest(collection.Id, Add: [new("due", "Due", "date")]), Ct);

        var error = Assert.IsType<ConflictError>(result.Error);
        Assert.Equal(("schema_version_conflict", "expectedSchemaVersion"), (error.Code, error.Field));
        var stored = await ReloadAsync(collection.Id);
        Assert.Null(stored.Schema.Find("due"));
        // The bump ran in the rolled-back transaction: nothing of it remains either.
        Assert.Equal(1, stored.SchemaVersion);
    }

    [Fact]
    public async Task UpdateCollection_ConcurrentSchemaChange_Conflict()
    {
        var (_, tenant, collection) = await ArrangeAsync();
        Db.ChangeTracker.Clear();
        var racing = new RacingDbContext(Db, RacingDbContext.BumpSchemaVersion(Db, collection.Id));

        var result = await new UpdateCollection(racing, tenant, Activity).HandleAsync(new UpdateCollectionRequest(collection.Id, "Tasks"), Ct);

        Assert.Equal("schema_version_conflict", Assert.IsType<ConflictError>(result.Error).Code);
        Assert.Equal("Aufgaben", (await ReloadAsync(collection.Id)).Name);
    }

    [Fact]
    public async Task ArchiveCollection_ConcurrentSchemaChange_Conflict()
    {
        var (_, tenant, collection) = await ArrangeAsync();
        Db.ChangeTracker.Clear();
        var racing = new RacingDbContext(Db, RacingDbContext.BumpSchemaVersion(Db, collection.Id));

        var result = await new ArchiveCollection(racing, tenant, Clock, Activity).HandleAsync(new ArchiveCollectionRequest(collection.Id), Ct);

        Assert.Equal("schema_version_conflict", Assert.IsType<ConflictError>(result.Error).Code);
        Assert.Null((await ReloadAsync(collection.Id)).ArchivedAt);
    }

    [Fact]
    public async Task UpdateSchema_RemoveOptions_NeedsConfirmation_ThenPresetsDropValues()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var template = await CreateTemplateAsync(seed, tenant, collection.Id, """{ "priority": "high", "summary": "x" }""");
        var change = new UpdateSchemaRequest(collection.Id, Update: [new("priority", Options: [new("low")])]);

        var unconfirmed = await UpdateAsync(tenant, change);
        var confirmed = await UpdateAsync(tenant, change with { ConfirmRemoval = true });

        var error = Field(Invalid(unconfirmed), "confirmRemoval");
        Assert.Equal(ErrorCodes.RemovalNotConfirmed, error.Code);
        Assert.Contains("'high'", error.Message, StringComparison.Ordinal);
        Assert.True(confirmed.IsSuccess);
        Assert.Equal(["summary"], (await PresetAsync(template)).Keys);
    }

    [Fact]
    public async Task UpdateSchema_RemoveMultiSelectOption_FiltersPresetArray()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        Assert.True((await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Add: [Select("tags", "a", "b") with { Type = "multi_select" }]))).IsSuccess);
        var template = await CreateTemplateAsync(seed, tenant, collection.Id, """{ "tags": ["a", "b"] }""");

        var result = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("tags", Options: [new("a")])], ConfirmRemoval: true));

        Assert.True(result.IsSuccess);
        Assert.Equal("""["a"]""", (await PresetAsync(template))["tags"].GetRawText());
    }

    [Fact]
    public async Task UpdateSchema_LowerPrecision_NeedsConfirmation_ThenInvalidPresetDropped()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var template = await CreateTemplateAsync(seed, tenant, collection.Id, """{ "estimate": 2.5 }""");

        var unconfirmed = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("estimate", Precision: 0)]));
        var confirmed = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("estimate", Precision: 0)], ConfirmRemoval: true));
        var raise = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("estimate", Precision: 2)]));

        Assert.Contains("precision", Field(Invalid(unconfirmed), "confirmRemoval").Message, StringComparison.Ordinal);
        Assert.True(confirmed.IsSuccess);
        Assert.Empty(await PresetAsync(template));
        Assert.True(raise.IsSuccess);
    }

    [Fact]
    public async Task UpdateSchema_MarkdownToText_NeedsConfirmation_TextToMarkdownDoesNot()
    {
        var (_, tenant, collection) = await ArrangeAsync();

        var toMarkdown = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("summary", Type: "markdown")]));
        var unconfirmed = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("summary", Type: "text")]));
        var confirmed = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("summary", Type: "text")], ConfirmRemoval: true));

        Assert.True(toMarkdown.IsSuccess);
        Assert.Equal(ErrorCodes.RemovalNotConfirmed, Field(Invalid(unconfirmed), "confirmRemoval").Code);
        Assert.Equal(3, confirmed.Value.SchemaVersion);
    }

    [Fact]
    public async Task UpdateSchema_UpdateWithoutActualChange_KeepsVersion()
    {
        var (_, tenant, collection) = await ArrangeAsync();

        var result = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("priority", Name: "priority", Options: [new("high"), new("low")])]));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.SchemaVersion);
        Assert.Equal(1, (await ReloadAsync(collection.Id)).SchemaVersion);
    }

    [Fact]
    public async Task UpdateSchema_Hooks_GetSchemaChangeBeforeSave()
    {
        var (_, tenant, collection) = await ArrangeAsync();
        var hook = new RecordingSchemaHook(Db);

        var result = await UpdateAsync(
            tenant,
            new UpdateSchemaRequest(collection.Id, Update: [new("priority", Type: "multi_select", Options: [new("high")], ClearDefault: true), new("summary", Type: "markdown")], Remove: ["estimate"], ConfirmRemoval: true),
            hook);

        Assert.True(result.IsSuccess);
        var change = hook.Change!;
        Assert.Equal(["estimate"], change.RemovedKeys);
        Assert.Equal(new PropertyTypeChange(PropertyType.Select, PropertyType.MultiSelect), change.TypeChanges["priority"]);
        Assert.Equal(new PropertyTypeChange(PropertyType.Text, PropertyType.Markdown), change.TypeChanges["summary"]);
        Assert.Equal(["low"], change.RemovedOptions["priority"]);
        Assert.Equal(4, change.Before.Count);
        Assert.Equal(3, change.After.Count);
        Assert.Equal(1, hook.VersionInDatabaseWhenCalled);
    }

    private sealed class RecordingSchemaHook(Infrastructure.Persistence.AgcoworkDbContext db) : ISchemaChangedHook
    {
        public SchemaChange? Change { get; private set; }

        public int? VersionInDatabaseWhenCalled { get; private set; }

        public async Task OnSchemaChangedAsync(Domain.Collections.Collection collection, SchemaChange change, CancellationToken cancellationToken)
        {
            Change = change;
            VersionInDatabaseWhenCalled = await db.Collections.AsNoTracking().Where(c => c.Id == collection.Id).Select(c => c.SchemaVersion).SingleAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task UpdateSchema_EditorWithoutSchemaScope_Forbidden()
    {
        var (seed, _, collection) = await ArrangeAsync();

        var result = await UpdateAsync(Enter(seed.Alpha, seed.MemberOfBoth), new UpdateSchemaRequest(collection.Id, Add: [new("due", "Due", "date")]));

        Assert.Equal("requires schema", Assert.IsType<ForbiddenError>(result.Error).Message);
    }

    [Fact]
    public async Task UpdateSchema_RelationToOtherWorkspace_NotFound()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var beta = Enter(seed.Beta, seed.BetaOwner);
        var foreign = await CreateCollectionAsync(beta, seed.BetaOwner, (await CreateSpaceAsync(beta, seed.BetaOwner)).Id, "Fremd");
        Tenant.Current = tenant;

        var result = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Add: [new("rel", "Rel", "relation", TargetCollectionId: foreign.Id)]));
        var self = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Add: [new("parent", "Parent", "relation", TargetCollectionId: collection.Id)]));

        Assert.Equal(ErrorCodes.NotFound, Field(Invalid(result), "add[0].targetCollectionId").Code);
        Assert.True(self.IsSuccess);
    }

    [Fact]
    public async Task UpdateSchema_CollectionOfOtherWorkspace_NotFound()
    {
        var (seed, _, collection) = await ArrangeAsync();

        var result = await UpdateAsync(Enter(seed.Beta, seed.BetaOwner), new UpdateSchemaRequest(collection.Id, Add: [new("due", "Due", "date")]));

        Assert.IsType<NotFoundError>(result.Error);
    }

    [Fact]
    public async Task UpdateSchema_NoChangesOrArchived_Rejected()
    {
        var (_, tenant, collection) = await ArrangeAsync();

        var empty = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id));
        Assert.True((await new ArchiveCollection(Db, tenant, Clock, Activity).HandleAsync(new ArchiveCollectionRequest(collection.Id), Ct)).IsSuccess);
        var archived = await UpdateAsync(tenant, new UpdateSchemaRequest(collection.Id, Add: [new("due", "Due", "date")]));

        Assert.Equal(ErrorCodes.Required, Field(Invalid(empty), "add").Code);
        Assert.Equal("collection_archived", Assert.IsType<ConflictError>(archived.Error).Code);
    }
}
