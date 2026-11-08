using System.Text;
using Agcowork.Application.Collections;
using Agcowork.Application.Collections.Templates;
using Agcowork.Application.Common.Paging;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Agcowork.Application.Schema;
using Agcowork.Application.Search;
using Agcowork.Application.Tests.Records;
using DomainRecord = Agcowork.Domain.Records.Record;
using Agcowork.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Agcowork.Application.Tests.Search;

/// <summary>Full-text search (AP-026) against PostgreSQL: index contents, ranking, syntax, filters, snippets, paging, limits.</summary>
public sealed class SearchRecordsTests(PostgresFixture postgres) : RecordTestBase(postgres)
{
    private async Task<(Arranged A, CollectionDto Notes)> ArrangeSearchAsync()
    {
        var a = await ArrangeAsync();
        var notes = await CreateCollectionAsync(a.Tenant, a.Seed.AlphaOwner, a.SpaceId, "Notizen",
            new PropertyDefinitionInput("summary", "Summary", "text"),
            new PropertyDefinitionInput("sources", "Sources", "markdown"),
            new PropertyDefinitionInput("site", "Site", "url"),
            new PropertyDefinitionInput("mail", "Mail", "email"),
            new PropertyDefinitionInput("kind", "Kind", "select", Options: [new("tech", "Technikanalyse"), new("market", "Marktanalyse")]),
            new PropertyDefinitionInput("labels", "Labels", "multi_select", Options: [new("red", "Dringlich"), new("blue", "Später")]),
            new PropertyDefinitionInput("phase", "Phase", "status", Options: [new("open", "Offen", Group: "todo"), new("closed", "Erledigt", Group: "done")]),
            new PropertyDefinitionInput("score", "Score", "number"),
            new PropertyDefinitionInput("owner", "Owner", "actor"),
            new PropertyDefinitionInput("payload", "Payload", "json"),
            new PropertyDefinitionInput("due", "Due", "date"));
        return (a, notes);
    }

    private SearchRecords UseCase(Arranged a) => new(Db, a.Tenant, new RecordSearchIndex(Db));

    private async Task<Result<Page<SearchHitDto>>> TrySearchAsync(Arranged a, SearchRecordsRequest request)
    {
        Db.ChangeTracker.Clear();
        return await UseCase(a).HandleAsync(request, Ct);
    }

    private async Task<Page<SearchHitDto>> SearchAsync(Arranged a, string q, Guid? spaceId = null, Guid? collectionId = null, bool includeArchived = false, int? limit = null, string? cursor = null)
    {
        var result = await TrySearchAsync(a, new SearchRecordsRequest(q, spaceId, collectionId, includeArchived, limit, cursor));
        Assert.True(result.IsSuccess, Describe(result.Error));
        return result.Value;
    }

    private async Task<RecordDto> CreateInAsync(Arranged a, Guid collectionId, string title, string properties = "{}", string? body = null)
    {
        var result = await TryCreateAsync(a, new CreateRecordRequest(collectionId, Title: title, Properties: Props(properties), Body: body));
        Assert.True(result.IsSuccess, Describe(result.Error));
        Db.ChangeTracker.Clear();
        return result.Value;
    }

    private async Task<IReadOnlyList<Guid>> IdsAsync(Arranged a, string q, bool includeArchived = false) =>
        [.. (await SearchAsync(a, q, includeArchived: includeArchived, limit: RecordSearch.MaxLimit)).Items.Select(h => h.RecordId)];

