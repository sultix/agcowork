using System.Data.Common;
using System.Text.Json;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Query;
using Agcowork.Application.Records;
using Agcowork.Application.Views;
using Microsoft.EntityFrameworkCore;
using Record = Agcowork.Domain.Records.Record;

namespace Agcowork.Application.Tests.Query;

/// <summary>
/// The SQL translation against PostgreSQL: every operator of the matrix per type (also negated, so missing values are
/// handled exactly), nested groups, typed sorting with empty values last, keyset pagination, archived records, fields, limits
/// and the GIN index.
/// </summary>
public sealed class SqlQueryTranslatorTests(PostgresFixture postgres) : QueryTestBase(postgres)
{
    private static bool IgnoreCase(string? value, Func<string, bool> test) => value is not null && test(value);

    /// <summary>Each case as given and wrapped in <c>not</c>: the negation must return exactly the complement.</summary>
    private async Task AssertCasesAsync(Seed seed, params (string Filter, Func<Seeded, bool> Expected)[] cases)
    {
        foreach (var (filter, expected) in cases)
        {
            await AssertFilterAsync(seed, filter, expected);
            await AssertFilterAsync(seed, $$"""{ "not": {{filter}} }""", r => !expected(r));
        }
    }

    [Fact]
    public async Task Text_EveryOperator()
    {
        var seed = await SeedAsync();
        await AssertCasesAsync(seed,
            ("""{ "prop": "notes", "op": "eq", "value": "Notiz 1" }""", r => r.Str("notes") == "Notiz 1"),
            ("""{ "prop": "notes", "op": "neq", "value": "Notiz 1" }""", r => r.Str("notes") != "Notiz 1"),
            ("""{ "prop": "notes", "op": "contains", "value": "OTIZ 2" }""", r => IgnoreCase(r.Str("notes"), s => s.Contains("otiz 2", StringComparison.OrdinalIgnoreCase))),
            ("""{ "prop": "notes", "op": "starts_with", "value": "notiz" }""", r => IgnoreCase(r.Str("notes"), s => s.StartsWith("notiz", StringComparison.OrdinalIgnoreCase))),
            ("""{ "prop": "notes", "op": "in", "value": ["Notiz 0", "Notiz 2"] }""", r => r.Str("notes") is "Notiz 0" or "Notiz 2"),
            ("""{ "prop": "notes", "op": "nin", "value": ["Notiz 0", "Notiz 2"] }""", r => r.Str("notes") is not ("Notiz 0" or "Notiz 2")),
            ("""{ "prop": "notes", "op": "is_empty" }""", r => string.IsNullOrEmpty(r.Str("notes"))),
            ("""{ "prop": "notes", "op": "is_not_empty" }""", r => !string.IsNullOrEmpty(r.Str("notes"))),
            ("""{ "prop": "md", "op": "contains", "value": "**FETT** 1" }""", r => IgnoreCase(r.Str("md"), s => s.Contains("**fett** 1", StringComparison.OrdinalIgnoreCase))),
            ("""{ "prop": "site", "op": "starts_with", "value": "https://example.com/1" }""", r => IgnoreCase(r.Str("site"), s => s.StartsWith("https://example.com/1", StringComparison.Ordinal))),
            ("""{ "prop": "mail", "op": "eq", "value": "user4@example.com" }""", r => r.Str("mail") == "user4@example.com"),
            ("""{ "prop": "mail", "op": "is_empty" }""", r => r.Str("mail") is null));
    }

