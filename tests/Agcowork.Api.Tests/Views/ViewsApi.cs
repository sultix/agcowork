using System.Net;
using System.Net.Http.Json;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Views;

namespace Agcowork.Api.Tests.Views;

/// <summary>HTTP helpers for views. Each call fails the test unless it succeeds.</summary>
internal static class ViewsApi
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static readonly object[] TaskSchema =
    [
        new
        {
            key = "status", name = "Status", type = "status",
            options = new object[] { new { value = "todo", group = "todo" }, new { value = "done", group = "done" } },
        },
        new { key = "priority", name = "Priority", type = "select", options = new[] { new { value = "high" }, new { value = "low" } } },
        new { key = "due", name = "Due", type = "date" },
    ];

    public static async Task<List<ViewDto>> ListAsync(HttpClient client, string slug, Guid collectionId)
    {
        using var response = await client.GetAsync(new Uri(TenancyFixture.Path(slug, $"/collections/{collectionId}/views"), UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<ViewDto>>(AgcoworkApiFactory.Json, Ct))!;
    }

    public static async Task<ViewDto> CreateAsync(HttpClient client, string slug, Guid collectionId, object body)
    {
        using var response = await client.PostAsJsonAsync(TenancyFixture.Path(slug, $"/collections/{collectionId}/views"), body, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ViewDto>(AgcoworkApiFactory.Json, Ct))!;
    }

    public static async Task<ViewDto> GetAsync(HttpClient client, string slug, Guid viewId) =>
        (await client.GetFromJsonAsync<ViewDto>(TenancyFixture.Path(slug, $"/views/{viewId}"), AgcoworkApiFactory.Json, Ct))!;

    /// <summary>Collection with the task schema, one extra board view, in a fresh space of <paramref name="slug"/>.</summary>
    public static async Task<(Guid CollectionId, ViewDto Board)> SeedAsync(HttpClient client, string slug)
    {
        var space = await Collections.SchemaApi.CreateSpaceAsync(client, slug, $"Space {Guid.NewGuid():N}");
        var collection = await Collections.SchemaApi.CreateCollectionAsync(client, slug, space.Id, schema: TaskSchema);
        var board = await CreateAsync(client, slug, collection.Id, new { name = "Board", type = "board", groupBy = "status" });
        return (collection.Id, board);
    }
}
