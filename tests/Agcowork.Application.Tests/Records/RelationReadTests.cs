using System.Data.Common;
using Agcowork.Application.Collections;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Agcowork.Application.Schema;
using Agcowork.Application.Spaces;
using Agcowork.Application.Tenancy;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Agcowork.Application.Tests.Records;

/// <summary>
/// Relations read back (AP-037): titles of many records in one query, backlinks grouped by source collection and property
/// (several collections, self-relations, scalar and array values, pagination, archived sources), expandRelations and
/// includeBacklinks on the record reads, and archived collections as relation targets.
/// </summary>
public sealed class RelationReadTests(PostgresFixture postgres) : RecordTestBase(postgres)
{
    /// <summary>
    /// Alpha with "Entscheidungen" (self-relation <c>supersedes</c>, single), "Aufgaben" (<c>decisions</c>, multiple) and
    /// "Komponenten" (<c>decided_in</c>, single, later switched to multiple).
    /// </summary>
    private sealed record Graph(Arranged A, Guid Decisions, Guid Tasks, Guid Components);

    private async Task<Graph> GraphAsync()
    {
        var a = await ArrangeAsync();
        var owner = a.Seed.AlphaOwner;
        var decisions = await CreateCollectionAsync(a.Tenant, owner, a.SpaceId, "Entscheidungen");
        await AddRelationAsync(a, decisions.Id, "supersedes", decisions.Id, multiple: false);
        await AddRelationAsync(a, a.Collection.Id, "decisions", decisions.Id, multiple: true);
        var components = await CreateCollectionAsync(a.Tenant, owner, a.SpaceId, "Komponenten");
        await AddRelationAsync(a, components.Id, "decided_in", decisions.Id, multiple: false);
        return new Graph(a, decisions.Id, a.Collection.Id, components.Id);
    }

    private async Task AddRelationAsync(Arranged a, Guid collectionId, string key, Guid target, bool multiple)
    {
        Db.ChangeTracker.Clear();
        var result = await new UpdateSchema(Db, a.Tenant, [], Activity).HandleAsync(
            new UpdateSchemaRequest(collectionId, Add: [new PropertyDefinitionInput(key, $"Name {key}", "relation", TargetCollectionId: target, Multiple: multiple)]), Ct);
        Assert.True(result.IsSuccess, Describe(result.Error));
        Db.ChangeTracker.Clear();
    }

    private async Task<Guid> RecordInAsync(Arranged a, Guid collectionId, string title, string properties = "{}")
    {
        Clock.Advance(TimeSpan.FromSeconds(1));
        var result = await TryCreateAsync(a, new CreateRecordRequest(collectionId, Title: title, Properties: Props(properties)));
        Assert.True(result.IsSuccess, Describe(result.Error));
        Db.ChangeTracker.Clear();
        return result.Value.Id;
    }

    private GetBacklinks Backlinks(ITenantContext tenant) => new(Db, tenant, RecordTranslator);

    private async Task<RecordBacklinksDto> BacklinksAsync(ITenantContext tenant, Guid recordId, int? limit = null, string? cursor = null, bool includeArchived = false)
    {
        var result = await Backlinks(tenant).HandleAsync(new GetBacklinksRequest(recordId, limit, cursor, includeArchived), Ct);
        Assert.True(result.IsSuccess, Describe(result.Error));
        return result.Value;
    }

    private async Task ArchiveAsync(Arranged a, Guid recordId)
    {
        Db.ChangeTracker.Clear();
        Assert.True((await new ArchiveRecord(Db, TestActor.Agent(a.Seed.AlphaOwner.Id), a.Tenant, Activity, Clock).HandleAsync(new ArchiveRecordRequest(recordId), Ct)).IsSuccess);
        Db.ChangeTracker.Clear();
    }