    [Fact]
    public async Task Title_PseudoField_EveryOperator_WildcardsAreLiteral()
    {
        var seed = await SeedAsync();
        await AssertCasesAsync(seed,
            ("""{ "prop": "title", "op": "eq", "value": "Aufgabe 03" }""", r => r.Title == "Aufgabe 03"),
            ("""{ "prop": "title", "op": "neq", "value": "Aufgabe 03" }""", r => r.Title != "Aufgabe 03"),
            ("""{ "prop": "title", "op": "contains", "value": "100%" }""", r => r.Title.Contains("100%", StringComparison.Ordinal)),
            ("""{ "prop": "title", "op": "contains", "value": "%" }""", r => r.Title.Contains('%', StringComparison.Ordinal)),
            ("""{ "prop": "title", "op": "contains", "value": "i_d" }""", r => r.Title.Contains("i_d", StringComparison.OrdinalIgnoreCase)),
            ("""{ "prop": "title", "op": "contains", "value": "_" }""", r => r.Title.Contains('_', StringComparison.Ordinal)),
            ("""{ "prop": "title", "op": "starts_with", "value": "datenbank" }""", r => r.Title.StartsWith("Datenbank", StringComparison.Ordinal)),
            ("""{ "prop": "title", "op": "in", "value": ["Aufgabe 03", "API_Doku 01"] }""", r => r.Title is "Aufgabe 03" or "API_Doku 01"),
            ("""{ "prop": "title", "op": "nin", "value": ["Aufgabe 03"] }""", r => r.Title != "Aufgabe 03"),
            ("""{ "prop": "title", "op": "is_empty" }""", _ => false),
            ("""{ "prop": "title", "op": "is_not_empty" }""", _ => true));
    }

    [Fact]
    public async Task Number_EveryOperator()
    {
        var seed = await SeedAsync();
        await AssertCasesAsync(seed,
            ("""{ "prop": "estimate", "op": "eq", "value": 1.5 }""", r => r.Num("estimate") == 1.5m),
            ("""{ "prop": "estimate", "op": "eq", "value": 2 }""", r => r.Num("estimate") == 2m),
            ("""{ "prop": "estimate", "op": "neq", "value": 2.0 }""", r => r.Num("estimate") != 2m),
            ("""{ "prop": "estimate", "op": "gt", "value": 2 }""", r => r.Num("estimate") > 2),
            ("""{ "prop": "estimate", "op": "gte", "value": 2 }""", r => r.Num("estimate") >= 2),
            ("""{ "prop": "estimate", "op": "lt", "value": 1.5 }""", r => r.Num("estimate") < 1.5m),
            ("""{ "prop": "estimate", "op": "lte", "value": 1.5 }""", r => r.Num("estimate") <= 1.5m),
            ("""{ "prop": "estimate", "op": "in", "value": [0, 4.5] }""", r => r.Num("estimate") is 0m or 4.5m),
            ("""{ "prop": "estimate", "op": "nin", "value": [0, 4.5] }""", r => r.Num("estimate") is not (0m or 4.5m)),
            ("""{ "prop": "estimate", "op": "is_empty" }""", r => r.Num("estimate") is null),
            ("""{ "prop": "estimate", "op": "is_not_empty" }""", r => r.Num("estimate") is not null));
    }

    [Fact]
    public async Task Checkbox_EveryOperator()
    {
        var seed = await SeedAsync();
        await AssertCasesAsync(seed,
            ("""{ "prop": "done", "op": "eq", "value": true }""", r => r.Bool("done") == true),
            ("""{ "prop": "done", "op": "eq", "value": false }""", r => r.Bool("done") == false),
            ("""{ "prop": "done", "op": "neq", "value": true }""", r => r.Bool("done") != true),
            ("""{ "prop": "done", "op": "is_empty" }""", r => r.Bool("done") is null));
    }

    [Fact]
    public async Task SelectAndStatus_EveryOperator()
    {
        var seed = await SeedAsync();
        await AssertCasesAsync(seed,
            ("""{ "prop": "status", "op": "eq", "value": "done" }""", r => r.Str("status") == "done"),
            ("""{ "prop": "status", "op": "neq", "value": "done" }""", r => r.Str("status") != "done"),
            ("""{ "prop": "status", "op": "in", "value": ["todo", "in_progress"] }""", r => r.Str("status") is "todo" or "in_progress"),
            ("""{ "prop": "status", "op": "nin", "value": ["todo", "in_progress"] }""", r => r.Str("status") is not ("todo" or "in_progress")),
            ("""{ "prop": "status", "op": "is_empty" }""", r => r.Str("status") is null),
            ("""{ "prop": "priority", "op": "eq", "value": "high" }""", r => r.Str("priority") == "high"),
            ("""{ "prop": "priority", "op": "neq", "value": "high" }""", r => r.Str("priority") != "high"),
            ("""{ "prop": "priority", "op": "is_not_empty" }""", r => r.Str("priority") is not null));
    }

