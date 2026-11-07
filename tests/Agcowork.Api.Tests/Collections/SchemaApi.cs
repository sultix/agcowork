using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Collections;
using Agcowork.Application.Collections.Templates;
using Agcowork.Application.Spaces;

namespace Agcowork.Api.Tests.Collections;

/// <summary>HTTP helpers for spaces, collections and templates. Each call fails the test unless it succeeds.</summary>
internal static class SchemaApi
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<SpaceDto> CreateSpaceAsync(HttpClient client, string slug, string name = "Produkt")
    {
        using var response = await client.PostAsJsonAsync(TenancyFixture.Path(slug, "/spaces"), new { name, description = "Product work." }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SpaceDto>(AgcoworkApiFactory.Json, Ct))!;
    }

    public static async Task<CollectionDto> CreateCollectionAsync(HttpClient client, string slug, Guid spaceId, string name = "Aufgaben", object[]? schema = null)
    {
        using var response = await client.PostAsJsonAsync(
            TenancyFixture.Path(slug, "/collections"),
            new { spaceId, name, description = "Work items. Add one per task.", schema = schema ?? [] },
            Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CollectionDto>(AgcoworkApiFactory.Json, Ct))!;
    }

    public static async Task<TemplateDto> CreateTemplateAsync(HttpClient client, string slug, Guid collectionId, string name = "Standard", object? preset = null)
    {
        using var response = await client.PostAsJsonAsync(
            TenancyFixture.Path(slug, $"/collections/{collectionId}/templates"),
            new { name, propertiesPreset = preset ?? new { }, bodyMarkdown = "## Kontext\n" },
            Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<TemplateDto>(AgcoworkApiFactory.Json, Ct))!;
    }

    /// <summary>Space, collection and template in <paramref name="slug"/>, created by <paramref name="client"/> (owner/admin).</summary>
    public static async Task<(SpaceDto Space, CollectionDto Collection, TemplateDto Template)> SeedAsync(HttpClient client, string slug)
    {
        var space = await CreateSpaceAsync(client, slug, $"Space {Guid.NewGuid():N}");
        var collection = await CreateCollectionAsync(client, slug, space.Id, schema: [new { key = "priority", name = "Priority", type = "select", options = new[] { new { value = "high" } } }]);
        var template = await CreateTemplateAsync(client, slug, collection.Id);
        return (space, collection, template);
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
}