    // ---- Lookup -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Lookup_RequestOrder_DuplicatesOnce_UnknownAndForeignMissing_ArchivedMarked()
    {
        var g = await GraphAsync();
        var a = g.A;
        var d1 = await RecordInAsync(a, g.Decisions, "Postgres");
        var d2 = await RecordInAsync(a, g.Decisions, "Archiviert");
        var c1 = await RecordInAsync(a, g.Components, "In archivierter Collection");
        var free = await TryCreateAsync(a, new CreateRecordRequest(SpaceId: a.SpaceId, Title: "Freie Seite"));
        await ArchiveAsync(a, d2);
        Db.ChangeTracker.Clear();
        Assert.True((await new ArchiveCollection(Db, a.Tenant, Clock, Activity).HandleAsync(new ArchiveCollectionRequest(g.Components), Ct)).IsSuccess);

        // A record of Beta, created in Beta's own tenant.
        var betaTenant = Enter(a.Seed.Beta, a.Seed.BetaOwner);
        var betaSpace = await CreateSpaceAsync(betaTenant, a.Seed.BetaOwner, "Beta");
        var betaCollection = await CreateCollectionAsync(betaTenant, a.Seed.BetaOwner, betaSpace.Id, "Geheim");
        Db.ChangeTracker.Clear();
        var beta = await new CreateRecord(Db, TestActor.Agent(a.Seed.BetaOwner.Id), betaTenant, Validator(), Activity, Clock)
            .HandleAsync(new CreateRecordRequest(betaCollection.Id, Title: "Beta intern"), Ct);
        Assert.True(beta.IsSuccess);
        Tenant.Current = a.Tenant;
        Db.ChangeTracker.Clear();

        var unknown = Guid.CreateVersion7();
        var result = await new LookupRecords(Db, a.Tenant).HandleAsync(new LookupRecordsRequest([c1, unknown, d2, d1, beta.Value.Id, d1, free.Value.Id]), Ct);

        Assert.True(result.IsSuccess, Describe(result.Error));
        Assert.Equal(
            [(c1, "In archivierter Collection", (Guid?)g.Components, true), (d2, "Archiviert", g.Decisions, true), (d1, "Postgres", g.Decisions, false), (free.Value.Id, "Freie Seite", null, false)],
            result.Value.Items.Select(i => (i.Id, i.Title, i.CollectionId, i.Archived)));
        Assert.All(result.Value.Items, i => Assert.Equal(a.SpaceId, i.SpaceId));
    }

    [Fact]
    public async Task Lookup_ArchivedSpace_MarksRecords()
    {
        var g = await GraphAsync();
        var d1 = await RecordInAsync(g.A, g.Decisions, "D");
        Db.ChangeTracker.Clear();
        Assert.True((await new ArchiveSpace(Db, g.A.Tenant, Clock, Activity).HandleAsync(new ArchiveSpaceRequest(g.A.SpaceId), Ct)).IsSuccess);

        var result = await new LookupRecords(Db, g.A.Tenant).HandleAsync(new LookupRecordsRequest([d1]), Ct);

        Assert.True(Assert.Single(result.Value.Items).Archived);
    }

    [Fact]
    public async Task Lookup_InvalidCounts_ValidationError()
    {
        var a = await ArrangeAsync();
        var lookup = new LookupRecords(Db, a.Tenant);

        var tooMany = await lookup.HandleAsync(new LookupRecordsRequest([.. Enumerable.Range(0, LookupRecords.MaxIds + 1).Select(_ => Guid.CreateVersion7())]), Ct);
        var empty = await lookup.HandleAsync(new LookupRecordsRequest([]), Ct);
        var missing = await lookup.HandleAsync(new LookupRecordsRequest(null), Ct);
        var max = await lookup.HandleAsync(new LookupRecordsRequest([.. Enumerable.Range(0, LookupRecords.MaxIds).Select(_ => Guid.CreateVersion7())]), Ct);

        Assert.Equal("too_long", Field(Invalid(tooMany), "ids").Code);
        Assert.Equal("required", Field(Invalid(empty), "ids").Code);
        Assert.Equal("required", Field(Invalid(missing), "ids").Code);
        Assert.True(max.IsSuccess);
        Assert.Empty(max.Value.Items);
    }