    [Fact]
    public async Task MultiSelect_EveryOperator()
    {
        var seed = await SeedAsync();
        await AssertCasesAsync(seed,
            ("""{ "prop": "tags", "op": "contains", "value": "db" }""", r => r.List("tags").Contains("db")),
            ("""{ "prop": "tags", "op": "in", "value": ["ui", "db"] }""", r => r.List("tags").Intersect(["ui", "db"]).Any()),
            ("""{ "prop": "tags", "op": "nin", "value": ["ui"] }""", r => !r.List("tags").Contains("ui")),
            ("""{ "prop": "tags", "op": "is_empty" }""", r => r.List("tags").Count == 0),
            ("""{ "prop": "tags", "op": "is_not_empty" }""", r => r.List("tags").Count > 0));
    }

    [Fact]
    public async Task Date_EveryOperator()
    {
        var seed = await SeedAsync();
        var day = new DateOnly(2026, 11, 10);
        await AssertCasesAsync(seed,
            ("""{ "prop": "due", "op": "eq", "value": "2026-11-10" }""", r => r.Date("due") == day),
            ("""{ "prop": "due", "op": "neq", "value": "2026-11-10" }""", r => r.Date("due") != day),
            ("""{ "prop": "due", "op": "gt", "value": "2026-11-10" }""", r => r.Date("due") > day),
            ("""{ "prop": "due", "op": "gte", "value": "2026-11-10" }""", r => r.Date("due") >= day),
            ("""{ "prop": "due", "op": "lt", "value": "2026-11-10" }""", r => r.Date("due") < day),
            ("""{ "prop": "due", "op": "lte", "value": "2026-11-10" }""", r => r.Date("due") <= day),
            ("""{ "prop": "due", "op": "is_empty" }""", r => r.Date("due") is null),
            ("""{ "prop": "due", "op": "is_not_empty" }""", r => r.Date("due") is not null));
    }

