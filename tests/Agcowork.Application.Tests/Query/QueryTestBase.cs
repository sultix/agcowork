using System.Globalization;
using System.Text.Json;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Query;
using Agcowork.Application.Records;
using Agcowork.Application.Schema;
using Agcowork.Application.Tests.Records;
using Agcowork.Domain.Views;
using Agcowork.Infrastructure.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Agcowork.Application.Tests.Query;

/// <summary>
/// Query tests on a seed of 50 records over all property types in collection "Alles" (plus 3 records in "Komponenten", the
/// relation target). Every seed record is kept as a .NET model (<see cref="Seeded"/>), so expected results are computed in
/// .NET and compared with what PostgreSQL returns.
/// </summary>
public abstract class QueryTestBase(PostgresFixture postgres) : RecordTestBase(postgres)
{
    public const int SeedSize = 50;

    internal static readonly string[] Statuses = ["todo", "in_progress", "done"];

    internal sealed record Seeded(int I, Guid Id, string Title, IReadOnlyDictionary<string, JsonElement> Values, DateTimeOffset CreatedAt, Guid CreatedBy, bool Archived)
    {
        public JsonElement? this[string key] => Values.TryGetValue(key, out var v) ? v : null;

        public string? Str(string key) => this[key] is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

        public decimal? Num(string key) => this[key] is { ValueKind: JsonValueKind.Number } v ? v.GetDecimal() : null;

        public bool? Bool(string key) => this[key] is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : null;

        public IReadOnlyList<string> List(string key) => this[key] is { ValueKind: JsonValueKind.Array } v ? [.. v.EnumerateArray().Select(e => e.GetString()!)] : [];

        public DateOnly? Date(string key) => Str(key) is { } s ? DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

