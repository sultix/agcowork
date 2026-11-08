using System.Net;
using System.Net.Http.Json;
using Agcowork.Api.Tests.Records;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Comments;
using Agcowork.Application.Records;

namespace Agcowork.Api.Tests.Comments;

/// <summary>HTTP helpers for comments and mentions. Each call fails the test unless it succeeds.</summary>
internal static class CommentsApi
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The canonical mention link of an actor.</summary>
    public static string Mention(Guid actorId, string name = "Jemand") => $"[@{name}](actor:{actorId})";

    /// <summary>A collection and a record in it, created by <paramref name="client"/>.</summary>
    public static async Task<RecordDto> SeedRecordAsync(HttpClient client, string slug, string title = "Diskussion")
    {
        var (_, collection) = await RecordsApi.SeedCollectionAsync(client, slug);
        return await RecordsApi.CreateInAsync(client, slug, collection.Id, title);
    }

    public static async Task<CommentDto> AddAsync(HttpClient client, string slug, Guid recordId, string markdown, Guid? parentCommentId = null)
    {
        using var response = await client.PostAsJsonAsync(TenancyFixture.Path(slug, $"/records/{recordId}/comments"), new { markdown, parentCommentId }, AgcoworkApiFactory.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CommentDto>(AgcoworkApiFactory.Json, Ct))!;
    }

    public static async Task<CommentThreadsDto> ListAsync(HttpClient client, string slug, Guid recordId, string query = "") =>
        (await client.GetFromJsonAsync<CommentThreadsDto>(TenancyFixture.Path(slug, $"/records/{recordId}/comments{query}"), AgcoworkApiFactory.Json, Ct))!;

    public static async Task<MentionPage> MentionsAsync(HttpClient client, string slug, string query = "") =>
        (await client.GetFromJsonAsync<MentionPage>(TenancyFixture.Path(slug, $"/mentions{query}"), AgcoworkApiFactory.Json, Ct))!;
}
