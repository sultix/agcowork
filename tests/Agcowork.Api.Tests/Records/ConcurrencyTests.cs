using System.Net;
using System.Net.Http.Json;
using Agcowork.Api.Tests.Collections;
using Agcowork.Api.Tests.Identity;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Collections;
using Agcowork.Application.Common.Paging;
using Agcowork.Application.Records;
using Agcowork.Application.Workspaces;

namespace Agcowork.Api.Tests.Records;

/// <summary>Concurrent writes to one record: the row lock serializes them (architecture §4.1, expectedRevision).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ConcurrencyTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ParallelUpdates_SameExpectedRevision_ExactlyOneSucceeds()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "conc");
        var second = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, second.ActorId, "editor");
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);

        for (var round = 0; round < 5; round++)
        {
            var record = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collection.Id, $"R{round}");
            var path = TenancyFixture.Path(workspace.Slug, $"/records/{record.Id}");

            var responses = await Task.WhenAll(
                owner.Client.PatchAsJsonAsync(path, new { properties = new { status = "doing" }, expectedRevision = 1 }, Ct),
                second.Client.PatchAsJsonAsync(path, new { properties = new { status = "done" }, expectedRevision = 1 }, Ct));

            var statuses = responses.Select(r => r.StatusCode).Order().ToList();
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], statuses);
            foreach (var response in responses)
            {
                response.Dispose();
            }

            var stored = await RecordsApi.GetAsync(owner.Client, workspace.Slug, record.Id);
            Assert.Equal(2, stored.Revision);
            var history = await owner.Client.GetFromJsonAsync<Page<RevisionSummaryDto>>($"{path}/history", AgcoworkApiFactory.Json, Ct);
            Assert.Equal([2, 1], history!.Items.Select(h => h.No));
        }
    }

    [Fact]
    public async Task ParallelAppends_AllKept_GaplessRevisions()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "conc");
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        var record = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collection.Id, body: "## Log\n");
        var path = TenancyFixture.Path(workspace.Slug, $"/records/{record.Id}/append");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => owner.Client.PostAsJsonAsync(path, new { markdown = $"- entry {i}", section = "Log" }, Ct)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        foreach (var response in responses)
        {
            response.Dispose();
        }

        var stored = await RecordsApi.GetAsync(owner.Client, workspace.Slug, record.Id);
        Assert.Equal(9, stored.Revision);
        Assert.All(Enumerable.Range(0, 8), i => Assert.Contains($"- entry {i}\n", stored.Body, StringComparison.Ordinal));
        var history = await owner.Client.GetFromJsonAsync<Page<RevisionSummaryDto>>(TenancyFixture.Path(workspace.Slug, $"/records/{record.Id}/history"), AgcoworkApiFactory.Json, Ct);
        Assert.Equal(Enumerable.Range(1, 9).Reverse(), history!.Items.Select(h => h.No));
    }

    [Fact]
    public async Task ParallelCreates_SameIdempotencyKey_OneRecord()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "conc");
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        var key = Guid.NewGuid().ToString();

        async Task<(HttpStatusCode Status, Guid? Id)> PostAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(TenancyFixture.Path(workspace.Slug, "/records"), UriKind.Relative))
            {
                Content = JsonContent.Create(new { collectionId = collection.Id, title = "Einmal" }),
            };
            request.Headers.Add("Idempotency-Key", key);
            using var response = await owner.Client.SendAsync(request, Ct);
            return (response.StatusCode, response.StatusCode == HttpStatusCode.Created ? (await response.Content.ReadFromJsonAsync<RecordDto>(AgcoworkApiFactory.Json, Ct))!.Id : null);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostAsync()));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        Assert.Single(results.Select(r => r.Id).Distinct());
        var all = await owner.Client.GetFromJsonAsync<Page<RecordDto>>(TenancyFixture.Path(workspace.Slug, $"/records?collectionId={collection.Id}"), AgcoworkApiFactory.Json, Ct);
        Assert.Single(all!.Items);
    }

    [Fact]
    public async Task ParallelCreates_SameKeyDifferentContent_OneCreated_OneConflict()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "conc");
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);

        for (var round = 0; round < 5; round++)
        {
            var key = Guid.NewGuid().ToString();

            async Task<(HttpStatusCode Status, string Body)> PostAsync(string title)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(TenancyFixture.Path(workspace.Slug, "/records"), UriKind.Relative))
                {
                    Content = JsonContent.Create(new { collectionId = collection.Id, title }),
                };
                request.Headers.Add("Idempotency-Key", key);
                using var response = await owner.Client.SendAsync(request, Ct);
                return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
            }

            var results = await Task.WhenAll(PostAsync($"A {round}"), PostAsync($"B {round}"));

            Assert.Single(results, r => r.Status == HttpStatusCode.Created);
            var conflict = Assert.Single(results, r => r.Status == HttpStatusCode.Conflict);
            Assert.Contains("idempotency_key_reused", conflict.Body, StringComparison.Ordinal);
        }

        var all = await owner.Client.GetFromJsonAsync<Page<RecordDto>>(TenancyFixture.Path(workspace.Slug, $"/records?collectionId={collection.Id}&limit=50"), AgcoworkApiFactory.Json, Ct);
        Assert.Equal(5, all!.Items.Count);
    }

    private async Task<(TestAgent Owner, WorkspaceDto Workspace, CollectionDto Collection)> OptionCollectionAsync()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "lock");
        var space = await SchemaApi.CreateSpaceAsync(owner.Client, workspace.Slug);
        var collection = await SchemaApi.CreateCollectionAsync(owner.Client, workspace.Slug, space.Id, schema:
        [
            new { key = "status", name = "Status", type = "select", options = new[] { new { value = "x" }, new { value = "y" } } },
            new { key = "prio", name = "Prio", type = "number" },
        ]);
        return (owner, workspace, collection);
    }

    private static async Task<HttpResponseMessage> RemoveOptionXAsync(HttpClient client, string slug, Guid collectionId, int? expectedSchemaVersion = null) =>
        await client.PatchAsJsonAsync(
            TenancyFixture.Path(slug, $"/collections/{collectionId}/schema"),
            new { update = new[] { new { key = "status", options = new[] { new { value = "y" } } } }, confirmRemoval = true, expectedSchemaVersion },
            Ct);

    [Fact]
    public async Task SchemaChangeRemovingOption_ParallelToPatch_NoLostUpdate_NoStaleValue()
    {
        for (var round = 0; round < 5; round++)
        {
            var (owner, workspace, collection) = await OptionCollectionAsync();
            var record = await RecordsApi.CreateAsync(owner.Client, workspace.Slug, new { collectionId = collection.Id, title = "R", properties = new { status = "x" } });
            var path = TenancyFixture.Path(workspace.Slug, $"/records/{record.Id}");

            var responses = await Task.WhenAll(
                RemoveOptionXAsync(owner.Client, workspace.Slug, collection.Id),
                owner.Client.PatchAsJsonAsync(path, new { properties = new { prio = 2 } }, Ct));

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            foreach (var response in responses)
            {
                response.Dispose();
            }

            var stored = await RecordsApi.GetAsync(owner.Client, workspace.Slug, record.Id);
            Assert.Equal(2, stored.Properties["prio"].GetInt32());
            Assert.False(stored.Properties.ContainsKey("status"));
            Assert.Equal(2, stored.Revision);
            var history = await owner.Client.GetFromJsonAsync<Page<RevisionSummaryDto>>($"{path}/history", AgcoworkApiFactory.Json, Ct);
            Assert.Equal([2, 1], history!.Items.Select(h => h.No));
        }
    }

    [Fact]
    public async Task Create_WithRemovedOption_AfterSchemaCommit_400_ParallelCreateNeverKeepsIt()
    {
        var (owner, workspace, collection) = await OptionCollectionAsync();
        using (var removed = await RemoveOptionXAsync(owner.Client, workspace.Slug, collection.Id))
        {
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        }

        using (var create = await owner.Client.PostAsJsonAsync(TenancyFixture.Path(workspace.Slug, "/records"), new { collectionId = collection.Id, title = "R", properties = new { status = "x" } }, Ct))
        {
            var problem = await ProblemAssert.IsProblemAsync(create, HttpStatusCode.BadRequest, "validation_failed");
            Assert.Equal("not_in_options", ProblemAssert.Field(problem, "properties.status").GetProperty("code").GetString());
        }

        for (var round = 0; round < 5; round++)
        {
            var (o, w, c) = await OptionCollectionAsync();
            var responses = await Task.WhenAll(
                RemoveOptionXAsync(o.Client, w.Slug, c.Id),
                o.Client.PostAsJsonAsync(TenancyFixture.Path(w.Slug, "/records"), new { collectionId = c.Id, title = "R", properties = new { status = "x" } }, Ct));

            Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
            Assert.Contains(responses[1].StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.BadRequest });
            foreach (var response in responses)
            {
                response.Dispose();
            }

            var all = await o.Client.GetFromJsonAsync<Page<RecordDto>>(TenancyFixture.Path(w.Slug, $"/records?collectionId={c.Id}"), AgcoworkApiFactory.Json, Ct);
            Assert.All(all!.Items, r => Assert.False(r.Properties.ContainsKey("status")));
        }
    }

    [Fact]
    public async Task ParallelSchemaChanges_SameExpectedVersion_One200One409()
    {
        for (var round = 0; round < 5; round++)
        {
            var (owner, workspace, collection) = await OptionCollectionAsync();
            await RecordsApi.CreateAsync(owner.Client, workspace.Slug, new { collectionId = collection.Id, title = "R", properties = new { status = "x" } });
            var schemaPath = TenancyFixture.Path(workspace.Slug, $"/collections/{collection.Id}/schema");

            var responses = await Task.WhenAll(
                RemoveOptionXAsync(owner.Client, workspace.Slug, collection.Id, expectedSchemaVersion: 1),
                owner.Client.PatchAsJsonAsync(schemaPath, new { add = new[] { new { key = "due", name = "Due", type = "date" } }, expectedSchemaVersion = 1 }, Ct));

            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
            var conflict = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
            await ProblemAssert.IsProblemAsync(conflict, HttpStatusCode.Conflict, "schema_version_conflict");
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }
}