        public DateTimeOffset? Time(string key) => Str(key) is { } s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture) : null;
    }

    internal sealed record Seed(Arranged A, Guid CollectionId, IReadOnlyList<Guid> Components, IReadOnlyList<Seeded> Records)
    {
        public IEnumerable<Seeded> Active => Records.Where(r => !r.Archived);
    }

    internal static IRecordQueryTranslator Translator { get; } = new SqlQueryTranslator();

    internal ISchemaProvider Schemas()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgcoworkDbContext>(Db);
        services.AddApplication();
        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<ISchemaProvider>();
    }

    internal static PropertyDefinitionInput Options(string key, string type, params string[] options) =>
        new(key, key, type, Options: [.. options.Select(o => new PropertyOptionInput(o, Group: type == "status" ? (o == "todo" ? "todo" : o == "done" ? "done" : "in_progress") : null))]);

    internal async Task<Seed> SeedAsync()
    {
        var a = await ArrangeAsync();
        var components = await CreateCollectionAsync(a.Tenant, a.Seed.AlphaOwner, a.SpaceId, "Komponenten", new PropertyDefinitionInput("kind", "Kind", "text"));
        var targets = new List<Guid>();
        foreach (var name in new[] { "Datenbank", "API", "UI" })
        {
            var created = await TryCreateAsync(a, new CreateRecordRequest(components.Id, Title: name));
            Assert.True(created.IsSuccess, Describe(created.Error));
            targets.Add(created.Value.Id);
        }

        var all = await CreateCollectionAsync(a.Tenant, a.Seed.AlphaOwner, a.SpaceId, "Alles",
            new PropertyDefinitionInput("notes", "Notes", "text"),
            new PropertyDefinitionInput("md", "Markdown", "markdown"),
            new PropertyDefinitionInput("site", "Site", "url"),
            new PropertyDefinitionInput("mail", "Mail", "email"),
            new PropertyDefinitionInput("estimate", "Estimate", "number", Precision: 1),
            new PropertyDefinitionInput("done", "Done", "checkbox"),
            Options("status", "status", Statuses),
            Options("priority", "select", "high", "low"),
            Options("tags", "multi_select", "db", "api", "ui"),
            new PropertyDefinitionInput("due", "Due", "date"),
            new PropertyDefinitionInput("at", "At", "datetime"),
            new PropertyDefinitionInput("component", "Component", "relation", TargetCollectionId: components.Id),
            new PropertyDefinitionInput("affects", "Affects", "relation", TargetCollectionId: components.Id, Multiple: true),
            new PropertyDefinitionInput("owner", "Owner", "actor"),
            new PropertyDefinitionInput("payload", "Payload", "json"),
            new PropertyDefinitionInput("created", "Created", "created_at"),
            new PropertyDefinitionInput("author", "Author", "created_by"));

        var other = Enter(a.Seed.Alpha, a.Seed.MemberOfBoth);
        Enter(a.Seed.Alpha, a.Seed.AlphaOwner);
        var records = new List<Seeded>();
        for (var i = 0; i < SeedSize; i++)
        {
            // Pairs share a creation time, so the tie-breaker needs the id.
            if (i % 2 == 0)
            {
                Clock.Advance(TimeSpan.FromMinutes(1));
            }

            var title = (i % 5) switch
            {
                0 => $"Datenbank migrieren {i:00}",
                1 => $"API_Doku {i:00}",
                2 => $"100% Abdeckung {i:00}",
                _ => $"Aufgabe {i:00}",
            };
            var values = new Dictionary<string, object?>
            {
                ["notes"] = i == 6 ? "" : i % 4 == 0 ? null : $"Notiz {i % 3}",
                ["md"] = i % 3 == 0 ? $"**fett** {i}" : null,
                ["site"] = i % 2 == 0 ? $"https://example.com/{i}" : null,
                ["mail"] = i % 3 == 1 ? $"user{i}@example.com" : null,
                ["estimate"] = i % 6 == 0 ? null : (i % 10) * 0.5m,
                ["done"] = i % 3 == 0 ? null : i % 2 == 0,
                ["status"] = i % 11 == 0 ? null : Statuses[i % 3],
                ["priority"] = i % 5 == 4 ? null : i % 2 == 0 ? "high" : "low",
                ["tags"] = (i % 4) switch { 0 => null, 1 => new[] { "db" }, 2 => new[] { "api", "ui" }, _ => new[] { "db", "api" } },
                ["due"] = i % 5 == 3 ? null : new DateOnly(2026, 11, 1).AddDays(i % 20).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["at"] = i % 4 == 2 ? null : (i % 3 == 0
                    ? new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero).AddHours(i).ToOffset(TimeSpan.FromHours(1)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)
                    : new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero).AddHours(i).AddMilliseconds(i * 7).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
                ["component"] = i % 3 == 2 ? null : targets[i % 3].ToString(),
                ["affects"] = i % 4 == 0 ? null : new[] { targets[i % 3].ToString(), targets[(i + 1) % 3].ToString() },
                ["owner"] = i % 3 == 0 ? null : (i % 2 == 0 ? a.Seed.AlphaOwner.Id : a.Seed.MemberOfBoth.Id).ToString(),
                ["payload"] = i % 2 == 1 ? new { x = i } : null,
            };
            var properties = values.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));
            var tenant = i % 2 == 0 ? a.Tenant : other;
            var actor = i % 2 == 0 ? a.Seed.AlphaOwner : a.Seed.MemberOfBoth;
            Tenant.Current = tenant;
            Db.ChangeTracker.Clear();
            var created = await CreateUseCase(tenant, actor).HandleAsync(new CreateRecordRequest(all.Id, Title: title, Properties: properties), Ct);
            Assert.True(created.IsSuccess, $"{i}: {Describe(created.Error)}");
            var archived = i is 7 or 21;
            if (archived)
            {
                Db.ChangeTracker.Clear();
                var archive = await new ArchiveRecord(Db, TestActor.Agent(actor.Id), tenant, Activity, Clock).HandleAsync(new ArchiveRecordRequest(created.Value.Id), Ct);
                Assert.True(archive.IsSuccess, Describe(archive.Error));
            }

            records.Add(new Seeded(i, created.Value.Id, title, created.Value.Properties, created.Value.CreatedAt, actor.Id, archived));
        }

        Tenant.Current = a.Tenant;
        Db.ChangeTracker.Clear();
        return new Seed(a, all.Id, targets, records);
    }

    internal ExecuteQuery Query(Seed seed) => new(Db, seed.A.Tenant, Schemas(), Translator);

    internal async Task<Result<QueryResult<RecordDto>>> TryQueryAsync(Seed seed, QueryDefinition definition)
    {
        Db.ChangeTracker.Clear();
        return await Query(seed).HandleAsync(new ExecuteQueryRequest(seed.CollectionId, definition), Ct);
    }

    internal async Task<QueryResult<RecordDto>> QueryAsync(Seed seed, QueryDefinition definition)
    {
        var result = await TryQueryAsync(seed, definition);
        Assert.True(result.IsSuccess, Describe(result.Error));
        return result.Value;
    }

    internal static ViewFilterNode Filter(string json) => JsonSerializer.Deserialize<ViewFilterNode>(json, JsonOptions)!;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Runs <paramref name="filterJson"/> (one page of up to 500) and compares the ids with the .NET oracle.</summary>
    internal async Task AssertFilterAsync(Seed seed, string filterJson, Func<Seeded, bool> expected)
    {
        var result = await QueryAsync(seed, new QueryDefinition(Filter(filterJson), Limit: QueryParser.MaxLimit));
        var want = seed.Active.Where(expected).Select(r => r.I).Order().ToList();
        var got = result.Items.Select(r => seed.Records.Single(s => s.Id == r.Id).I).Order().ToList();
        Assert.True(want.SequenceEqual(got), $"{filterJson}\n  expected: [{string.Join(",", want)}]\n  actual:   [{string.Join(",", got)}]");
        Assert.Null(result.NextCursor);
    }

    /// <summary>All pages of a query, following nextCursor.</summary>
    internal async Task<List<List<RecordDto>>> PagesAsync(Seed seed, QueryDefinition definition)
    {
        var pages = new List<List<RecordDto>>();
        string? cursor = null;
        do
        {
            var page = await QueryAsync(seed, definition with { Cursor = cursor });
            pages.Add([.. page.Items]);
            cursor = page.NextCursor;
            Assert.True(pages.Count < 100, "Pagination does not end.");
        }
        while (cursor is not null);

        return pages;
    }
}
