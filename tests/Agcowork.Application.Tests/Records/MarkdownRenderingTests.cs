using System.Text.Json;
using Agcowork.Application.Records;

namespace Agcowork.Application.Tests.Records;

public sealed class MarkdownRenderingTests(PostgresFixture postgres) : RecordTestBase(postgres)
{
    private static readonly DateTimeOffset Created = new(2026, 11, 5, 9, 30, 0, TimeSpan.FromHours(1));

    [Fact]
    public void Render_Snapshot()
    {
        var record = new RecordDto(
            Guid.Parse("0190f5e2-7a4c-7000-8000-000000000001"),
            Guid.Parse("0190f5e2-7a4c-7000-8000-000000000002"),
            Guid.Parse("0190f5e2-7a4c-7000-8000-000000000003"),
            null,
            "PostgreSQL statt MongoDB",
            Props("""
                {
                  "status": "akzeptiert",
                  "date": "2026-11-05",
                  "tags": ["db", "true", "a: b"],
                  "empty": [],
                  "estimate": 2.5,
                  "done": false,
                  "summary": "Line one\nline \"two\"",
                  "payload": { "k": [1, 2] },
                  "revision": "collides"
                }
                """),
            "## Kontext\n…\n",
            3,
            Guid.Parse("0190f5e2-7a4c-7000-8000-0000000000c1"),
            Created,
            Guid.Parse("0190f5e2-7a4c-7000-8000-0000000000c2"),
            Created.AddMinutes(90),
            null);

        var markdown = RecordMarkdown.Render(record, "Entscheidungen", "Technik");

        Assert.Equal(
            """
            ---
            id: 0190f5e2-7a4c-7000-8000-000000000001
            collection: Entscheidungen
            collection_id: 0190f5e2-7a4c-7000-8000-000000000003
            space: Technik
            space_id: 0190f5e2-7a4c-7000-8000-000000000002
            title: PostgreSQL statt MongoDB
            status: akzeptiert
            date: 2026-11-05
            tags:
              - db
              - "true"
              - "a: b"
            empty: []
            estimate: 2.5
            done: false
            summary: "Line one\nline \"two\""
            payload: {"k":[1,2]}
            properties.revision: collides
            revision: 3
            created_at: 2026-11-05T08:30:00Z
            updated_at: 2026-11-05T10:00:00Z
            created_by: 0190f5e2-7a4c-7000-8000-0000000000c1
            updated_by: 0190f5e2-7a4c-7000-8000-0000000000c2
            ---
            ## Kontext
            …

            """.Replace("\r\n", "\n", StringComparison.Ordinal),
            markdown);
    }

    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("", "\"\"")]
    [InlineData(" padded", "\" padded\"")]
    [InlineData("- dash", "\"- dash\"")]
    [InlineData("#tag", "\"#tag\"")]
    [InlineData("a #b", "\"a #b\"")]
    [InlineData("null", "\"null\"")]
    [InlineData("Yes", "\"Yes\"")]
    [InlineData("42", "\"42\"")]
    [InlineData("1e3", "\"1e3\"")]
    [InlineData("Übergröße ä", "Übergröße ä")]
    [InlineData("14:30", "14:30")]
    [InlineData("ends:", "\"ends:\"")]
    public void Quote_PlainOnlyWhenUnambiguous(string value, string expected) =>
        Assert.Equal(expected, RecordMarkdown.Quote(value));

    [Fact]
    public async Task GetRecordMarkdown_RendersStoredRecordWithCollectionAndSpace()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, "Index anlegen", """{ "tags": ["db"] }""", "## Kontext\nText\n");

        var markdown = await new GetRecordMarkdown(Db, a.Tenant, RecordTranslator).HandleAsync(new GetRecordMarkdownRequest(record.Id), Ct);

        Assert.True(markdown.IsSuccess);
        Assert.StartsWith($"---\nid: {record.Id}\ncollection: Aufgaben\ncollection_id: {a.Collection.Id}\nspace: Produkt\n", markdown.Value, StringComparison.Ordinal);
        Assert.Contains("\ntitle: Index anlegen\nstatus: todo\ntags:\n  - db\nrevision: 1\n", markdown.Value, StringComparison.Ordinal);
        Assert.EndsWith($"updated_by: {a.Seed.AlphaOwner.Id}\n---\n## Kontext\nText\n", markdown.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRecordMarkdown_FreePage_NoCollectionLines()
    {
        var a = await ArrangeAsync();
        var page = (await TryCreateAsync(a, new CreateRecordRequest(SpaceId: a.SpaceId, Title: "Notiz", Body: "Text"))).Value;

        var markdown = (await new GetRecordMarkdown(Db, a.Tenant, RecordTranslator).HandleAsync(new GetRecordMarkdownRequest(page.Id), Ct)).Value;

        Assert.DoesNotContain("collection", markdown, StringComparison.Ordinal);
        Assert.Contains("\nspace: Produkt\n", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ValuesAreValidJsonInsideQuotes()
    {
        var quoted = RecordMarkdown.Quote("tab\there \u0001");

        Assert.Equal("tab\there \u0001", JsonSerializer.Deserialize<string>(quoted));
    }
}
