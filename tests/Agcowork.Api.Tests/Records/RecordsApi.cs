using System.Net;
using System.Net.Http.Json;
using Agcowork.Api.Tests.Collections;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Collections;
using Agcowork.Application.Records;
using Agcowork.Application.Spaces;

namespace Agcowork.Api.Tests.Records;

/// <summary>HTTP helpers for records. Each call fails the test unless it succeeds.</summary>
internal static class RecordsApi
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Space and collection "Aufgaben" (status select with default, tags) in <paramref name="slug"/>.</summary>
    public static async Task<(SpaceDto Space, CollectionDto Collection)> SeedCollectionAsync(HttpClient client, string slug)
    {
        var space = await SchemaApi.CreateSpaceAsync(client, slug, $"Space {Guid.NewGuid():N}");
        var collection = await SchemaApi.CreateCollectionAsync(client, slug, space.Id, schema:
        [
            new { key = "status", name = "Status", type = "select", required = true, @default = "todo", options = new[] { new { value = "todo" }, new { value = "doing" }, new { value = "done" } } },
            new { key = "tags", name = "Tags", type = "multi_select", options = new[] { new { value = "db" }, new { value = "api" } } },
        ]);
        return (space, collection);
    }

    public static async Task<RecordDto> CreateAsync(HttpClient client, string slug, object body)
    {
        using var response = await client.PostAsJsonAsync(TenancyFixture.Path(slug, "/records"), body, AgcoworkApiFactory.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RecordDto>(AgcoworkApiFactory.Json, Ct))!;
    }

    public static Task<RecordDto> CreateInAsync(HttpClient client, string slug, Guid collectionId, string title = "Aufgabe", string? body = null) =>
        CreateAsync(client, slug, new { collectionId, title, body });

    public static async Task<RecordDto> GetAsync(HttpClient client, string slug, Guid id, string query = "?includeBody=true") =>
        (await client.GetFromJsonAsync<RecordDto>(TenancyFixture.Path(slug, $"/records/{id}{query}"), AgcoworkApiFactory.Json, Ct))!;
}