    [Fact]
    public async Task DateTime_EveryOperator_OffsetsAndPlainDates()
    {
        var seed = await SeedAsync();
        var noon = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var exact = seed.Records.Single(r => r.I == 9).Time("at")!.Value;
        await AssertCasesAsync(seed,
            ($$"""{ "prop": "at", "op": "eq", "value": "{{exact.ToOffset(TimeSpan.FromHours(2)):yyyy-MM-dd'T'HH:mm:sszzz}}" }""", r => r.Time("at") == exact),
            ($$"""{ "prop": "at", "op": "neq", "value": "{{exact:yyyy-MM-dd'T'HH:mm:ss'Z'}}" }""", r => r.Time("at") != exact),
            ("""{ "prop": "at", "op": "gt", "value": "2026-11-02T13:00:00+01:00" }""", r => r.Time("at") > noon),
            ("""{ "prop": "at", "op": "gte", "value": "2026-11-02T12:00:00Z" }""", r => r.Time("at") >= noon),
            ("""{ "prop": "at", "op": "lt", "value": "2026-11-02" }""", r => r.Time("at") < new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero)),
            ("""{ "prop": "at", "op": "lte", "value": "2026-11-02T12:00:00Z" }""", r => r.Time("at") <= noon),
            ("""{ "prop": "at", "op": "is_empty" }""", r => r.Time("at") is null),
            ("""{ "prop": "at", "op": "is_not_empty" }""", r => r.Time("at") is not null));
    }

    [Fact]
    public async Task Relation_EveryOperator_SingleAndMultiple()
    {
        var seed = await SeedAsync();
        var (db, api, ui) = (seed.Components[0].ToString(), seed.Components[1].ToString(), seed.Components[2].ToString());
        await AssertCasesAsync(seed,
            ($$"""{ "prop": "component", "op": "contains", "value": "{{db.ToUpperInvariant()}}" }""", r => r.Str("component") == db),
            ($$"""{ "prop": "component", "op": "in", "value": ["{{db}}", "{{api}}"] }""", r => r.Str("component") is { } c && (c == db || c == api)),
            ($$"""{ "prop": "component", "op": "nin", "value": ["{{db}}"] }""", r => r.Str("component") != db),
            ("""{ "prop": "component", "op": "is_empty" }""", r => r.Str("component") is null),
            ($$"""{ "prop": "affects", "op": "contains", "value": "{{ui}}" }""", r => r.List("affects").Contains(ui)),
            ($$"""{ "prop": "affects", "op": "in", "value": ["{{ui}}", "{{api}}"] }""", r => r.List("affects").Intersect([ui, api]).Any()),
            ($$"""{ "prop": "affects", "op": "nin", "value": ["{{ui}}"] }""", r => !r.List("affects").Contains(ui)),
            ("""{ "prop": "affects", "op": "is_empty" }""", r => r.List("affects").Count == 0),
            ("""{ "prop": "affects", "op": "is_not_empty" }""", r => r.List("affects").Count > 0));
    }

    [Fact]
    public async Task Relation_Multiple_AlsoMatchesScalarIdFromBeforeMultipleWasSwitchedOn()
    {
        var seed = await SeedAsync();
        var collection = await Db.Collections.AsNoTracking().SingleAsync(c => c.Id == seed.CollectionId, Ct);
        var legacy = Record.Create(seed.A.Seed.Alpha.Id, seed.A.SpaceId, collection, null, null, "Alt", new Dictionary<string, JsonElement>
        {
            ["affects"] = JsonSerializer.SerializeToElement(seed.Components[2].ToString()),
        }, null, seed.A.Seed.AlphaOwner.Id, Clock);
        Db.Records.Add(legacy);
        await Db.SaveChangesAsync(Ct);

        var result = await QueryAsync(seed, new QueryDefinition(Filter($$"""{ "prop": "affects", "op": "contains", "value": "{{seed.Components[2]}}" }"""), Limit: 500));

        Assert.Contains(result.Items, r => r.Id == legacy.Id);
        Assert.Equal(seed.Active.Count(r => r.List("affects").Contains(seed.Components[2].ToString())) + 1, result.Items.Count);
    }

    [Fact]
    public async Task Actor_EveryOperator_GuidOrActorPrefix()
    {
        var seed = await SeedAsync();
        var (owner, other) = (seed.A.Seed.AlphaOwner.Id.ToString(), seed.A.Seed.MemberOfBoth.Id.ToString());
        await AssertCasesAsync(seed,
            ($$"""{ "prop": "owner", "op": "eq", "value": "actor:{{owner}}" }""", r => r.Str("owner") == owner),
            ($$"""{ "prop": "owner", "op": "neq", "value": "{{owner}}" }""", r => r.Str("owner") != owner),
            ($$"""{ "prop": "owner", "op": "in", "value": ["{{owner}}", "actor:{{other}}"] }""", r => r.Str("owner") is not null),
            ($$"""{ "prop": "owner", "op": "nin", "value": ["{{other}}"] }""", r => r.Str("owner") != other),
            ("""{ "prop": "owner", "op": "is_empty" }""", r => r.Str("owner") is null),
            ("""{ "prop": "owner", "op": "is_not_empty" }""", r => r.Str("owner") is not null));
    }

    [Fact]
    public async Task ComputedAndPseudoFields_UseColumns()
    {
        var seed = await SeedAsync();
        var middle = seed.Records[20].CreatedAt;
        var owner = seed.A.Seed.AlphaOwner.Id;
        await AssertCasesAsync(seed,
            ($$"""{ "prop": "created", "op": "gt", "value": "{{middle:O}}" }""", r => r.CreatedAt > middle),
            ($$"""{ "prop": "created_at", "op": "lte", "value": "{{middle:O}}" }""", r => r.CreatedAt <= middle),
            ($$"""{ "prop": "created", "op": "eq", "value": "{{middle:O}}" }""", r => r.CreatedAt == middle),
            ("""{ "prop": "created", "op": "is_empty" }""", _ => false),
            ($$"""{ "prop": "author", "op": "eq", "value": "{{owner}}" }""", r => r.CreatedBy == owner),
            ($$"""{ "prop": "created_by", "op": "in", "value": ["actor:{{owner}}"] }""", r => r.CreatedBy == owner),
            ($$"""{ "prop": "updated_by", "op": "neq", "value": "{{owner}}" }""", r => r.CreatedBy != owner),
            ("""{ "prop": "updated_at", "op": "gte", "value": "2026-11-07" }""", _ => true),
            ("""{ "prop": "author", "op": "is_not_empty" }""", _ => true));
    }

    [Fact]
    public async Task ArchitectureExample_AndOrNested_ExactRecords()
    {
        var seed = await SeedAsync();
        var owner = seed.A.Seed.AlphaOwner.Id;
        var filter = $$"""
            {
              "and": [
                { "prop": "status", "op": "in", "value": ["todo", "in_progress"] },
                { "prop": "owner", "op": "eq", "value": "actor:{{owner}}" },
                { "or": [
                  { "prop": "due", "op": "lt", "value": "2026-11-08" },
                  { "prop": "priority", "op": "eq", "value": "high" }
                ]}
              ]
            }
            """;
        bool Expected(Seeded r) =>
            r.Str("status") is "todo" or "in_progress"
            && r.Str("owner") == owner.ToString()
            && (r.Date("due") < new DateOnly(2026, 11, 8) || r.Str("priority") == "high");

        await AssertFilterAsync(seed, filter, Expected);
        // The seed is rich enough that every branch decides somewhere: matches via due, via priority, and misses.
        Assert.Contains(seed.Active, r => Expected(r) && r.Str("priority") != "high");
        Assert.Contains(seed.Active, r => Expected(r) && r.Date("due") >= new DateOnly(2026, 11, 8));
        Assert.Contains(seed.Active, r => !Expected(r) && r.Str("owner") == owner.ToString());
    }

    [Fact]
    public async Task DeeplyNested_AndOrNot()
    {
        var seed = await SeedAsync();
        await AssertFilterAsync(
            seed,
            """
            { "or": [
              { "and": [ { "prop": "tags", "op": "contains", "value": "api" }, { "not": { "or": [ { "prop": "estimate", "op": "gt", "value": 3 }, { "prop": "done", "op": "eq", "value": true } ] } } ] },
              { "not": { "not": { "prop": "title", "op": "starts_with", "value": "100%" } } }
            ] }
            """,
            r => (r.List("tags").Contains("api") && !(r.Num("estimate") > 3 || r.Bool("done") == true)) || r.Title.StartsWith("100%", StringComparison.Ordinal));
    }

    /// <summary>Expected order: empty values last (both directions), then the value (strings ordinal), then (created_at, id).</summary>
    private static List<Guid> Oracle(Seed seed, Func<Seeded, object?> key, bool descending)
    {
        static int Compare(object? a, object? b) => (a, b) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            (string x, string y) => string.CompareOrdinal(x, y),
            _ => ((IComparable)a).CompareTo(b),
        };

        var sorted = seed.Active.ToList();
        sorted.Sort((x, y) =>
        {
            var (a, b) = (key(x), key(y));
            if ((a is null) != (b is null))
            {
                return a is null ? 1 : -1;
            }

            var byValue = descending ? Compare(b, a) : Compare(a, b);
            return byValue != 0 ? byValue
                : x.CreatedAt != y.CreatedAt ? x.CreatedAt.CompareTo(y.CreatedAt)
                : string.CompareOrdinal(x.Id.ToString("D"), y.Id.ToString("D"));
        });
        return [.. sorted.Select(r => r.Id)];
    }

    public static TheoryData<string, bool> SortCases => new()
    {
        { "estimate", false }, { "estimate", true },
        { "due", false }, { "due", true },
        { "at", false }, { "at", true },
        { "notes", false }, { "notes", true },
        { "title", false }, { "title", true },
        { "created", true }, { "created_at", false },
        { "done", false }, { "status", true }, { "owner", false }, { "author", true },
    };

    [Theory]
    [MemberData(nameof(SortCases))]
    public async Task Sort_Typed_EmptyLast_StableTieBreaker_AndCursorWalksEveryRecordOnce(string prop, bool descending)
    {
        var seed = await SeedAsync();
        Func<Seeded, object?> key = prop switch
        {
            "estimate" => r => r.Num("estimate"),
            "due" => r => r.Date("due"),
            "at" => r => r.Time("at"),
            "title" => r => r.Title,
            "created" or "created_at" => r => r.CreatedAt,
            "done" => r => r.Bool("done") is { } b ? (b ? "true" : "false") : null,
            "author" => r => r.CreatedBy.ToString("D"),
            _ => r => r.Str(prop),
        };
        var expected = Oracle(seed, key, descending);
        var definition = new QueryDefinition(Sorts: [new ViewSortInput(prop, descending ? "desc" : "asc")], Limit: 7);

        var single = await QueryAsync(seed, definition with { Limit = QueryParser.MaxLimit });
        var pages = await PagesAsync(seed, definition);

        Assert.Equal(expected, single.Items.Select(r => r.Id));
        Assert.True(pages.Count >= 3);
        Assert.All(pages.SkipLast(1), p => Assert.Equal(7, p.Count));
        Assert.Equal(expected, pages.SelectMany(p => p).Select(r => r.Id));
    }

    [Fact]
    public async Task Cursor_SeveralSortsWithManyTies_EveryRecordExactlyOnce()
    {
        var seed = await SeedAsync();
        var definition = new QueryDefinition(
            Filter("""{ "prop": "status", "op": "is_not_empty" }"""),
            [new ViewSortInput("priority", "desc"), new ViewSortInput("status", "asc"), new ViewSortInput("done", "desc")],
            Limit: 3);

        var pages = await PagesAsync(seed, definition);
        var all = (await QueryAsync(seed, definition with { Limit = QueryParser.MaxLimit })).Items.Select(r => r.Id).ToList();

        var walked = pages.SelectMany(p => p).Select(r => r.Id).ToList();
        Assert.Equal(all, walked);
        Assert.Equal(walked.Count, walked.Distinct().Count());
        Assert.Equal(seed.Active.Count(r => r.Str("status") is not null), walked.Count);
        Assert.True(pages.Count >= 3);
    }

    [Fact]
    public async Task Cursor_LastPageHasNoNextCursor_ExactFit()
    {
        var seed = await SeedAsync();
        var active = seed.Active.Count();

        var exact = await QueryAsync(seed, new QueryDefinition(Limit: active));
        var oneLess = await QueryAsync(seed, new QueryDefinition(Limit: active - 1));

        Assert.Null(exact.NextCursor);
        Assert.Equal(active, exact.Items.Count);
        Assert.NotNull(oneLess.NextCursor);
        var rest = await QueryAsync(seed, new QueryDefinition(Limit: active - 1, Cursor: oneLess.NextCursor));
        Assert.Equal(exact.Items[^1].Id, Assert.Single(rest.Items).Id);
        Assert.Null(rest.NextCursor);
    }

    public static TheoryData<string> BadCursors => new()
    {
        "not-a-cursor!",
        "eyJ2IjoxfQ",
        "AAAA",
        Convert.ToBase64String("""{"v":1,"s":"","k":[],"c":"x","i":"y"}"""u8.ToArray()).TrimEnd('='),
    };

    [Theory]
    [MemberData(nameof(BadCursors))]
    public async Task Cursor_Invalid_400InvalidCursor(string cursor)
    {
        var seed = await SeedAsync();

        var result = await TryQueryAsync(seed, new QueryDefinition(Cursor: cursor));

        Assert.Equal(ErrorCodes.InvalidCursor, Field(Invalid(result), "cursor").Code);
    }

    [Fact]
    public async Task Cursor_OfOtherSortOrder_400InvalidCursor()
    {
        var seed = await SeedAsync();
        var page = await QueryAsync(seed, new QueryDefinition(Sorts: [new ViewSortInput("estimate", "asc")], Limit: 5));

        var other = await TryQueryAsync(seed, new QueryDefinition(Sorts: [new ViewSortInput("estimate", "desc")], Limit: 5, Cursor: page.NextCursor));
        var none = await TryQueryAsync(seed, new QueryDefinition(Limit: 5, Cursor: page.NextCursor));

        Assert.Equal(ErrorCodes.InvalidCursor, Field(Invalid(other), "cursor").Code);
        Assert.Equal(ErrorCodes.InvalidCursor, Field(Invalid(none), "cursor").Code);
    }

    [Fact]
    public async Task IncludeArchived_AndCount()
    {
        var seed = await SeedAsync();

        var active = await QueryAsync(seed, new QueryDefinition(Limit: 5, IncludeCount: true));
        var all = await QueryAsync(seed, new QueryDefinition(Limit: 5, IncludeArchived: true, IncludeCount: true));
        var archivedTitles = string.Join(", ", seed.Records.Where(r => r.Archived).Select(r => $"\"{r.Title}\""));
        var archivedOnly = await QueryAsync(seed, new QueryDefinition(Filter($$"""{ "prop": "title", "op": "in", "value": [{{archivedTitles}}] }"""), IncludeArchived: true));
        var hidden = await QueryAsync(seed, new QueryDefinition(Filter($$"""{ "prop": "title", "op": "in", "value": [{{archivedTitles}}] }""")));

        Assert.Equal(SeedSize - 2, active.Count);
        Assert.Equal(SeedSize, all.Count);
        Assert.Equal(2, archivedOnly.Items.Count);
        Assert.All(archivedOnly.Items, r => Assert.NotNull(r.ArchivedAt));
        Assert.Empty(hidden.Items);
        Assert.Null((await QueryAsync(seed, new QueryDefinition())).Count);
    }

    [Fact]
    public async Task Fields_ReduceProperties_BodyOnlyOnRequest()
    {
        var seed = await SeedAsync();
        var target = seed.Records[5];
        Db.ChangeTracker.Clear();
        await new UpdateRecord(Db, TestActor.Agent(seed.A.Seed.AlphaOwner.Id), seed.A.Tenant, Validator(), Activity, Clock)
            .HandleAsync(new UpdateRecordRequest(target.Id, Body: "## Inhalt"), Ct);
        var filter = Filter($$"""{ "prop": "title", "op": "eq", "value": "{{target.Title}}" }""");

        var all = Assert.Single((await QueryAsync(seed, new QueryDefinition(filter))).Items);
        var some = Assert.Single((await QueryAsync(seed, new QueryDefinition(filter, Fields: ["owner", "estimate", "author", "title"]))).Items);
        var withBody = Assert.Single((await QueryAsync(seed, new QueryDefinition(filter, Fields: ["body"]))).Items);

        Assert.Null(all.Body);
        Assert.Contains("payload", all.Properties.Keys);
        Assert.Equal(["estimate", "owner", "author"], some.Properties.Keys);
        Assert.Equal(target.Title, some.Title);
        Assert.Null(some.Body);
        Assert.Equal("## Inhalt", withBody.Body);
        Assert.Empty(withBody.Properties);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public async Task Limit_OutOfRange_400(int limit)
    {
        var seed = await SeedAsync();

        var result = await TryQueryAsync(seed, new QueryDefinition(Limit: limit));

        Assert.Equal(ErrorCodes.OutOfRange, Field(Invalid(result), "limit").Code);
    }

    [Fact]
    public async Task Limit_DefaultAndBounds()
    {
        var seed = await SeedAsync();

        Assert.Equal(SeedSize - 2, (await QueryAsync(seed, new QueryDefinition())).Items.Count);
        Assert.Single((await QueryAsync(seed, new QueryDefinition(Limit: 1))).Items);
        Assert.Equal(SeedSize - 2, (await QueryAsync(seed, new QueryDefinition(Limit: 500))).Items.Count);
    }

    [Fact]
    public async Task OtherWorkspace_SameCollectionId_NotFound()
    {
        var seed = await SeedAsync();
        var beta = Enter(seed.A.Seed.Beta, seed.A.Seed.BetaOwner);
        Db.ChangeTracker.Clear();

        var result = await new ExecuteQuery(Db, beta, Schemas(), Translator).HandleAsync(new ExecuteQueryRequest(seed.CollectionId, new QueryDefinition()), Ct);

        Assert.IsType<NotFoundError>(result.Error);
    }

    [Fact]
    public async Task Explain_EqOnSelectAndContainsOnRelation_UseGinIndex()
    {
        var seed = await SeedAsync();
        var collection = await Db.Collections.AsNoTracking().SingleAsync(c => c.Id == seed.CollectionId, Ct);
        var clock = TimeProvider.System;
        for (var i = 0; i < 3000; i++)
        {
            var properties = new Dictionary<string, JsonElement>
            {
                ["priority"] = JsonSerializer.SerializeToElement(i % 2 == 0 ? "low" : "high"),
                ["component"] = JsonSerializer.SerializeToElement(seed.Components[i == 0 ? 1 : 0].ToString()),
            };
            Db.Records.Add(Record.Create(seed.A.Seed.Alpha.Id, seed.A.SpaceId, collection, null, null, $"Bulk {i}", properties, null, seed.A.Seed.AlphaOwner.Id, clock));
        }

        Db.Records.Add(Record.Create(seed.A.Seed.Alpha.Id, seed.A.SpaceId, collection, null, null, "Rare", new Dictionary<string, JsonElement>
        {
            ["status"] = JsonSerializer.SerializeToElement("done"),
            ["priority"] = JsonSerializer.SerializeToElement("high"),
        }, null, seed.A.Seed.AlphaOwner.Id, clock));
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();
        await Db.Database.ExecuteSqlRawAsync("ANALYZE records", Ct);

        var schema = (await Schemas().GetSchemaAsync(seed.CollectionId, Ct))!;
        string Plan(string filterJson)
        {
            var errors = new Agcowork.Application.Common.Validation.FieldErrors();
            var filter = QueryParser.ParseFilter(errors, "filter", schema.Properties, Filter(filterJson))!;
            var query = Translator.Filter(Db.Records.AsNoTracking().Where(r => r.CollectionId == seed.CollectionId), filter);
            using var command = query.CreateDbCommand();
            return ExplainAsync(command).GetAwaiter().GetResult();
        }

        await Db.Database.OpenConnectionAsync(Ct);
        try
        {
            // The planner may prefer other plans on tiny tables; forbid sequential scans to see whether the index is usable.
            await Db.Database.ExecuteSqlRawAsync("SET enable_seqscan = off", Ct);
            var select = Plan("""{ "prop": "status", "op": "eq", "value": "done" }""");
            var relation = Plan($$"""{ "prop": "component", "op": "contains", "value": "{{seed.Components[1]}}" }""");

            Assert.True(select.Contains("Bitmap Index Scan on ix_records_workspace_id_properties", StringComparison.Ordinal), select);
            Assert.Contains("properties @>", select, StringComparison.Ordinal);
            Assert.True(relation.Contains("Bitmap Index Scan on ix_records_workspace_id_properties", StringComparison.Ordinal), relation);
        }
        finally
        {
            await Db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<string> ExplainAsync(DbCommand command)
    {
        command.CommandText = "EXPLAIN " + command.CommandText;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var lines = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }
}
