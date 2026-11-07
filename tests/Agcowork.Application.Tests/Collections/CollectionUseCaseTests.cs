using System.Text.Json;
using Agcowork.Application.Collections;
using Agcowork.Application.Collections.Templates;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Schema;
using Agcowork.Application.Spaces;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Tests.Tenancy;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agcowork.Application.Tests.Collections;

public sealed class CollectionUseCaseTests(PostgresFixture postgres) : SchemaTestBase(postgres)
{
    [Fact]
    public async Task CreateCollection_WithoutSchema_AddsTitle()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.MemberOfBoth);
        var space = await CreateSpaceAsync(tenant, seed.MemberOfBoth);

        var collection = await CreateCollectionAsync(tenant, seed.MemberOfBoth, space.Id, "Aufgaben");

        var title = Assert.Single(collection.Schema);
        Assert.Equal("title", title.Key);
        Assert.Equal(PropertyType.Text, title.Type);
        Assert.True(title.Required);
        Assert.Equal(1, collection.SchemaVersion);
        Assert.Equal(seed.MemberOfBoth.Id, collection.CreatedBy);
    }

    [Fact]
    public async Task CreateCollection_SchemaRoundTripsThroughJsonb()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);
        var target = await CreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Komponenten");

        var created = await CreateCollectionAsync(
            tenant, seed.AlphaOwner, space.Id, "Entscheidungen",
            new PropertyDefinitionInput("status", "Status", "status", "Lifecycle.", true,
                [new("proposed", "Vorgeschlagen", "gray", "todo"), new("accepted", "Akzeptiert", "green", "done")], Default: JsonSerializer.SerializeToElement(" proposed ")),
            new PropertyDefinitionInput("affects", "Betrifft", "relation", TargetCollectionId: target.Id, Multiple: true),
            new PropertyDefinitionInput("estimate", "Estimate", "number", Unit: "h", Precision: 1),
            new PropertyDefinitionInput("at", "At", "datetime"));

        var stored = await Db.Collections.AsNoTracking().SingleAsync(c => c.Id == created.Id, Ct);
        Assert.Equal(["title", "status", "affects", "estimate", "at"], stored.Schema.Select(p => p.Key));
        var status = stored.Schema[1];
        Assert.Equal(StatusGroup.Done, status.Options![1].Group);
        Assert.Equal("Akzeptiert", status.Options[1].Label);
        Assert.Equal("proposed", status.Default!.Value.GetString());
        Assert.Equal(target.Id, stored.Schema[2].TargetCollectionId);
        Assert.True(stored.Schema[2].Multiple);
        Assert.Equal(1, stored.Schema[3].Precision);
        Assert.Equal(PropertyType.DateTime, stored.Schema[4].Type);

        var raw = await Db.Database.SqlQuery<string>($"SELECT schema::text AS \"Value\" FROM collections WHERE id = {created.Id}").SingleAsync(Ct);
        Assert.Contains("\"version\": 1", raw, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"datetime\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"group\": \"done\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("$type", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateCollection_InvalidSchema_AllErrorsWithAllowed()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var beta = Enter(seed.Beta, seed.BetaOwner);
        var betaSpace = await CreateSpaceAsync(beta, seed.BetaOwner);
        var foreign = await CreateCollectionAsync(beta, seed.BetaOwner, betaSpace.Id, "Fremd");
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);

        var result = await TryCreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Kaputt",
        [
            new("status", "Status", "text"),
            new("status", "Again", "text"),
            new("title", "Title", "number"),
            new("rel", "Rel", "relation", TargetCollectionId: foreign.Id),
            new("kind", "Kind", "enum"),
            new("Bad-Key", "Bad", "text"),
            new("level", "Level", "status", Options: [new("x", Group: "later")]),
        ]);

        var error = Invalid(result);
        Assert.Equal(ErrorCodes.Duplicate, Field(error, "schema[1].key").Code);
        var title = Field(error, "schema[2].type");
        Assert.Equal(ErrorCodes.Reserved, title.Code);
        Assert.Equal(["text"], title.Allowed);
        var type = Field(error, "schema[4].type");
        Assert.Equal(ErrorCodes.NotInOptions, type.Code);
        Assert.Equal(PropertyTypes.AllWireNames, type.Allowed);
        Assert.Equal(ErrorCodes.InvalidFormat, Field(error, "schema[5].key").Code);
        Assert.Equal(["todo", "in_progress", "done"], Field(error, "schema[6].options[0].group").Allowed);
        Assert.False(await Db.Collections.AnyAsync(c => c.Name == "Kaputt", Ct));

        // The foreign target is only reported once the input itself is valid (it needs a database lookup).
        var relation = await TryCreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Kaputt", [new("rel", "Rel", "relation", TargetCollectionId: foreign.Id)]);
        Assert.Equal(ErrorCodes.NotFound, Field(Invalid(relation), "schema[0].targetCollectionId").Code);
    }

    [Fact]
    public async Task CreateCollection_InvalidDefault_ErrorOnDefault()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);

        var result = await TryCreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "X",
        [
            Select("priority", "high", "low") with { Default = JsonSerializer.SerializeToElement("urgent") },
            new("owner", "Owner", "actor", Default: JsonSerializer.SerializeToElement(seed.AlphaOwner.Id)),
        ]);

        var error = Invalid(result);
        Assert.Equal(["high", "low"], Field(error, "schema[0].default").Allowed);
        Assert.Equal(ErrorCodes.NotAllowed, Field(error, "schema[1].default").Code);
    }

    [Fact]
    public async Task CreateCollection_MissingDescriptionAndSpace_RequiredWithReason()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);

        var result = await new CreateCollection(Db, TestActor.Agent(seed.AlphaOwner.Id), tenant, Clock, [])
            .HandleAsync(new CreateCollectionRequest(null, "X", " "), Ct);

        var error = Invalid(result);
        Assert.Equal(ErrorCodes.Required, Field(error, "spaceId").Code);
        Assert.Contains("agents read it", Field(error, "description").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateCollection_SpaceOfOtherWorkspace_NotFoundField()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var betaSpace = await CreateSpaceAsync(Enter(seed.Beta, seed.BetaOwner), seed.BetaOwner);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);

        var result = await TryCreateCollectionAsync(tenant, seed.AlphaOwner, betaSpace.Id, "X");

        Assert.Equal(ErrorCodes.NotFound, Field(Invalid(result), "spaceId").Code);
    }

    [Fact]
    public async Task CreateCollection_DuplicateNameInSpace_Conflict_OtherSpaceAllowed()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);
        var other = await CreateSpaceAsync(tenant, seed.AlphaOwner, "Technik");
        await CreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Aufgaben");

        var duplicate = await TryCreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, " Aufgaben ");
        var elsewhere = await TryCreateCollectionAsync(tenant, seed.AlphaOwner, other.Id, "Aufgaben");

        Assert.Equal("collection_name_taken", Assert.IsType<ConflictError>(duplicate.Error).Code);
        Assert.True(elsewhere.IsSuccess);
    }

    [Fact]
    public async Task CreateCollection_ArchivedSpace_Conflict()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);
        Assert.True((await new ArchiveSpace(Db, tenant, Clock).HandleAsync(new ArchiveSpaceRequest(space.Id), Ct)).IsSuccess);

        var result = await TryCreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "X");

        Assert.Equal("space_archived", Assert.IsType<ConflictError>(result.Error).Code);
    }

    [Fact]
    public async Task CreateCollection_ReadOnlyKey_Forbidden()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var space = await CreateSpaceAsync(Enter(seed.Alpha, seed.AlphaOwner), seed.AlphaOwner);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner, WorkspaceScope.Read);

        var result = await TryCreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "X");

        Assert.Equal("requires write", Assert.IsType<ForbiddenError>(result.Error).Message);
    }

    [Fact]
    public async Task CreateCollection_Hooks_RunBeforeSaveInSameUnitOfWork()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);
        var hook = new RecordingHook(Db);

        var result = await TryCreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Aufgaben", hooks: hook);

        Assert.True(result.IsSuccess);
        Assert.Equal(result.Value.Id, hook.Seen!.Id);
        Assert.Equal(EntityState.Added, hook.StateWhenCalled);
    }

    [Fact]
    public async Task CreateCollection_EntityAddedByHook_IsSavedWithTheCollection()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);

        var result = await TryCreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Aufgaben", hooks: new AddsTemplateHook(Db, Clock));

        Assert.True(result.IsSuccess);
        Db.ChangeTracker.Clear();
        var template = await Db.Templates.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((result.Value.Id, seed.Alpha.Id, "Created by hook"), (template.CollectionId, template.WorkspaceId, template.Name));
    }

    [Fact]
    public async Task CreateCollection_HookFails_NothingSaved()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);
        await CreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Aufgaben");

        // Same name: the save fails with a conflict, and the hook's entity is not stored either.
        var result = await TryCreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Aufgaben ", hooks: new AddsTemplateHook(Db, Clock));

        Assert.IsType<ConflictError>(result.Error);
        Db.ChangeTracker.Clear();
        Assert.False(await Db.Templates.AnyAsync(Ct));
    }

    [Fact]
    public async Task Schema_StoredFormat_ExactJsonSnapshot()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);
        var target = Guid.Parse("0190f5e2-7a4c-7000-8000-0000000000aa");
        var full = new PropertyDefinition
        {
            Key = "estimate", Name = "Estimate", Type = PropertyType.Number, Description = "Hours.", Required = true,
            Default = JsonSerializer.SerializeToElement(1.5), Unit = "h", Precision = 1,
        };
        var status = new PropertyDefinition
        {
            Key = "status", Name = "Status", Type = PropertyType.Status,
            Options = [new("open", "Open", "gray", StatusGroup.Todo), new("doing", "Doing", null, StatusGroup.InProgress)],
        };
        var relation = new PropertyDefinition { Key = "affects", Name = "Affects", Type = PropertyType.Relation, TargetCollectionId = target, Multiple = true };
        var collection = Collection.Create(seed.Alpha.Id, space.Id, "Snapshot", "D.", [PropertyDefinition.Title("T", "D"), full, status, relation], seed.AlphaOwner.Id, Clock);
        Db.Collections.Add(collection);
        await Db.SaveChangesAsync(Ct);

        var raw = await Db.Database.SqlQuery<string>($"SELECT schema::text AS \"Value\" FROM collections WHERE id = {collection.Id}").SingleAsync(Ct);

        // jsonb renders keys in its canonical order. A failing snapshot means the storage format changed: add a migration path.
        Assert.Equal(
            """{"version": 1, "properties": [{"key": "title", "name": "T", "type": "text", "multiple": false, "required": true, "description": "D"}, """
            + """{"key": "estimate", "name": "Estimate", "type": "number", "unit": "h", "default": 1.5, "multiple": false, "required": true, "precision": 1, "description": "Hours."}, """
            + """{"key": "status", "name": "Status", "type": "status", "options": [{"color": "gray", "group": "todo", "label": "Open", "value": "open"}, {"group": "in_progress", "label": "Doing", "value": "doing"}], "multiple": false, "required": false}, """
            + """{"key": "affects", "name": "Affects", "type": "relation", "multiple": true, "required": false, "targetCollectionId": "0190f5e2-7a4c-7000-8000-0000000000aa"}]}""",
            raw);
    }

    [Fact]
    public async Task CreateTemplate_PresetWithDeactivatedMember_ActorInactive()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);
        var collection = await CreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Aufgaben", new PropertyDefinitionInput("owner", "Owner", "actor"));
        var member = await Db.Actors.SingleAsync(a => a.Id == seed.MemberOfBoth.Id, Ct);
        member.Deactivate();
        await Db.SaveChangesAsync(Ct);

        var result = await TryCreateTemplateAsync(tenant, seed.AlphaOwner, collection.Id, "Bug", $$"""{ "owner": "{{seed.MemberOfBoth.Id}}" }""");

        Assert.Equal(ErrorCodes.ActorInactive, Field(Invalid(result), "propertiesPreset.owner").Code);
    }

    [Fact]
    public async Task ListCollections_BySpace_OnlyThatSpaceAndNotArchived()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var produkt = await CreateSpaceAsync(tenant, seed.AlphaOwner);
        var technik = await CreateSpaceAsync(tenant, seed.AlphaOwner, "Technik");
        await CreateCollectionAsync(tenant, seed.AlphaOwner, produkt.Id, "Aufgaben");
        await CreateCollectionAsync(tenant, seed.AlphaOwner, produkt.Id, "Analysen");
        var archived = await CreateCollectionAsync(tenant, seed.AlphaOwner, produkt.Id, "Alt");
        await CreateCollectionAsync(tenant, seed.AlphaOwner, technik.Id, "Komponenten");
        Assert.True((await new ArchiveCollection(Db, tenant, Clock).HandleAsync(new ArchiveCollectionRequest(archived.Id), Ct)).IsSuccess);

        var list = await new ListCollections(Db, tenant).HandleAsync(new ListCollectionsRequest(produkt.Id), Ct);
        var all = await new ListCollections(Db, tenant).HandleAsync(new ListCollectionsRequest(IncludeArchived: true), Ct);
        var unknown = await new ListCollections(Db, tenant).HandleAsync(new ListCollectionsRequest(Guid.CreateVersion7()), Ct);

        Assert.Equal(["Analysen", "Aufgaben"], list.Value.Select(c => c.Name));
        Assert.Equal(4, all.Value.Count);
        Assert.IsType<NotFoundError>(unknown.Error);
    }

    [Fact]
    public async Task UpdateCollection_EditorWithoutSchema_Forbidden_AdminRenames()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var owner = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(owner, seed.AlphaOwner);
        var collection = await CreateCollectionAsync(owner, seed.AlphaOwner, space.Id, "Aufgaben");

        var editor = await new UpdateCollection(Db, Enter(seed.Alpha, seed.MemberOfBoth)).HandleAsync(new UpdateCollectionRequest(collection.Id, "Tasks"), Ct);
        var renamed = await new UpdateCollection(Db, Enter(seed.Alpha, seed.AlphaOwner)).HandleAsync(new UpdateCollectionRequest(collection.Id, " Tasks ", "New description."), Ct);
        var clear = await new UpdateCollection(Db, owner).HandleAsync(new UpdateCollectionRequest(collection.Id, Description: ""), Ct);

        Assert.Equal("requires schema", Assert.IsType<ForbiddenError>(editor.Error).Message);
        Assert.Equal("Tasks", renamed.Value.Name);
        Assert.Equal("New description.", renamed.Value.Description);
        Assert.Equal(ErrorCodes.Required, Field(Invalid(clear), "description").Code);
    }

    [Fact]
    public async Task DescribeCollection_ContainsSchemaJsonSchemaExampleAndTemplates()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);
        var collection = await CreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Aufgaben", Select("priority", "high", "low"));
        await CreateTemplateAsync(tenant, seed.AlphaOwner, collection.Id, "Bug", """{ "priority": "high" }""", isDefault: true);

        var result = await new DescribeCollection(Db, Enter(seed.Alpha, seed.MemberOfBoth, WorkspaceScope.Read)).HandleAsync(new DescribeCollectionRequest(collection.Id), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(space.Id, result.Value.Space.Id);
        Assert.False(result.Value.JsonSchema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal("high", result.Value.Example["priority"]!.GetValue<string>());
        Assert.Equal("Bug", Assert.Single(result.Value.Templates).Name);
        Assert.Null(result.Value.Statistics.RecordCount);
    }

    [Fact]
    public async Task CreateTemplate_PresetNotMatchingSchema_ValidationErrors()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);
        var target = await CreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Komponenten");
        var collection = await CreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Aufgaben",
            Select("priority", "high", "low"),
            new PropertyDefinitionInput("owner", "Owner", "actor"),
            new PropertyDefinitionInput("affects", "Affects", "relation", TargetCollectionId: target.Id));

        var result = await TryCreateTemplateAsync(tenant, seed.AlphaOwner, collection.Id, "Bug",
            $$"""{ "priority": "urgent", "prio": 1, "owner": "{{seed.BetaOwner.Id}}", "affects": "{{Guid.CreateVersion7()}}" }""");

        var error = Invalid(result);
        Assert.Equal(["high", "low"], Field(error, "propertiesPreset.priority").Allowed);
        Assert.Equal(ErrorCodes.UnknownProperty, Field(error, "propertiesPreset.prio").Code);
        Assert.Equal(ErrorCodes.NotFound, Field(error, "propertiesPreset.owner").Code);
        Assert.Equal(ErrorCodes.NotFound, Field(error, "propertiesPreset.affects").Code);
        Assert.False(await Db.Templates.AnyAsync(Ct));
    }

    [Fact]
    public async Task CreateTemplate_ValidPreset_NormalizedAndStored()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);
        var collection = await CreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Aufgaben",
            Select("priority", "high", "low"), new PropertyDefinitionInput("owner", "Owner", "actor"));

        var template = await CreateTemplateAsync(tenant, seed.AlphaOwner, collection.Id, "Bug",
            $$"""{ "priority": " high ", "owner": "{{seed.MemberOfBoth.Id.ToString().ToUpperInvariant()}}", "title": "Bug: " }""");

        Db.ChangeTracker.Clear();
        var stored = await Db.Templates.AsNoTracking().SingleAsync(t => t.Id == template.Id, Ct);
        Assert.Equal("high", stored.PropertiesPreset["priority"].GetString());
        Assert.Equal(seed.MemberOfBoth.Id.ToString(), stored.PropertiesPreset["owner"].GetString());
        Assert.Equal("Bug:", stored.PropertiesPreset["title"].GetString());
    }

    [Fact]
    public async Task Templates_DefaultMovesBetweenTemplates_AndNamesAreUnique()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);
        var collection = await CreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Entscheidungen");

        var first = await CreateTemplateAsync(tenant, seed.AlphaOwner, collection.Id, "ADR", "{}", isDefault: true);
        var second = await CreateTemplateAsync(tenant, seed.AlphaOwner, collection.Id, "Kurz", "{}", isDefault: true);
        var duplicate = await TryCreateTemplateAsync(tenant, seed.AlphaOwner, collection.Id, "ADR", "{}");

        Db.ChangeTracker.Clear();
        var list = await new ListTemplates(Db, tenant).HandleAsync(new ListTemplatesRequest(collection.Id), Ct);
        Assert.Equal([second.Id, first.Id], list.Value.Select(t => t.Id));
        Assert.Equal([true, false], list.Value.Select(t => t.IsDefault));
        Assert.Equal("template_name_taken", Assert.IsType<ConflictError>(duplicate.Error).Code);

        var update = await new UpdateTemplate(Db, tenant, Validator())
            .HandleAsync(new UpdateTemplateRequest(collection.Id, first.Id, IsDefault: true, BodyMarkdown: "## Kontext\n"), Ct);
        Assert.True(update.IsSuccess);
        Db.ChangeTracker.Clear();
        Assert.Equal(first.Id, (await Db.Templates.SingleAsync(t => t.IsDefault, Ct)).Id);

        var delete = await new DeleteTemplate(Db, tenant).HandleAsync(new DeleteTemplateRequest(collection.Id, first.Id), Ct);
        Assert.True(delete.IsSuccess);
        Assert.Equal(1, await Db.Templates.CountAsync(Ct));
    }

    [Fact]
    public async Task Templates_EditorWithoutSchema_Forbidden_ReadAllowed()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var owner = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(owner, seed.AlphaOwner);
        var collection = await CreateCollectionAsync(owner, seed.AlphaOwner, space.Id, "Entscheidungen");
        var editor = Enter(seed.Alpha, seed.MemberOfBoth);

        var create = await new CreateTemplate(Db, TestActor.Agent(seed.MemberOfBoth.Id), editor, Validator(), Clock)
            .HandleAsync(new CreateTemplateRequest(collection.Id, "ADR"), Ct);
        var list = await new ListTemplates(Db, editor).HandleAsync(new ListTemplatesRequest(collection.Id), Ct);

        Assert.Equal("requires schema", Assert.IsType<ForbiddenError>(create.Error).Message);
        Assert.True(list.IsSuccess);
    }

    [Fact]
    public async Task SchemaProvider_OwnCollection_SnapshotCachedPerScope_ForeignNull()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var beta = Enter(seed.Beta, seed.BetaOwner);
        var foreign = await CreateCollectionAsync(beta, seed.BetaOwner, (await CreateSpaceAsync(beta, seed.BetaOwner)).Id, "Fremd");
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var collection = await CreateCollectionAsync(tenant, seed.AlphaOwner, (await CreateSpaceAsync(tenant, seed.AlphaOwner)).Id, "Aufgaben", Select("priority", "high"));
        var services = new ServiceCollection();
        services.AddSingleton<Application.Common.Persistence.IAgcoworkDbContext>(Db);
        services.AddApplication();
        using var scope = services.BuildServiceProvider().CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<ISchemaProvider>();

        var snapshot = await provider.GetSchemaAsync(collection.Id, Ct);
        await Db.Collections.Where(c => c.Id == collection.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.SchemaVersion, 5), Ct);
        var cached = await provider.GetSchemaAsync(collection.Id, Ct);
        provider.Invalidate(collection.Id);
        var fresh = await provider.GetSchemaAsync(collection.Id, Ct);

        Assert.Equal(["title", "priority"], snapshot!.Properties.Select(p => p.Key));
        Assert.Same(snapshot, cached);
        Assert.Equal(5, fresh!.SchemaVersion);
        Assert.Null(await provider.GetSchemaAsync(foreign.Id, Ct));
    }

    private Task<Result<TemplateDto>> TryCreateTemplateAsync(TenantContext tenant, Actor actor, Guid collectionId, string name, string preset, bool isDefault = false) =>
        new CreateTemplate(Db, TestActor.Agent(actor.Id), tenant, Validator(), Clock)
            .HandleAsync(new CreateTemplateRequest(collectionId, name, "When to use it.", Schema.SchemaFixtures.Props(preset), "## Body\n", isDefault), Ct);

    private async Task<TemplateDto> CreateTemplateAsync(TenantContext tenant, Actor actor, Guid collectionId, string name, string preset, bool isDefault = false)
    {
        var result = await TryCreateTemplateAsync(tenant, actor, collectionId, name, preset, isDefault);
        Assert.True(result.IsSuccess, result.Error is ValidationError v ? string.Join("; ", v.Errors.Select(e => $"{e.Field}:{e.Code}")) : result.Error?.ToString());
        return result.Value;
    }

    private sealed class AddsTemplateHook(AgcoworkDbContext db, TimeProvider clock) : ICollectionCreatedHook
    {
        public Task OnCollectionCreatedAsync(Collection collection, CancellationToken cancellationToken)
        {
            db.Templates.Add(Template.Create(collection.WorkspaceId, collection.Id, "Created by hook", null, null, null, false, collection.CreatedBy, clock));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHook(AgcoworkDbContext db) : ICollectionCreatedHook
    {
        public Collection? Seen { get; private set; }

        public EntityState StateWhenCalled { get; private set; }

        public Task OnCollectionCreatedAsync(Collection collection, CancellationToken cancellationToken)
        {
            Seen = collection;
            StateWhenCalled = db.Entry(collection).State;
            return Task.CompletedTask;
        }
    }
}