    [Fact]
    public async Task Search_TitleHit_RanksBeforeBodyOnlyHit_EvenWithManyBodyOccurrences()
    {
        var (a, notes) = await ArrangeSearchAsync();
        var body = await CreateInAsync(a, notes.Id, "Messaging", body: string.Join(' ', Enumerable.Repeat("Kafka ist schnell.", 40)));
        var title = await CreateInAsync(a, notes.Id, "Kafka Einführung", body: "Nichts weiter.");

        var page = await SearchAsync(a, "kafka");

        Assert.Equal([title.Id, body.Id], page.Items.Select(h => h.RecordId));
        Assert.True(page.Items[0].Rank >= 1f, $"title rank {page.Items[0].Rank}");
        Assert.InRange(page.Items[1].Rank, 0f, 0.9999f);
        Assert.Equal("Kafka Einführung", page.Items[0].Title);
        Assert.Equal(notes.Id, page.Items[0].CollectionId);
        Assert.Equal(a.SpaceId, page.Items[0].SpaceId);
        Assert.False(page.Items[0].Archived);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Search_PrefixPhraseExcludeOr_FollowWebSearchSyntax()
    {
        var (a, notes) = await ArrangeSearchAsync();
        var decision = await CreateInAsync(a, notes.Id, "Entscheidung über Persistenz");
        var es = await CreateInAsync(a, notes.Id, "Architektur", body: "Wir nutzen Event Sourcing für den Verlauf.");
        var esKafka = await CreateInAsync(a, notes.Id, "Streaming", body: "Event Sourcing auf Kafka.");
        var reversed = await CreateInAsync(a, notes.Id, "Durcheinander", body: "Sourcing ohne ein Event.");
        var mongo = await CreateInAsync(a, notes.Id, "MongoDB verworfen");

        Assert.Equal([decision.Id], await IdsAsync(a, "entsch"));
        Assert.Equal([decision.Id], await IdsAsync(a, "ENTSCHEIDUNG"));
        Assert.Equal([es.Id], await IdsAsync(a, "\"event sourcing\" -kafka"));
        Assert.Equal(new[] { es.Id, esKafka.Id }.Order(), (await IdsAsync(a, "\"event sourcing\"")).Order());
        Assert.Equal(new[] { es.Id, esKafka.Id, reversed.Id }.Order(), (await IdsAsync(a, "event sourcing")).Order());
        Assert.Equal(new[] { decision.Id, mongo.Id }.Order(), (await IdsAsync(a, "persistenz or mongodb")).Order());

        // Quoted or excluded last terms are no prefixes: -kaf does not exclude "Kafka", -ein excludes "ein".
        Assert.Empty(await IdsAsync(a, "\"entsch\""));
        Assert.Equal(new[] { es.Id, esKafka.Id }.Order(), (await IdsAsync(a, "sourcing -kaf -ein")).Order());
    }

    [Fact]
    public void PrefixLastTerm_OnlyForAPlainTrailingWord()
    {
        Assert.True(SearchRecords.PrefixLastTerm("entsch"));
        Assert.True(SearchRecords.PrefixLastTerm("\"event sourcing\" kaf"));
        Assert.False(SearchRecords.PrefixLastTerm("\"event sourcing\""));
        Assert.False(SearchRecords.PrefixLastTerm("\"event sourc"));
        Assert.False(SearchRecords.PrefixLastTerm("event -kafka"));
        Assert.False(SearchRecords.PrefixLastTerm("event or"));
        Assert.False(SearchRecords.PrefixLastTerm("event."));
        Assert.False(SearchRecords.PrefixLastTerm("a"));
        Assert.False(SearchRecords.PrefixLastTerm("event a"));
        Assert.True(SearchRecords.PrefixLastTerm("event ab"));
    }

    [Fact]
    public async Task Search_IndexesTextLikePropertiesAndOptionLabels_NotIdsNumbersOrJson()
    {
        var (a, notes) = await ArrangeSearchAsync();
        var actor = a.Seed.AlphaOwner.Id;
        var record = await CreateInAsync(a, notes.Id, "Bericht", $$"""
            {
              "summary": "Zusammenfassung Quartal",
              "sources": "**Quellenangabe** im Text",
              "site": "https://beispielseite.example/pfad",
              "mail": "kontakt@firmenname.example",
              "kind": "tech",
              "labels": ["red"],
              "phase": "closed",
              "score": 4711,
              "owner": "{{actor}}",
              "payload": { "secret": "nutzlastwort" },
              "due": "2026-12-24"
            }
            """);
        var other = await CreateInAsync(a, notes.Id, "Anderes", """{ "kind": "market", "labels": ["blue"] }""");

        foreach (var q in new[] { "zusammenfassung", "quellenangabe", "beispielseite", "kontakt", "technikanalyse", "dringlich", "erledigt", "tech", "closed" })
        {
            Assert.True((await IdsAsync(a, q)).SequenceEqual([record.Id]), q);
        }

        Assert.Equal([other.Id], await IdsAsync(a, "marktanalyse"));
        Assert.Equal([other.Id], await IdsAsync(a, "später"));
        foreach (var q in new[] { "4711", "nutzlastwort", "secret", actor.ToString(), actor.ToString("N")[..8], "2026" })
        {
            Assert.True((await IdsAsync(a, q)).Count == 0, q);
        }

        var hit = (await SearchAsync(a, "quartal")).Items.Single();
        Assert.Contains(RecordSearch.HighlightStart + "Quartal" + RecordSearch.HighlightEnd, hit.Snippet, StringComparison.Ordinal);
        Assert.True(hit.Rank < 1f);
    }

    [Fact]
    public async Task Search_FreePages_AreFound()
    {
        var (a, _) = await ArrangeSearchAsync();
        var page = await TryCreateAsync(a, new CreateRecordRequest(SpaceId: a.SpaceId, Title: "Protokoll Montag", Body: "Notiz zur Retrospektive"));
        Assert.True(page.IsSuccess, Describe(page.Error));

        var hit = (await SearchAsync(a, "retrospektive")).Items.Single();
        Assert.Equal(page.Value.Id, hit.RecordId);
        Assert.Null(hit.CollectionId);
    }

    [Fact]
    public async Task Search_FiltersSpaceCollectionArchived()
    {
        var (a, notes) = await ArrangeSearchAsync();
        var otherSpace = await CreateSpaceAsync(a.Tenant, a.Seed.AlphaOwner, "Technik");
        var otherCollection = await CreateCollectionAsync(a.Tenant, a.Seed.AlphaOwner, otherSpace.Id, "Komponenten");
        var inNotes = await CreateInAsync(a, notes.Id, "Filterwort eins");
        var inTasks = await CreateRecordAsync(a, "Filterwort zwei");
        var inOther = await CreateInAsync(a, otherCollection.Id, "Filterwort drei");
        var archived = await CreateInAsync(a, notes.Id, "Filterwort archiviert");
        Db.ChangeTracker.Clear();
        Assert.True((await new ArchiveRecord(Db, TestActor.Agent(a.Seed.AlphaOwner.Id), a.Tenant, Activity, Clock).HandleAsync(new ArchiveRecordRequest(archived.Id), Ct)).IsSuccess);

        Assert.Equal(new[] { inNotes.Id, inTasks.Id, inOther.Id }.Order(), (await IdsAsync(a, "filterwort")).Order());
        Assert.Equal(new[] { inNotes.Id, inTasks.Id, inOther.Id, archived.Id }.Order(), (await IdsAsync(a, "filterwort", includeArchived: true)).Order());
        Assert.Equal(new[] { inNotes.Id, inTasks.Id }.Order(), (await SearchAsync(a, "filterwort", spaceId: a.SpaceId)).Items.Select(h => h.RecordId).Order());
        Assert.Equal([inOther.Id], (await SearchAsync(a, "filterwort", spaceId: otherSpace.Id)).Items.Select(h => h.RecordId));
        Assert.Equal([inNotes.Id], (await SearchAsync(a, "filterwort", collectionId: notes.Id)).Items.Select(h => h.RecordId));
        var withArchived = await SearchAsync(a, "filterwort", collectionId: notes.Id, includeArchived: true);
        Assert.Equal(new[] { inNotes.Id, archived.Id }.Order(), withArchived.Items.Select(h => h.RecordId).Order());
        Assert.True(withArchived.Items.Single(h => h.RecordId == archived.Id).Archived);
        Assert.Empty((await SearchAsync(a, "filterwort", spaceId: otherSpace.Id, collectionId: notes.Id)).Items);

        Assert.IsType<NotFoundError>((await TrySearchAsync(a, new SearchRecordsRequest("filterwort", SpaceId: Guid.NewGuid()))).Error);
        Assert.IsType<NotFoundError>((await TrySearchAsync(a, new SearchRecordsRequest("filterwort", CollectionId: Guid.NewGuid()))).Error);
    }

    [Fact]
    public async Task Search_Snippet_MarksOnlyMatches_ContentMarkupStaysText_MarkersOnlyFromServer()
    {
        var (a, notes) = await ArrangeSearchAsync();
        await CreateInAsync(a, notes.Id, "Sicherheit", body:
            "Einleitung <img src=x onerror=alert(1)> Zauberwort im Text, dazu ⟦falsch⟧ markiert und [eckig]. "
            + string.Join(' ', Enumerable.Repeat("Füllwort", 80)));

        var hit = (await SearchAsync(a, "zauberwort")).Items.Single();

        // Plain text: markup of the content can remain (clients render it as text); the markers only enclose the match.
        Assert.Contains("<img src=x onerror=alert(1)>", hit.Snippet, StringComparison.Ordinal);
        Assert.Equal(["Zauberwort"], Marked(hit.Snippet));
        Assert.DoesNotContain("⟦falsch⟧", hit.Snippet, StringComparison.Ordinal);
        Assert.True(hit.Snippet.Split(' ').Length < 70, hit.Snippet);

        // A title-only hit has a snippet without markers.
        var titleOnly = (await SearchAsync(a, "sicherheit")).Items.Single();
        Assert.Empty(Marked(titleOnly.Snippet));
    }

    [Fact]
    public async Task Search_MatchBeyondSnippetSource_FoundWithoutMarkers()
    {
        var (a, notes) = await ArrangeSearchAsync();
        var body = new StringBuilder();
        while (body.Length < RecordSearch.SnippetSourceLength + 100)
        {
            body.Append("lorem ipsum ");
        }

        var record = await CreateInAsync(a, notes.Id, "Lang", body: body.Append("Tiefwort").ToString());

        var hit = (await SearchAsync(a, "tiefwort")).Items.Single();
        Assert.Equal(record.Id, hit.RecordId);
        Assert.Empty(Marked(hit.Snippet));
    }

    [Fact]
    public async Task Search_MaximalBody_CreateAndUpdateSucceed_StartFound_OnlyPrefixIndexed()
    {
        var (a, notes) = await ArrangeSearchAsync();
        var filler = new StringBuilder("Anfangswort ");
        while (filler.Length < RecordSearch.BodyPrefixLength + 1000)
        {
            filler.Append("lorem ipsum ");
        }

        filler.Append("Spaetwort ");
        var body = Fill(filler.ToString(), "dolor sit ");
        var record = await CreateInAsync(a, notes.Id, "Groß", body: body);

        Assert.Equal([record.Id], await IdsAsync(a, "anfangswort"));
        Assert.Empty(await IdsAsync(a, "spaetwort"));

        var update = await UpdateAsync(a, new UpdateRecordRequest(record.Id, Body: Fill("Neuanfang ", "amet consectetur ")));
        Assert.True(update.IsSuccess, Describe(update.Error));
        Assert.Equal([record.Id], await IdsAsync(a, "neuanfang"));
        Assert.Empty(await IdsAsync(a, "anfangswort"));
    }

    [Fact]
    public async Task Search_MaximalBodyOfDistinctMultibyteWords_DoesNotHitTheTsvectorLimit()
    {
        // Worst case for the tsvector size: every word distinct and multi-byte (CJK, 3 bytes per character). The whole body
        // would exceed PostgreSQL's 1 MB limit; the indexed prefix does not.
        var (a, notes) = await ArrangeSearchAsync();
        var words = new StringBuilder("Startwort ", DomainRecord.BodyMaxBytes);
        var bytes = words.Length;
        for (var n = 0; bytes + 7 <= DomainRecord.BodyMaxBytes; n++)
        {
            // Two CJK characters (3 bytes each) and a space; the pair is distinct for every n.
            words.Append((char)(0x4E00 + (n % 20000))).Append((char)(0x4E00 + (n / 20000))).Append(' ');
            bytes += 7;
        }

        var body = words.ToString();
        Assert.True(DomainRecord.IsBodyWithinLimit(body));
        var record = await CreateInAsync(a, notes.Id, "Zeichen", body: body);
        var update = await UpdateAsync(a, new UpdateRecordRequest(record.Id, Body: "Zweitwort " + body[10..]));

        Assert.True(update.IsSuccess, Describe(update.Error));
        Assert.Equal([record.Id], await IdsAsync(a, "zweitwort"));
    }

    [Fact]
    public async Task Search_WorstCaseCompoundsInBodyAndMarkdownProperty_CreateAndUpdateSucceed()
    {
        // 4-byte letters in hyphenated compounds: about 8 bytes per character in the tsvector. With the old prefixes
        // (100000 body, 50000 properties) body and property together exceeded PostgreSQL's 1 MB limit.
        var (a, notes) = await ArrangeSearchAsync();
        var body = "Körperwort " + Compounds(DomainRecord.BodyMaxBytes - 20, fromEnd: false, utf8: true);
        var sources = "Quellwort " + Compounds(PropertyTypeRegistry.MarkdownMaxLength - 20, fromEnd: true, utf8: false);
        Assert.True(DomainRecord.IsBodyWithinLimit(body));

        var record = await TryCreateAsync(a, new CreateRecordRequest(notes.Id, Title: "Titelwort Komposita", Properties: new Dictionary<string, System.Text.Json.JsonElement> { ["sources"] = System.Text.Json.JsonSerializer.SerializeToElement(sources) }, Body: body));
        Assert.True(record.IsSuccess, Describe(record.Error));
        Assert.Equal([record.Value.Id], await IdsAsync(a, "titelwort"));
        Assert.Equal([record.Value.Id], await IdsAsync(a, "körperwort"));
        Assert.Equal([record.Value.Id], await IdsAsync(a, "quellwort"));

        var update = await UpdateAsync(a, new UpdateRecordRequest(record.Value.Id, Title: "Neutitelwort", Body: "Zweitkörper " + body["Körperwort ".Length..]));
        Assert.True(update.IsSuccess, Describe(update.Error));
        Assert.Equal([record.Value.Id], await IdsAsync(a, "zweitkörper"));
        Assert.Equal([record.Value.Id], await IdsAsync(a, "neutitelwort"));
    }

    [Fact]
    public async Task SearchVector_OverTheLimit_FallsBackToShorterPrefixesThenTitle()
    {
        await ArrangeAsync();
        var body = Compounds(DomainRecord.BodyMaxBytes - 20, fromEnd: false, utf8: true);
        const string Properties = "{}";

        // The whole 2 MiB body exceeds the limit: PostgreSQL raises program_limit_exceeded (54000), which the function catches.
        var failure = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Db.Database
            .SqlQuery<string>($"SELECT agcowork_record_search_vector_parts('Fallbackwort', {Properties}::jsonb, {body}, NULL, 1000000, 1000000)::text AS \"Value\"")
            .SingleAsync(Ct));
        Assert.Equal("54000", failure.SqlState);

        var vector = await Db.Database
            .SqlQuery<string>($"SELECT agcowork_record_search_vector('Fallbackwort', {Properties}::jsonb, {body}, NULL, 1000000, 1000000)::text AS \"Value\"")
            .SingleAsync(Ct);
        Assert.Contains("'fallbackwort':1A", vector, StringComparison.Ordinal);


        // The old prefixes (50000 properties, 100000 body) on the worst case of body and markdown property exceed the limit too;
        // the current defaults do not.
        var sources = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["sources"] = Compounds(PropertyTypeRegistry.MarkdownMaxLength - 20, fromEnd: true, utf8: false) });
        const string Schema = """{ "version": 1, "properties": [ { "key": "sources", "name": "Sources", "type": "markdown" } ] }""";
        var old = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Db.Database
            .SqlQuery<string>($"SELECT agcowork_record_search_vector_parts('Titel', {sources}::jsonb, {body}, {Schema}::jsonb, 50000, 100000)::text AS \"Value\"")
            .SingleAsync(Ct));
        Assert.Equal("54000", old.SqlState);
        var current = await Db.Database
            .SqlQuery<string>($"SELECT agcowork_record_search_vector_parts('Titel', {sources}::jsonb, {body}, {Schema}::jsonb, {RecordSearch.PropertiesPrefixLength}, {RecordSearch.BodyPrefixLength})::text AS \"Value\"")
            .SingleAsync(Ct);
        Assert.Contains("'titel':1A", current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_BackfillOfWorstCaseRecords_Succeeds()
    {
        var (a, notes) = await ArrangeSearchAsync();
        var record = await CreateInAsync(a, notes.Id, "Bestandswort");
        // Migrations run as the owner, like at startup.
        await using var owner = PostgresFixture.OpenOwner(Db);
        var migrator = owner.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20261107231617_AddActivityXactId", Ct);

        // Existing data before the search migration: worst-case body and markdown property.
        var body = "Altbestand " + Compounds(DomainRecord.BodyMaxBytes - 20, fromEnd: false, utf8: true);
        var properties = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["sources"] = Compounds(PropertyTypeRegistry.MarkdownMaxLength - 20, fromEnd: true, utf8: false) });
        await owner.Database.ExecuteSqlAsync($"UPDATE records SET body = {body}, properties = {properties}::jsonb WHERE id = {record.Id}", Ct);

        await migrator.MigrateAsync(cancellationToken: Ct);

        Assert.Equal([record.Id], await IdsAsync(a, "bestandswort"));
        Assert.Equal([record.Id], await IdsAsync(a, "altbestand"));
    }

    [Fact]
    public async Task SchemaChange_ReindexesOnlyRecordsWithAChangedKey()
    {
        var (a, notes) = await ArrangeSearchAsync();
        var withKind = await CreateInAsync(a, notes.Id, "Mit Art", """{ "kind": "tech" }""");
        var withoutKind = await CreateInAsync(a, notes.Id, "Ohne Art", """{ "summary": "Etwas" }""");
        var before = (await RowVersionAsync(withKind.Id), await RowVersionAsync(withoutKind.Id));

        await ChangeSchemaAsync(a, new UpdateSchemaRequest(notes.Id, Update: [new PropertyChangeInput("kind", Options: [new("tech", "Technologiestudie"), new("market", "Marktanalyse")])]));

        Assert.NotEqual(before.Item1, await RowVersionAsync(withKind.Id));
        Assert.Equal(before.Item2, await RowVersionAsync(withoutKind.Id));
        Assert.Equal([withKind.Id], await IdsAsync(a, "technologiestudie"));

        // A change that does not touch the indexed text (description) re-indexes nothing.
        var unchanged = await RowVersionAsync(withKind.Id);
        await ChangeSchemaAsync(a, new UpdateSchemaRequest(notes.Id, Update: [new PropertyChangeInput("kind", Description: "Art der Analyse")]));
        Assert.Equal(unchanged, await RowVersionAsync(withKind.Id));
    }

    [Fact]
    public async Task Search_RecordsOfArchivedCollectionOrSpace_CountAsArchived()
    {
        var (a, notes) = await ArrangeSearchAsync();
        var otherSpace = await CreateSpaceAsync(a.Tenant, a.Seed.AlphaOwner, "Archivraum");
        var inNotes = await CreateInAsync(a, notes.Id, "Hüllenwort Sammlung");
        var page = await TryCreateAsync(a, new CreateRecordRequest(SpaceId: otherSpace.Id, Title: "Hüllenwort Seite"));
        Assert.True(page.IsSuccess, Describe(page.Error));
        var active = await CreateRecordAsync(a, "Hüllenwort aktiv");

        Db.ChangeTracker.Clear();
        Assert.True((await new ArchiveCollection(Db, a.Tenant, Clock, Activity).HandleAsync(new ArchiveCollectionRequest(notes.Id), Ct)).IsSuccess);
        Db.ChangeTracker.Clear();
        Assert.True((await new Application.Spaces.ArchiveSpace(Db, a.Tenant, Clock, Activity).HandleAsync(new Application.Spaces.ArchiveSpaceRequest(otherSpace.Id), Ct)).IsSuccess);

        Assert.Equal([active.Id], await IdsAsync(a, "hüllenwort"));
        var all = (await SearchAsync(a, "hüllenwort", includeArchived: true, limit: 10)).Items.ToDictionary(h => h.RecordId);
        Assert.Equal(new[] { inNotes.Id, page.Value.Id, active.Id }.Order(), all.Keys.Order());
        Assert.True(all[inNotes.Id].Archived);
        Assert.True(all[page.Value.Id].Archived);
        Assert.False(all[active.Id].Archived);
    }

    [Fact]
    public async Task Search_AfterChanges_FindsNewContentNotOld()
    {
        var (a, notes) = await ArrangeSearchAsync();
        var record = await CreateInAsync(a, notes.Id, "Alttitel", """{ "summary": "Altsumme", "kind": "tech" }""", "Altkörper");

        var update = await UpdateAsync(a, new UpdateRecordRequest(record.Id, Title: "Neutitel", Properties: Props("""{ "summary": "Neusumme", "kind": "market" }"""), Body: "Neukörper"));
        Assert.True(update.IsSuccess, Describe(update.Error));

        foreach (var q in new[] { "alttitel", "altsumme", "altkörper", "technikanalyse" })
        {
            Assert.True((await IdsAsync(a, q)).Count == 0, q);
        }

        foreach (var q in new[] { "neutitel", "neusumme", "neukörper", "marktanalyse" })
        {
            Assert.True((await IdsAsync(a, q)).SequenceEqual([record.Id]), q);
        }

        Db.ChangeTracker.Clear();
        Assert.True((await AppendUseCase(a.Tenant, a.Seed.AlphaOwner).HandleAsync(new AppendToRecordRequest(record.Id, "Anhangwort"), Ct)).IsSuccess);
        Assert.Equal([record.Id], await IdsAsync(a, "anhangwort"));
    }

    [Fact]
    public async Task Search_AfterSchemaChange_LabelsAndRemovedPropertiesReindexed()
    {
        var (a, notes) = await ArrangeSearchAsync();
        var record = await CreateInAsync(a, notes.Id, "Schema", """{ "kind": "tech", "summary": "Entfernwort" }""");
        Assert.Equal([record.Id], await IdsAsync(a, "technikanalyse"));

        await ChangeSchemaAsync(a, new UpdateSchemaRequest(notes.Id, Update: [new PropertyChangeInput("kind", Options: [new("tech", "Technologiestudie"), new("market", "Marktanalyse")])]));
        Assert.Empty(await IdsAsync(a, "technikanalyse"));
        Assert.Equal([record.Id], await IdsAsync(a, "technologiestudie"));

        await ChangeSchemaAsync(a, new UpdateSchemaRequest(notes.Id, Remove: ["summary"], ConfirmRemoval: true));
        Assert.Empty(await IdsAsync(a, "entfernwort"));
        Assert.Equal([record.Id], await IdsAsync(a, "technologiestudie"));
    }

    [Fact]
    public async Task Search_Paging_AllHitsOnceInRankOrder()
    {
        var (a, notes) = await ArrangeSearchAsync();
        var expected = new List<Guid>();
        for (var i = 0; i < 23; i++)
        {
            // Equal ranks in groups (ties need the id), title hits on every fifth record.
            var title = i % 5 == 0 ? $"Seitenwort {i}" : $"Eintrag {i}";
            var body = string.Join(' ', Enumerable.Repeat("seitenwort", 1 + (i % 3)));
            expected.Add((await CreateInAsync(a, notes.Id, title, body: body)).Id);
        }

        var single = await SearchAsync(a, "seitenwort", limit: RecordSearch.MaxLimit);
        Assert.Equal(23, single.Items.Count);
        Assert.Equal(single.Items.Select(h => h.Rank).OrderDescending(), single.Items.Select(h => h.Rank));

        var paged = new List<SearchHitDto>();
        string? cursor = null;
        do
        {
            var page = await SearchAsync(a, "seitenwort", limit: 4, cursor: cursor);
            Assert.True(page.Items.Count <= 4);
            paged.AddRange(page.Items);
            cursor = page.NextCursor;
            Assert.True(paged.Count < 100, "Paging does not end.");
        }
        while (cursor is not null);

        Assert.Equal(single.Items.Select(h => h.RecordId), paged.Select(h => h.RecordId));
        Assert.Equal(expected.Order(), paged.Select(h => h.RecordId).Order());
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData("", "empty_query")]
    [InlineData("   ", "empty_query")]
    [InlineData("!!! ... ---", "empty_query")]
    [InlineData("-kafka", "empty_query")]
    [InlineData("-kafka -event", "empty_query")]
    [InlineData("a\0b", "invalid_value")]
    [InlineData("a\u0007b", "invalid_value")]
    public async Task Search_UnusableQuery_400WithCode(string? q, string code)
    {
        var (a, _) = await ArrangeSearchAsync();

        var error = Invalid(await TrySearchAsync(a, new SearchRecordsRequest(q)));

        Assert.Equal(code, Field(error, "q").Code);
    }

    [Fact]
    public async Task Search_UnpairedSurrogate_400InvalidValue()
    {
        var (a, _) = await ArrangeSearchAsync();
        var q = "x" + (char)0xD800;

        Assert.Equal("invalid_value", Field(Invalid(await TrySearchAsync(a, new SearchRecordsRequest(q))), "q").Code);
    }

    [Fact]
    public async Task Search_InvalidLimitCursorLength_400()
    {
        var (a, notes) = await ArrangeSearchAsync();
        for (var i = 0; i < 3; i++)
        {
            await CreateInAsync(a, notes.Id, $"Cursorwort {i}");
        }

        Assert.Equal("too_long", Field(Invalid(await TrySearchAsync(a, new SearchRecordsRequest(new string('a', 201)))), "q").Code);
        Assert.True((await TrySearchAsync(a, new SearchRecordsRequest(new string('a', 200)))).IsSuccess);
        Assert.Equal("out_of_range", Field(Invalid(await TrySearchAsync(a, new SearchRecordsRequest("cursorwort", Limit: 0))), "limit").Code);
        Assert.Equal("out_of_range", Field(Invalid(await TrySearchAsync(a, new SearchRecordsRequest("cursorwort", Limit: 51))), "limit").Code);
        Assert.Equal("invalid_cursor", Field(Invalid(await TrySearchAsync(a, new SearchRecordsRequest("cursorwort", Cursor: "kaputt"))), "cursor").Code);

        var first = await SearchAsync(a, "cursorwort", limit: 1);
        Assert.NotNull(first.NextCursor);
        Assert.Equal("invalid_cursor", Field(Invalid(await TrySearchAsync(a, new SearchRecordsRequest("anderes", Cursor: first.NextCursor))), "cursor").Code);
    }

    [Fact]
    public async Task Search_ForeignWorkspace_NeverVisible()
    {
        var (a, notes) = await ArrangeSearchAsync();
        var own = await CreateInAsync(a, notes.Id, "Mandantwort Alpha");
        var beta = Enter(a.Seed.Beta, a.Seed.BetaOwner);
        var betaSpace = await CreateSpaceAsync(beta, a.Seed.BetaOwner, "Beta");
        Db.ChangeTracker.Clear();
        var betaRecord = await CreateUseCase(beta, a.Seed.BetaOwner).HandleAsync(new CreateRecordRequest(SpaceId: betaSpace.Id, Title: "Mandantwort Beta"), Ct);
        Assert.True(betaRecord.IsSuccess, Describe(betaRecord.Error));
        Enter(a.Seed.Alpha, a.Seed.AlphaOwner);

        Assert.Equal([own.Id], await IdsAsync(a, "mandantwort", includeArchived: true));
        Assert.IsType<NotFoundError>((await TrySearchAsync(a, new SearchRecordsRequest("mandantwort", SpaceId: betaSpace.Id))).Error);
    }

    [Fact]
    public async Task Search_WithoutReadScope_Forbidden()
    {
        var (a, _) = await ArrangeSearchAsync();
        var tenant = Enter(a.Seed.Alpha, a.Seed.AlphaOwner, "write");

        var result = await new SearchRecords(Db, tenant, new RecordSearchIndex(Db)).HandleAsync(new SearchRecordsRequest("x"), Ct);

        Assert.IsType<ForbiddenError>(result.Error);
    }

    [Fact]
    public async Task Database_SearchIndex_StartsWithWorkspaceId()
    {
        await ArrangeAsync();
        var definition = await Db.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'records' AND indexname = 'ix_records_workspace_id_search_vector'")
            .SingleAsync(Ct);

        Assert.Contains("USING gin (workspace_id, search_vector)", definition, StringComparison.Ordinal);
    }

    private async Task ChangeSchemaAsync(Arranged a, UpdateSchemaRequest request)
    {
        Db.ChangeTracker.Clear();
        var result = await new UpdateSchema(Db, a.Tenant, [new TemplatePresetSchemaHook(Db), new RecordValuesSchemaHook(Db)], Activity).HandleAsync(request, Ct);
        Assert.True(result.IsSuccess, Describe(result.Error));
        Db.ChangeTracker.Clear();
    }

    /// <summary>The texts between the markers of a snippet.</summary>
    private static List<string> Marked(string snippet) =>
        [.. System.Text.RegularExpressions.Regex.Matches(snippet, "⟦([^⟦⟧]*)⟧").Select(m => m.Groups[1].Value)];

    private async Task<string> RowVersionAsync(Guid id) =>
        await Db.Database.SqlQuery<string>($"SELECT xmin::text AS \"Value\" FROM records WHERE id = {id}").SingleAsync(Ct);

    /// <summary>4-byte letters: CJK Unified Ideographs Extension B (U+20000–U+2A6DF).</summary>
    private static readonly int[] FourByteLetters = [.. Enumerable.Range(0x20000, 0x2A6E0 - 0x20000)];

    /// <summary>
    /// Hyphenated compounds "ab-cd " of 4-byte letters, every part distinct (<paramref name="fromEnd"/> picks another set of
    /// parts), up to <paramref name="size"/> UTF-8 bytes (<paramref name="utf8"/>) or UTF-16 characters. The compound and both
    /// parts become lexemes: about 8 bytes per character in the tsvector.
    /// </summary>
    private static string Compounds(int size, bool fromEnd, bool utf8)
    {
        var pool = FourByteLetters;
        var text = new StringBuilder(size);
        var used = 0;
        var perCompound = utf8 ? 18 : 10;
        var offset = fromEnd ? 1_000_000 : 0;
        for (var n = 0; used + perCompound <= size; n++)
        {
            void Part(int k) => text.Append(char.ConvertFromUtf32(pool[k % pool.Length])).Append(char.ConvertFromUtf32(pool[k / pool.Length % pool.Length]));
            Part(offset + (2 * n));
            text.Append('-');
            Part(offset + (2 * n) + 1);
            text.Append(' ');
            used += perCompound;
        }

        return text.ToString();
    }

    /// <summary><paramref name="start"/>, then <paramref name="filler"/> (ASCII) repeated up to exactly the maximum body size.</summary>
    private static string Fill(string start, string filler)
    {
        var text = new StringBuilder(start, DomainRecord.BodyMaxBytes);
        var bytes = Encoding.UTF8.GetByteCount(start);
        while (bytes + filler.Length <= DomainRecord.BodyMaxBytes)
        {
            text.Append(filler);
            bytes += filler.Length;
        }

        text.Append('x', DomainRecord.BodyMaxBytes - bytes);
        var body = text.ToString();
        Assert.Equal(DomainRecord.BodyMaxBytes, Encoding.UTF8.GetByteCount(body));
        return body;
    }
}
