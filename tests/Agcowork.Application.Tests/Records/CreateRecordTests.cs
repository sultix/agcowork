using Agcowork.Application.Collections;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Agcowork.Application.Schema;
using Agcowork.Application.Tests.Tenancy;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Records;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Tests.Records;

public sealed class CreateRecordTests(PostgresFixture postgres) : RecordTestBase(postgres)
{
    [Fact]
    public async Task Create_InCollection_AppliesDefaults_StoresRevisionOneAndActivity()
    {
        var a = await ArrangeAsync();

        var record = await CreateRecordAsync(a, "  Schema migrieren ", """{ "tags": [" db ", "db"], "estimate": 2.5 }""", "Body");

        Assert.Equal(("Schema migrieren", 1, a.Collection.Id, a.SpaceId), (record.Title, record.Revision, record.CollectionId, record.SpaceId));
        Assert.Equal(["status", "tags", "estimate"], record.Properties.Keys);
        Assert.Equal("todo", record.Properties["status"].GetString());
        Assert.Equal("""["db"]""", record.Properties["tags"].GetRawText());
        Assert.Null(record.Body);

        var stored = await Db.Records.AsNoTracking().SingleAsync(r => r.Id == record.Id, Ct);
        Assert.Equal("Body", stored.Body);
        Assert.False(stored.Properties.ContainsKey("title"));
        var revision = await Db.Revisions.AsNoTracking().SingleAsync(r => r.RecordId == record.Id, Ct);
        Assert.Equal((1, RevisionKind.Created, a.Seed.AlphaOwner.Id), (revision.No, revision.Kind, revision.ActorId));
        var activity = await Db.ActivityEvents.AsNoTracking().SingleAsync(e => e.TargetId == record.Id, Ct);
        Assert.Equal((ActivityTypes.RecordCreated, a.Collection.Id, a.SpaceId, a.Seed.AlphaOwner.Id), (activity.Type, activity.CollectionId, activity.SpaceId, activity.ActorId));
        Assert.Equal("Schema migrieren", activity.Diff["title"]!["new"]!.GetValue<string>());
        Assert.Equal(2.5m, activity.Diff["properties"]!["estimate"]!["new"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task Create_WithDefaultTemplate_PresetMixedWithSent_SentWins_BodyFromTemplate()
    {
        var a = await ArrangeAsync();
        await CreateTemplateAsync(a, """{ "status": "doing", "tags": ["api"], "estimate": 1 }""", "## Kontext\n\n## Log\n", isDefault: true);

        var result = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Mit Vorlage", Properties: Props("""{ "status": "done", "tags": null }""")));

        Assert.True(result.IsSuccess, Describe(result.Error));
        Assert.Equal("done", result.Value.Properties["status"].GetString());
        Assert.False(result.Value.Properties.ContainsKey("tags"));
        Assert.Equal(1, result.Value.Properties["estimate"].GetDecimal());
        Assert.Equal("## Kontext\n\n## Log\n", (await Db.Records.AsNoTracking().SingleAsync(r => r.Id == result.Value.Id, Ct)).Body);
    }

    [Fact]
    public async Task Create_SkipTemplate_StartsEmptyDespiteDefaultTemplate()
    {
        var a = await ArrangeAsync();
        await CreateTemplateAsync(a, """{ "status": "doing", "estimate": 1, "title": "Vorlagentitel" }""", "## Kontext\n", isDefault: true);

        var skipped = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Leer", SkipTemplate: true));
        var withDefault = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Mit"));
        var titleless = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, SkipTemplate: true));

        Assert.True(skipped.IsSuccess, Describe(skipped.Error));
        Assert.Equal("todo", skipped.Value.Properties["status"].GetString());
        Assert.False(skipped.Value.Properties.ContainsKey("estimate"));
        Assert.Equal(string.Empty, (await Db.Records.AsNoTracking().SingleAsync(r => r.Id == skipped.Value.Id, Ct)).Body);
        Assert.Equal("doing", withDefault.Value.Properties["status"].GetString());
        Assert.Equal("## Kontext\n", (await Db.Records.AsNoTracking().SingleAsync(r => r.Id == withDefault.Value.Id, Ct)).Body);
        // Without the template there is no title to fall back to.
        Assert.Equal(ErrorCodes.Required, Field(Invalid(titleless), "title").Code);
    }

    [Fact]
    public async Task Create_SkipTemplateWithTemplateId_ConflictOnSkipTemplate_NothingStored()
    {
        var a = await ArrangeAsync();
        var template = await CreateTemplateAsync(a, """{ "status": "doing" }""");

        var result = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "X", TemplateId: template.Id, SkipTemplate: true));

        Assert.Equal(ErrorCodes.Conflict, Field(Invalid(result), "skipTemplate").Code);
        Assert.Equal(0, await Db.Records.CountAsync(r => r.CollectionId == a.Collection.Id, Ct));
    }

    [Fact]
    public async Task Create_SkipTemplateOnFreePage_IsIgnored()
    {
        var a = await ArrangeAsync();

        var result = await TryCreateAsync(a, new CreateRecordRequest(SpaceId: a.SpaceId, Title: "Seite", SkipTemplate: true));

        Assert.True(result.IsSuccess, Describe(result.Error));
        Assert.Null(result.Value.CollectionId);
    }

    [Fact]
    public async Task Create_NamedTemplateAndSentBody_BodyWins_TemplateOfOtherCollectionNotFound()
    {
        var a = await ArrangeAsync();
        await CreateTemplateAsync(a, """{ "status": "doing" }""", isDefault: true, name: "Default");
        var named = await CreateTemplateAsync(a, """{ "status": "done", "title": "Bug" }""", "## Bug\n", name: "Bug");
        var other = await CreateCollectionAsync(a.Tenant, a.Seed.AlphaOwner, a.SpaceId, "Analysen");

        var result = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Body: "Eigener Body", TemplateId: named.Id));
        var wrong = await TryCreateAsync(a, new CreateRecordRequest(other.Id, Title: "X", TemplateId: named.Id));

        Assert.True(result.IsSuccess, Describe(result.Error));
        Assert.Equal(("Bug", "done"), (result.Value.Title, result.Value.Properties["status"].GetString()));
        Assert.Equal("Eigener Body", (await Db.Records.AsNoTracking().SingleAsync(r => r.Id == result.Value.Id, Ct)).Body);
        Assert.Equal(ErrorCodes.NotFound, Field(Invalid(wrong), "templateId").Code);
    }

    [Fact]
    public async Task Create_TemplatePresetNoLongerValid_ValueDroppedAndDefaultApplies()
    {
        var a = await ArrangeAsync();
        await CreateTemplateAsync(a, """{ "status": "doing", "tags": ["api"] }""", isDefault: true);
        // Schema change without the template hook: the preset still holds the removed option.
        Db.ChangeTracker.Clear();
        var change = await new UpdateSchema(Db, a.Tenant, [], Activity).HandleAsync(
            new UpdateSchemaRequest(a.Collection.Id, Update: [new("status", Options: [new("todo"), new("done")])], ConfirmRemoval: true), Ct);
        Assert.True(change.IsSuccess, Describe(change.Error));

        var result = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Nach Schemaänderung"));

        Assert.True(result.IsSuccess, Describe(result.Error));
        Assert.Equal("todo", result.Value.Properties["status"].GetString());
        Assert.Equal("""["api"]""", result.Value.Properties["tags"].GetRawText());
    }

    [Fact]
    public async Task Create_TitleAsProperty_Accepted_ConflictingTitles_Rejected()
    {
        var a = await ArrangeAsync();

        var fromProperties = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Properties: Props("""{ "title": "Aus Properties" }""")));
        var conflicting = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "A", Properties: Props("""{ "title": "B" }""")));

        Assert.Equal("Aus Properties", fromProperties.Value.Title);
        Assert.Equal(ErrorCodes.Conflict, Field(Invalid(conflicting), "properties.title").Code);
    }

    [Fact]
    public async Task Create_InvalidInput_AllFieldErrorsAtOnce()
    {
        var a = await ArrangeAsync();

        var result = await TryCreateAsync(a, new CreateRecordRequest(
            a.Collection.Id,
            Title: new string('t', 501),
            Properties: Props("""{ "colour": "red", "status": "maybe", "estimate": 1.25, "owner": "0190f5e2-7a4c-7000-8000-000000000999" }""")));

        var error = Invalid(result);
        Assert.Equal(ErrorCodes.TooLong, Field(error, "title").Code);
        var unknown = Field(error, "properties.colour");
        Assert.Equal(ErrorCodes.UnknownProperty, unknown.Code);
        Assert.Equal(["title", "status", "tags", "estimate", "owner", "notes"], unknown.Allowed);
        Assert.Equal(["todo", "doing", "done"], Field(error, "properties.status").Allowed);
        Assert.Equal(ErrorCodes.InvalidFormat, Field(error, "properties.estimate").Code);
        Assert.Equal(ErrorCodes.NotFound, Field(error, "properties.owner").Code);
    }

    [Fact]
    public async Task Create_MissingTitleOrRequiredValue_Required()
    {
        var a = await ArrangeAsync();

        var noTitle = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id));
        var nullStatus = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "T", Properties: Props("""{ "status": null }""")));

        Assert.Equal(ErrorCodes.Required, Field(Invalid(noTitle), "title").Code);
        Assert.Equal(ErrorCodes.Required, Field(Invalid(nullStatus), "properties.status").Code);
    }

    [Fact]
    public async Task Create_FreePageInSpace_OnlyTitle()
    {
        var a = await ArrangeAsync();

        var page = await TryCreateAsync(a, new CreateRecordRequest(SpaceId: a.SpaceId, Title: "Notiz", Body: "Text"));
        var withProperty = await TryCreateAsync(a, new CreateRecordRequest(SpaceId: a.SpaceId, Title: "Notiz", Properties: Props("""{ "status": "todo" }""")));
        var nothing = await TryCreateAsync(a, new CreateRecordRequest(Title: "Notiz"));

        Assert.True(page.IsSuccess, Describe(page.Error));
        Assert.Null(page.Value.CollectionId);
        Assert.Equal(ErrorCodes.UnknownProperty, Field(Invalid(withProperty), "properties.status").Code);
        Assert.Equal(ErrorCodes.Required, Field(Invalid(nothing), "collectionId").Code);
    }

    [Fact]
    public async Task Create_SpaceDiffersFromCollection_NotAllowed()
    {
        var a = await ArrangeAsync();
        var other = await CreateSpaceAsync(a.Tenant, a.Seed.AlphaOwner, "Technik");

        var result = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, other.Id, Title: "T"));

        Assert.Equal(ErrorCodes.NotAllowed, Field(Invalid(result), "spaceId").Code);
    }

    [Fact]
    public async Task Create_Relation_TargetsRecordsOfTargetCollectionOnly()
    {
        var a = await ArrangeAsync();
        var target = await CreateRecordAsync(a, "Ziel");
        var decisions = await CreateCollectionAsync(a.Tenant, a.Seed.AlphaOwner, a.SpaceId, "Entscheidungen",
            new PropertyDefinitionInput("affects", "Affects", "relation", TargetCollectionId: a.Collection.Id, Multiple: true));
        var freePage = (await TryCreateAsync(a, new CreateRecordRequest(SpaceId: a.SpaceId, Title: "Seite"))).Value;

        var valid = await TryCreateAsync(a, new CreateRecordRequest(decisions.Id, Title: "D", Properties: Props($$"""{ "affects": ["{{target.Id.ToString().ToUpperInvariant()}}"] }""")));
        var invalid = await TryCreateAsync(a, new CreateRecordRequest(decisions.Id, Title: "D", Properties: Props($$"""{ "affects": ["{{freePage.Id}}"] }""")));

        Assert.True(valid.IsSuccess, Describe(valid.Error));
        Assert.Equal($"[\"{target.Id}\"]", valid.Value.Properties["affects"].GetRawText());
        Assert.Equal(ErrorCodes.NotFound, Field(Invalid(invalid), "properties.affects[0]").Code);
    }

    [Fact]
    public async Task Create_ActorOfWorkspace_Accepted()
    {
        var a = await ArrangeAsync();

        var record = await CreateRecordAsync(a, "Mit Owner", $$"""{ "owner": "{{a.Seed.MemberOfBoth.Id}}" }""");

        Assert.Equal(a.Seed.MemberOfBoth.Id, record.Properties["owner"].GetGuid());
    }

    [Fact]
    public async Task Create_WithParent_Nested_UnknownParent_NotFound()
    {
        var a = await ArrangeAsync();
        var parent = (await TryCreateAsync(a, new CreateRecordRequest(SpaceId: a.SpaceId, Title: "Eltern"))).Value;

        var child = await TryCreateAsync(a, new CreateRecordRequest(SpaceId: a.SpaceId, Title: "Kind", ParentRecordId: parent.Id));
        var orphan = await TryCreateAsync(a, new CreateRecordRequest(SpaceId: a.SpaceId, Title: "Kind", ParentRecordId: Guid.CreateVersion7()));

        Assert.Equal(parent.Id, child.Value.ParentRecordId);
        Assert.Equal(ErrorCodes.NotFound, Field(Invalid(orphan), "parentRecordId").Code);
    }

    [Fact]
    public async Task Create_ArchivedCollection_Conflict()
    {
        var a = await ArrangeAsync();
        Assert.True((await new ArchiveCollection(Db, a.Tenant, Clock, Activity).HandleAsync(new ArchiveCollectionRequest(a.Collection.Id), Ct)).IsSuccess);

        var result = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "T"));

        Assert.Equal("collection_archived", Assert.IsType<ConflictError>(result.Error).Code);
    }

    [Fact]
    public async Task Create_ForeignCollection_NotFound()
    {
        var a = await ArrangeAsync();
        var beta = Enter(a.Seed.Beta, a.Seed.BetaOwner);
        var betaSpace = await CreateSpaceAsync(beta, a.Seed.BetaOwner);
        var betaCollection = await CreateCollectionAsync(beta, a.Seed.BetaOwner, betaSpace.Id, "Beta");
        Enter(a.Seed.Alpha, a.Seed.AlphaOwner);

        var result = await TryCreateAsync(a, new CreateRecordRequest(betaCollection.Id, Title: "T"));

        Assert.Equal(ErrorCodes.NotFound, Field(Invalid(result), "collectionId").Code);
    }

    [Fact]
    public async Task Create_ReadOnlyOrArchivedWorkspace_ForbiddenOrConflict()
    {
        var a = await ArrangeAsync();
        var readOnly = Enter(a.Seed.Alpha, a.Seed.AlphaOwner, WorkspaceScope.Read);
        var archived = TenancySeed.TenantFor(Db, a.Seed.Alpha, a.Seed.AlphaOwner, archived: true);

        var denied = await CreateUseCase(readOnly, a.Seed.AlphaOwner).HandleAsync(new CreateRecordRequest(a.Collection.Id, Title: "T"), Ct);
        var conflict = await CreateUseCase(archived, a.Seed.AlphaOwner).HandleAsync(new CreateRecordRequest(a.Collection.Id, Title: "T"), Ct);

        Assert.Equal("requires write", Assert.IsType<ForbiddenError>(denied.Error).Message);
        Assert.Equal("workspace_archived", Assert.IsType<ConflictError>(conflict.Error).Code);
    }

    [Fact]
    public async Task Describe_CountsRecordsThatAreNotArchived()
    {
        var a = await ArrangeAsync();
        await CreateRecordAsync(a, "Eins");
        var two = await CreateRecordAsync(a, "Zwei");
        Assert.True((await new ArchiveRecord(Db, TestActor.Agent(a.Seed.AlphaOwner.Id), a.Tenant, Activity, Clock).HandleAsync(new ArchiveRecordRequest(two.Id), Ct)).IsSuccess);

        var described = await new DescribeCollection(Db, a.Tenant).HandleAsync(new DescribeCollectionRequest(a.Collection.Id), Ct);

        Assert.Equal(1, described.Value.Statistics.RecordCount);
    }
}
