using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Records;
using Agcowork.Application.Workspaces;

namespace Agcowork.Api.Tests.Mcp;

/// <summary>
/// End to end as an agent would work (AP-011 acceptance 2): orient, read the schema of 'Entscheidungen', write an ADR from
/// the template, append a consequence, find it by status, read its history.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class AgentScenarioTests(AgcoworkApiFactory factory)
{
    private static readonly string[] Accepted = ["accepted"];

    [Fact]
    public async Task Agent_RecordsAnAdr_FindsItAndReadsItsHistory()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(t.AlphaOwner.Client, "adr", "software-projekt");
        await using var agent = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, workspace.Slug);

        // 1. Orientation.
        var overview = await McpClientFixture.OkAsync<WorkspaceOverviewDto>(agent, "workspace_overview");
        var decisions = overview.Spaces.SelectMany(s => s.Collections).Single(c => c.Name == "Entscheidungen");

        // 2. Schema and template.
        var described = McpClientFixture.Ok(await McpClientFixture.CallAsync(agent, "collection_describe", new { collectionId = decisions.Id }));
        var statusValues = described.GetProperty("jsonSchema").GetProperty("properties").GetProperty("status").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("accepted", statusValues);

        // 3. ADR from the default template (body comes from it).
        var adr = await McpClientFixture.OkAsync<RecordDto>(agent, "record_create", new
        {
            collectionId = decisions.Id,
            title = "PostgreSQL statt MongoDB",
            properties = new { status = "accepted", date = "2026-11-05", decided_by = t.AlphaOwner.ActorId },
            idempotencyKey = Guid.NewGuid().ToString(),
        });

        // 4. Consequence appended to its section.
        await McpClientFixture.OkAsync<RecordDto>(agent, "record_append", new
        {
            recordId = adr.Id,
            markdown = "- Wir brauchen Migrationen mit EF Core.",
            section = "Konsequenzen",
            reason = "Konsequenz ergänzt",
        });

        // 5. Query by status.
        var found = McpClientFixture.Ok(await McpClientFixture.CallAsync(agent, "records_query", new
        {
            collectionId = decisions.Id,
            filter = new { prop = "status", op = "in", value = Accepted },
        }));
        Assert.Equal([adr.Id], found.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));

        // 6. Markdown with frontmatter, and the history.
        var markdown = McpClientFixture.Text(await McpClientFixture.CallAsync(agent, "record_get", new { recordId = adr.Id }));
        Assert.StartsWith("---\n", markdown, StringComparison.Ordinal);
        Assert.Contains("collection: Entscheidungen", markdown, StringComparison.Ordinal);
        Assert.Contains("status: accepted", markdown, StringComparison.Ordinal);
        Assert.Contains("revision: 2", markdown, StringComparison.Ordinal);
        var consequences = markdown.IndexOf("## Konsequenzen", StringComparison.Ordinal);
        Assert.True(consequences > 0);
        Assert.True(markdown.IndexOf("- Wir brauchen Migrationen", StringComparison.Ordinal) > consequences);

        var history = McpClientFixture.Ok(await McpClientFixture.CallAsync(agent, "history_get", new { recordId = adr.Id }));
        var revisions = history.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["appended", "created"], revisions.Select(r => r.GetProperty("kind").GetString()));
        Assert.Equal("Konsequenz ergänzt", revisions[0].GetProperty("reason").GetString());
        Assert.All(revisions, r => Assert.Equal(t.AlphaOwner.ActorId, r.GetProperty("actorId").GetGuid()));
    }
}