    [Fact]
    public async Task Lookup_OneDatabaseCommand()
    {
        var g = await GraphAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await RecordInAsync(g.A, g.Decisions, $"D{i}"));
        }

        var counter = new CommandCounter();
        await using var counted = new AgcoworkDbContext(
            new DbContextOptionsBuilder<AgcoworkDbContext>((DbContextOptions<AgcoworkDbContext>)Db.GetService<IDbContextOptions>())
                .AddInterceptors(counter)
                .Options,
            Tenant);

        var result = await new LookupRecords(counted, g.A.Tenant).HandleAsync(new LookupRecordsRequest(ids), Ct);

        Assert.Equal(5, result.Value.Items.Count);
        Assert.Equal(1, counter.Count);
    }

    // ---- Backlinks ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Backlinks_SeveralCollections_SelfRelation_ScalarAndArray_Grouped()
    {
        var g = await GraphAsync();
        var a = g.A;
        var target = await RecordInAsync(a, g.Decisions, "Ziel");
        var other = await RecordInAsync(a, g.Decisions, "Andere");
        var newer = await RecordInAsync(a, g.Decisions, "Nachfolger", $$"""{ "supersedes": "{{target}}" }""");
        await RecordInAsync(a, g.Decisions, "Ersetzt andere", $$"""{ "supersedes": "{{other}}" }""");
        var t1 = await RecordInAsync(a, g.Tasks, "Aufgabe 1", $$"""{ "decisions": ["{{target}}"] }""");
        var t2 = await RecordInAsync(a, g.Tasks, "Aufgabe 2", $$"""{ "decisions": ["{{other}}", "{{target}}"] }""");
        await RecordInAsync(a, g.Tasks, "Aufgabe 3", $$"""{ "decisions": ["{{other}}"] }""");
        var c1 = await RecordInAsync(a, g.Components, "Komponente", $$"""{ "decided_in": "{{target}}" }""");

        var result = await BacklinksAsync(a.Tenant, target);

        Assert.Equal(target, result.RecordId);
        Assert.Equal(
            [("Aufgaben", "decisions", "Name decisions"), ("Entscheidungen", "supersedes", "Name supersedes"), ("Komponenten", "decided_in", "Name decided_in")],
            result.Groups.Select(x => (x.CollectionName, x.PropertyKey, x.PropertyName)));
        Assert.Equal([g.Tasks, g.Decisions, g.Components], result.Groups.Select(x => x.CollectionId));
        Assert.Equal([t2, t1], result.Groups[0].Items.Select(i => i.Id));
        Assert.Equal(["Aufgabe 2", "Aufgabe 1"], result.Groups[0].Items.Select(i => i.Title));
        Assert.Equal([newer], result.Groups[1].Items.Select(i => i.Id));
        Assert.Equal([c1], result.Groups[2].Items.Select(i => i.Id));
        Assert.All(result.Groups, x => Assert.Null(x.NextCursor));
        Assert.All(result.Groups, x => Assert.Equal(a.SpaceId, x.SpaceId));
        Assert.All(result.Groups.SelectMany(x => x.Items), i => Assert.False(i.Archived));
    }

    [Fact]
    public async Task Backlinks_ScalarFromBeforeMultiple_StillFound()
    {
        var g = await GraphAsync();
        var a = g.A;
        var target = await RecordInAsync(a, g.Decisions, "Ziel");
        var before = await RecordInAsync(a, g.Components, "Vorher", $$"""{ "decided_in": "{{target}}" }""");
        Db.ChangeTracker.Clear();
        var switched = await new UpdateSchema(Db, a.Tenant, [], Activity).HandleAsync(
            new UpdateSchemaRequest(g.Components, Update: [new PropertyChangeInput("decided_in", Multiple: true)]), Ct);
        Assert.True(switched.IsSuccess, Describe(switched.Error));
        Db.ChangeTracker.Clear();
        var after = await RecordInAsync(a, g.Components, "Nachher", $$"""{ "decided_in": ["{{target}}"] }""");

        var result = await BacklinksAsync(a.Tenant, target);

        Assert.Equal([after, before], Assert.Single(result.Groups).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Backlinks_PaginatedPerGroup_NoDuplicates_CursorContinuesOneGroup()
    {
        var g = await GraphAsync();
        var a = g.A;
        var target = await RecordInAsync(a, g.Decisions, "Ziel");
        var tasks = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            tasks.Add(await RecordInAsync(a, g.Tasks, $"Aufgabe {i}", $$"""{ "decisions": ["{{target}}"] }"""));
        }

        var component = await RecordInAsync(a, g.Components, "Komponente", $$"""{ "decided_in": "{{target}}" }""");

        var first = await BacklinksAsync(a.Tenant, target, limit: 2);
        Assert.Equal(["decisions", "decided_in"], first.Groups.Select(x => x.PropertyKey));
        Assert.Equal([component], first.Groups[1].Items.Select(i => i.Id));
        Assert.Null(first.Groups[1].NextCursor);

        var listed = first.Groups[0].Items.Select(i => i.Id).ToList();
        var cursor = first.Groups[0].NextCursor;
        while (cursor is not null)
        {
            var next = await BacklinksAsync(a.Tenant, target, limit: 2, cursor: cursor);
            var group = Assert.Single(next.Groups);
            Assert.Equal("decisions", group.PropertyKey);
            listed.AddRange(group.Items.Select(i => i.Id));
            cursor = group.NextCursor;
        }

        tasks.Reverse();
        Assert.Equal(tasks, listed);
    }

    [Fact]
    public async Task Backlinks_ArchivedSources_OnlyWithIncludeArchived()
    {
        var g = await GraphAsync();
        var a = g.A;
        var target = await RecordInAsync(a, g.Decisions, "Ziel");
        var active = await RecordInAsync(a, g.Tasks, "Aktiv", $$"""{ "decisions": ["{{target}}"] }""");
        var archived = await RecordInAsync(a, g.Tasks, "Archiviert", $$"""{ "decisions": ["{{target}}"] }""");
        var inArchivedCollection = await RecordInAsync(a, g.Components, "Komponente", $$"""{ "decided_in": "{{target}}" }""");
        await ArchiveAsync(a, archived);
        Assert.True((await new ArchiveCollection(Db, a.Tenant, Clock, Activity).HandleAsync(new ArchiveCollectionRequest(g.Components), Ct)).IsSuccess);
        Db.ChangeTracker.Clear();

        var without = await BacklinksAsync(a.Tenant, target);
        var with = await BacklinksAsync(a.Tenant, target, includeArchived: true);

        Assert.Equal([active], Assert.Single(without.Groups).Items.Select(i => i.Id));
        Assert.Equal([(archived, true), (active, false)], with.Groups[0].Items.Select(i => (i.Id, i.Archived)));
        Assert.Equal([(inArchivedCollection, true)], with.Groups[1].Items.Select(i => (i.Id, i.Archived)));
    }

    [Fact]
    public async Task Backlinks_UnknownOrForeignRecord_NotFound_FreePage_Empty_BadCursor_Invalid()
    {
        var g = await GraphAsync();
        var a = g.A;
        var free = await TryCreateAsync(a, new CreateRecordRequest(SpaceId: a.SpaceId, Title: "Frei"));
        var target = await RecordInAsync(a, g.Decisions, "Ziel");
        await RecordInAsync(a, g.Tasks, "Aufgabe", $$"""{ "decisions": ["{{target}}"] }""");

        var unknown = await Backlinks(a.Tenant).HandleAsync(new GetBacklinksRequest(Guid.CreateVersion7()), Ct);
        var fromBeta = await Backlinks(TenancySeedTenant(a, beta: true)).HandleAsync(new GetBacklinksRequest(target), Ct);
        Tenant.Current = a.Tenant;
        var freePage = await BacklinksAsync(a.Tenant, free.Value.Id);
        var badCursor = await Backlinks(a.Tenant).HandleAsync(new GetBacklinksRequest(target, Cursor: "kaputt"), Ct);
        var badLimit = await Backlinks(a.Tenant).HandleAsync(new GetBacklinksRequest(target, Limit: 0), Ct);

        Assert.IsType<NotFoundError>(unknown.Error);
        Assert.IsType<NotFoundError>(fromBeta.Error);
        Assert.Empty(freePage.Groups);
        Assert.Equal("invalid_format", Field(Assert.IsType<ValidationError>(badCursor.Error), "cursor").Code);
        Assert.Equal("out_of_range", Field(Assert.IsType<ValidationError>(badLimit.Error), "limit").Code);
    }

    [Fact]
    public async Task Backlinks_ForeignWorkspaceReferences_NeverListed()
    {
        // Beta gets a collection whose relation is made to target Alpha's collection, and a record that holds Alpha's target id
        // (both written as the owner, past validation and the composite keys). Alpha's backlinks must not see them.
        var g = await GraphAsync();
        var a = g.A;
        var target = await RecordInAsync(a, g.Decisions, "Ziel");
        var betaTenant = Enter(a.Seed.Beta, a.Seed.BetaOwner);
        var betaSpace = await CreateSpaceAsync(betaTenant, a.Seed.BetaOwner, "Beta");
        var betaTargets = await CreateCollectionAsync(betaTenant, a.Seed.BetaOwner, betaSpace.Id, "Entscheidungen");
        var betaSources = await CreateCollectionAsync(betaTenant, a.Seed.BetaOwner, betaSpace.Id, "Aufgaben",
            new PropertyDefinitionInput("decisions", "Decisions", "relation", TargetCollectionId: betaTargets.Id, Multiple: true));
        Db.ChangeTracker.Clear();
        var betaRecord = await new CreateRecord(Db, TestActor.Agent(a.Seed.BetaOwner.Id), betaTenant, Validator(), Activity, Clock)
            .HandleAsync(new CreateRecordRequest(betaSources.Id, Title: "Beta"), Ct);
        Assert.True(betaRecord.IsSuccess, Describe(betaRecord.Error));
        await using (var owner = PostgresFixture.OpenOwner(Db))
        {
            await owner.Database.ExecuteSqlAsync(
                $"UPDATE records SET properties = jsonb_build_object('decisions', jsonb_build_array({target.ToString("D")}::text)) WHERE id = {betaRecord.Value.Id}", Ct);
            await owner.Database.ExecuteSqlAsync(
                $"UPDATE collections SET schema = replace(schema::text, {betaTargets.Id.ToString("D")}, {g.Decisions.ToString("D")})::jsonb WHERE id = {betaSources.Id}", Ct);
        }

        Tenant.Current = a.Tenant;
        Db.ChangeTracker.Clear();

        var result = await BacklinksAsync(a.Tenant, target);

        Assert.Empty(result.Groups);
    }

    [Fact]
    public async Task Backlinks_ManyGroups_AtMostFiftyPerCall_NextGroupsCursor()
    {
        // 120 relation properties of one collection all point to the target: with the graph's three, 123 groups in three calls.
        var g = await GraphAsync();
        var a = g.A;
        var target = await RecordInAsync(a, g.Decisions, "Ziel");
        var keys = Enumerable.Range(0, 120).Select(i => $"r{i:D3}").ToList();
        var many = await CreateCollectionAsync(a.Tenant, a.Seed.AlphaOwner, a.SpaceId, "Viele",
            [.. keys.Select(k => new PropertyDefinitionInput(k, k, "relation", TargetCollectionId: g.Decisions))]);
        var source = await RecordInAsync(a, many.Id, "Quelle", "{" + string.Join(", ", keys.Select(k => $"\"{k}\": \"{target}\"")) + "}");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var pages = new List<RecordBacklinksDto>();
        string? cursor = null;
        do
        {
            var page = await BacklinksAsync(a.Tenant, target, cursor: cursor);
            pages.Add(page);
            cursor = page.NextGroupsCursor;
        }
        while (cursor is not null && pages.Count < 10);

        watch.Stop();
        // 123 candidate groups (the graph's three relations to "Entscheidungen" have no backlinks and are left out): 50 per call.
        Assert.Equal([47, 50, 23], pages.Select(p => p.Groups.Count));
        Assert.Equal(keys, pages.SelectMany(p => p.Groups).Select(x => x.PropertyKey));
        Assert.All(pages.SelectMany(p => p.Groups), x => Assert.Equal([source], x.Items.Select(i => i.Id)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Three pages of 50 groups took {watch.Elapsed}.");

        var record = (await new GetRecord(Db, a.Tenant, RecordTranslator).HandleAsync(new GetRecordRequest(target, IncludeBacklinks: true), Ct)).Value;
        Assert.Equal(47, record.Backlinks!.Groups.Count);
        Assert.NotNull(record.Backlinks.NextGroupsCursor);
        var markdown = (await new GetRecordMarkdown(Db, a.Tenant, RecordTranslator).HandleAsync(new GetRecordMarkdownRequest(target, IncludeBacklinks: true), Ct)).Value;
        Assert.Contains("Only the first 50 relation properties pointing here were looked at; more exist.", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backlinks_CursorOfOtherRecordOrSetting_OrVanishedGroup_InvalidCursor()
    {
        var g = await GraphAsync();
        var a = g.A;
        var target = await RecordInAsync(a, g.Decisions, "Ziel");
        var other = await RecordInAsync(a, g.Decisions, "Andere");
        for (var i = 0; i < 3; i++)
        {
            await RecordInAsync(a, g.Tasks, $"Aufgabe {i}", $$"""{ "decisions": ["{{target}}", "{{other}}"] }""");
        }

        var cursor = (await BacklinksAsync(a.Tenant, target, limit: 1)).Groups[0].NextCursor!;
        var archivedCursor = (await BacklinksAsync(a.Tenant, target, limit: 1, includeArchived: true)).Groups[0].NextCursor!;

        var otherRecord = await Backlinks(a.Tenant).HandleAsync(new GetBacklinksRequest(other, Limit: 1, Cursor: cursor), Ct);
        var otherSetting = await Backlinks(a.Tenant).HandleAsync(new GetBacklinksRequest(target, Limit: 1, Cursor: cursor, IncludeArchived: true), Ct);
        var otherSetting2 = await Backlinks(a.Tenant).HandleAsync(new GetBacklinksRequest(target, Limit: 1, Cursor: archivedCursor), Ct);
        Assert.True((await Backlinks(a.Tenant).HandleAsync(new GetBacklinksRequest(target, Limit: 1, Cursor: cursor), Ct)).IsSuccess);

        Db.ChangeTracker.Clear();
        var removed = await new UpdateSchema(Db, a.Tenant, [], Activity).HandleAsync(
            new UpdateSchemaRequest(g.Tasks, Remove: ["decisions"], ConfirmRemoval: true), Ct);
        Assert.True(removed.IsSuccess, Describe(removed.Error));
        Db.ChangeTracker.Clear();
        var vanished = await Backlinks(a.Tenant).HandleAsync(new GetBacklinksRequest(target, Limit: 1, Cursor: cursor), Ct);

        foreach (var result in new[] { otherRecord, otherSetting, otherSetting2, vanished })
        {
            Assert.Equal("invalid_format", Field(Assert.IsType<ValidationError>(result.Error), "cursor").Code);
        }

        Assert.Contains("does not belong to this record or includeArchived setting", Field(Invalid(otherRecord), "cursor").Message, StringComparison.Ordinal);
        Assert.Contains("no longer exists", Field(Invalid(vanished), "cursor").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backlinks_GroupWithMore_NamesRecordsQuery()
    {
        var g = await GraphAsync();
        var a = g.A;
        var target = await RecordInAsync(a, g.Decisions, "Ziel");
        await RecordInAsync(a, g.Tasks, "Aufgabe 1", $$"""{ "decisions": ["{{target}}"] }""");
        await RecordInAsync(a, g.Tasks, "Aufgabe 2", $$"""{ "decisions": ["{{target}}"] }""");
        await RecordInAsync(a, g.Components, "Komponente", $$"""{ "decided_in": "{{target}}" }""");

        var page = await BacklinksAsync(a.Tenant, target, limit: 1);

        Assert.Equal(new BacklinkQueryDto(g.Tasks, new BacklinkFilterDto("decisions", "contains", target)), page.Groups[0].More);
        Assert.Null(page.Groups[1].More);
        Assert.Null(page.Groups[1].NextCursor);
    }

    [Fact]
    public async Task RelationSources_OnlyCollectionsWithRelationToTarget()
    {
        var g = await GraphAsync();
        var a = g.A;
        await CreateCollectionAsync(a.Tenant, a.Seed.AlphaOwner, a.SpaceId, "Anderes Ziel",
            new PropertyDefinitionInput("ref", "Ref", "relation", TargetCollectionId: g.Tasks),
            new PropertyDefinitionInput("note", "Note", "text", Description: g.Decisions.ToString("D")));

        var sources = await RecordTranslator.RelationSources(Db.Collections.AsNoTracking(), g.Decisions).Select(c => c.Id).ToListAsync(Ct);

        Assert.Equal(new[] { g.Decisions, g.Tasks, g.Components }.Order(), sources.Order());
    }

    private TenantContext TenancySeedTenant(Arranged a, bool beta) =>
        beta ? Enter(a.Seed.Beta, a.Seed.BetaOwner) : Enter(a.Seed.Alpha, a.Seed.AlphaOwner);

    // ---- Record reads: expandRelations / includeBacklinks ----------------------------------------------------------------

    [Fact]
    public async Task GetRecord_ExpandRelations_TitlesInStoredOrder_IncludeBacklinks()
    {
        var g = await GraphAsync();
        var a = g.A;
        var d1 = await RecordInAsync(a, g.Decisions, "Postgres");
        var d2 = await RecordInAsync(a, g.Decisions, "Archiviert");
        await ArchiveAsync(a, d2);
        var task = await RecordInAsync(a, g.Tasks, "Aufgabe", $$"""{ "decisions": ["{{d2}}", "{{d1}}"] }""");
        var superseding = await RecordInAsync(a, g.Decisions, "Neu", $$"""{ "supersedes": "{{d1}}" }""");
        var get = new GetRecord(Db, a.Tenant, RecordTranslator);

        var plain = (await get.HandleAsync(new GetRecordRequest(task), Ct)).Value;
        var expanded = (await get.HandleAsync(new GetRecordRequest(task, ExpandRelations: true), Ct)).Value;
        var single = (await get.HandleAsync(new GetRecordRequest(superseding, ExpandRelations: true), Ct)).Value;
        var withBacklinks = (await get.HandleAsync(new GetRecordRequest(d1, IncludeBacklinks: true), Ct)).Value;

        Assert.Null(plain.Relations);
        Assert.Null(plain.Backlinks);
        var decisions = expanded.Relations!["decisions"];
        Assert.Equal([(d2, "Archiviert", true), (d1, "Postgres", false)], decisions.Select(t => (t.Id, t.Title, t.Archived)));
        Assert.Equal([(d1, "Postgres")], single.Relations!["supersedes"].Select(t => (t.Id, t.Title!)));
        Assert.Equal(["decisions", "supersedes"], withBacklinks.Backlinks!.Groups.Select(x => x.PropertyKey));
        Assert.Equal([task], withBacklinks.Backlinks.Groups[0].Items.Select(i => i.Id));
        Assert.Equal([superseding], withBacklinks.Backlinks.Groups[1].Items.Select(i => i.Id));
    }

    [Fact]
    public async Task GetRecord_IncludeBacklinks_FirstTenPerGroup_WithCursor()
    {
        var g = await GraphAsync();
        var a = g.A;
        var target = await RecordInAsync(a, g.Decisions, "Ziel");
        for (var i = 0; i < 12; i++)
        {
            await RecordInAsync(a, g.Tasks, $"Aufgabe {i}", $$"""{ "decisions": ["{{target}}"] }""");
        }

        var record = (await new GetRecord(Db, a.Tenant, RecordTranslator).HandleAsync(new GetRecordRequest(target, IncludeBacklinks: true), Ct)).Value;

        var group = Assert.Single(record.Backlinks!.Groups);
        Assert.Equal(10, group.Items.Count);
        Assert.Equal("Aufgabe 11", group.Items[0].Title);
        var rest = await BacklinksAsync(a.Tenant, target, cursor: group.NextCursor);
        Assert.Equal(["Aufgabe 1", "Aufgabe 0"], Assert.Single(rest.Groups).Items.Select(i => i.Title));
    }

    [Fact]
    public async Task GetRecordMarkdown_ExpandRelations_TitleComments_BacklinksSection()
    {
        var g = await GraphAsync();
        var a = g.A;
        var d1 = await RecordInAsync(a, g.Decisions, "Postgres: ja\nstatus: gekapert");
        var d2 = await RecordInAsync(a, g.Decisions, "Alt");
        await ArchiveAsync(a, d2);
        var task = await RecordInAsync(a, g.Tasks, "Aufgabe", $$"""{ "decisions": ["{{d1}}", "{{d2}}"] }""");
        var component = await RecordInAsync(a, g.Components, "Komponente", $$"""{ "decided_in": "{{d1}}" }""");
        var markdown = new GetRecordMarkdown(Db, a.Tenant, RecordTranslator);

        var plain = (await markdown.HandleAsync(new GetRecordMarkdownRequest(task), Ct)).Value;
        var expanded = (await markdown.HandleAsync(new GetRecordMarkdownRequest(component, ExpandRelations: true), Ct)).Value;
        var list = (await markdown.HandleAsync(new GetRecordMarkdownRequest(task, ExpandRelations: true), Ct)).Value;
        var backlinks = (await markdown.RenderAsync(new GetRecordMarkdownRequest(d1, IncludeBacklinks: true, IncludeComments: true), Ct)).Value;

        Assert.DoesNotContain(" # ", plain, StringComparison.Ordinal);
        Assert.Contains($"decided_in: {d1} # \"Postgres: ja\\nstatus: gekapert\"\n", expanded, StringComparison.Ordinal);
        Assert.Contains($"decisions:\n  - {d1} # \"Postgres: ja\\nstatus: gekapert\"\n  - {d2} # \"Alt\" (archived)\n", list, StringComparison.Ordinal);
        Assert.DoesNotContain("\nstatus: gekapert", expanded, StringComparison.Ordinal);

        var text = backlinks.Markdown;
        var section = text.IndexOf("\n---\n\n# Backlinks\n", StringComparison.Ordinal);
        Assert.True(section > 0, text);
        Assert.True(section < text.IndexOf("# Comments", StringComparison.Ordinal), "Backlinks come before the comments.");
        Assert.Contains($"- collection \"Aufgaben\" (id {g.Tasks}), property decisions (\"Name decisions\"):\n  - {task} \"Aufgabe\"\n", text, StringComparison.Ordinal);
        Assert.Contains($"- collection \"Komponenten\" (id {g.Components}), property decided_in (\"Name decided_in\"):\n  - {component} \"Komponente\"\n", text, StringComparison.Ordinal);
        Assert.Equal(["decisions", "decided_in"], backlinks.Backlinks!.Groups.Select(x => x.PropertyKey));
        Assert.Null(backlinks.Relations);
    }

    [Theory]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\u0085")]
    public async Task GetRecordMarkdown_ExpandRelations_LineSeparatorsInTitle_Escaped(string separator)
    {
        var g = await GraphAsync();
        var a = g.A;
        var target = await RecordInAsync(a, g.Decisions, $"Vorne{separator}status: gekapert");
        var component = await RecordInAsync(a, g.Components, "Komponente", $$"""{ "decided_in": "{{target}}" }""");

        var markdown = (await new GetRecordMarkdown(Db, a.Tenant, RecordTranslator).HandleAsync(new GetRecordMarkdownRequest(component, ExpandRelations: true), Ct)).Value;

        var escaped = $"\\u{(int)separator[0]:x4}";
        Assert.Contains($"decided_in: {target} # \"Vorne{escaped}status: gekapert\"\n", markdown, StringComparison.OrdinalIgnoreCase);
        var frontmatter = markdown[..markdown.IndexOf("\n---\n", 4, StringComparison.Ordinal)];
        Assert.DoesNotContain(separator, frontmatter[frontmatter.IndexOf("decided_in", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRecordMarkdown_IncludeBacklinks_MoreHint_And_NoneText()
    {
        var g = await GraphAsync();
        var a = g.A;
        var target = await RecordInAsync(a, g.Decisions, "Ziel");
        var lonely = await RecordInAsync(a, g.Decisions, "Allein");
        for (var i = 0; i < 11; i++)
        {
            await RecordInAsync(a, g.Tasks, $"Aufgabe {i}", $$"""{ "decisions": ["{{target}}"] }""");
        }

        var markdown = new GetRecordMarkdown(Db, a.Tenant, RecordTranslator);
        var many = (await markdown.HandleAsync(new GetRecordMarkdownRequest(target, IncludeBacklinks: true), Ct)).Value;
        var none = (await markdown.HandleAsync(new GetRecordMarkdownRequest(lonely, IncludeBacklinks: true), Ct)).Value;

        Assert.Contains(
            $"  - more: records_query with collectionId {g.Tasks} and filter {{ \"prop\": \"decisions\", \"op\": \"contains\", \"value\": \"{target}\" }}\n",
            many,
            StringComparison.Ordinal);
        Assert.EndsWith("# Backlinks\n\nNo active records reference this record.\n", none, StringComparison.Ordinal);
    }

    // ---- Archived collections as relation targets ----------------------------------------------------------------------

    [Fact]
    public async Task NewRelation_ToArchivedCollection_TargetArchived_ExistingRelationsStay()
    {
        var g = await GraphAsync();
        var a = g.A;
        var d1 = await RecordInAsync(a, g.Decisions, "Ziel");
        var task = await RecordInAsync(a, g.Tasks, "Aufgabe", $$"""{ "decisions": ["{{d1}}"] }""");
        Db.ChangeTracker.Clear();
        Assert.True((await new ArchiveCollection(Db, a.Tenant, Clock, Activity).HandleAsync(new ArchiveCollectionRequest(g.Decisions), Ct)).IsSuccess);
        Db.ChangeTracker.Clear();

        var created = await TryCreateCollectionAsync(a.Tenant, a.Seed.AlphaOwner, a.SpaceId, "Neu",
            [new PropertyDefinitionInput("ref", "Ref", "relation", TargetCollectionId: g.Decisions)]);
        Db.ChangeTracker.Clear();
        var added = await new UpdateSchema(Db, a.Tenant, [], Activity).HandleAsync(
            new UpdateSchemaRequest(g.Tasks, Add: [new PropertyDefinitionInput("other", "Other", "relation", TargetCollectionId: g.Decisions)]), Ct);
        Db.ChangeTracker.Clear();

        Assert.Equal("target_archived", Field(Invalid(created), "schema[0].targetCollectionId").Code);
        Assert.Equal("target_archived", Field(Assert.IsType<ValidationError>(added.Error), "add[0].targetCollectionId").Code);

        // The existing relation keeps working: other schema changes, writing and reading its values.
        var unrelated = await new UpdateSchema(Db, a.Tenant, [], Activity).HandleAsync(
            new UpdateSchemaRequest(g.Tasks, Add: [new PropertyDefinitionInput("note", "Note", "text")]), Ct);
        Assert.True(unrelated.IsSuccess, Describe(unrelated.Error));
        Db.ChangeTracker.Clear();
        var rewritten = await UpdateAsync(a, new UpdateRecordRequest(task, Properties: Props($$"""{ "decisions": ["{{d1}}"], "note": "x" }""")));
        Assert.True(rewritten.IsSuccess, Describe(rewritten.Error));
        var expanded = (await new GetRecord(Db, a.Tenant, RecordTranslator).HandleAsync(new GetRecordRequest(task, ExpandRelations: true), Ct)).Value;
        Assert.Equal([(d1, "Ziel", true)], expanded.Relations!["decisions"].Select(t => (t.Id, t.Title!, t.Archived)));
    }

    [Fact]
    public async Task NewRelation_ToCollectionInArchivedSpace_TargetArchived()
    {
        var a = await ArrangeAsync();
        var otherSpace = await CreateSpaceAsync(a.Tenant, a.Seed.AlphaOwner, "Alt");
        var target = await CreateCollectionAsync(a.Tenant, a.Seed.AlphaOwner, otherSpace.Id, "Ziel");
        Db.ChangeTracker.Clear();
        Assert.True((await new ArchiveSpace(Db, a.Tenant, Clock, Activity).HandleAsync(new ArchiveSpaceRequest(otherSpace.Id), Ct)).IsSuccess);
        Db.ChangeTracker.Clear();

        var created = await TryCreateCollectionAsync(a.Tenant, a.Seed.AlphaOwner, a.SpaceId, "Neu",
            [new PropertyDefinitionInput("ref", "Ref", "relation", TargetCollectionId: target.Id)]);

        Assert.Equal("target_archived", Field(Invalid(created), "schema[0].targetCollectionId").Code);
    }

    /// <summary>Counts the commands EF executes (the session settings of row-level security run past this pipeline).</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
