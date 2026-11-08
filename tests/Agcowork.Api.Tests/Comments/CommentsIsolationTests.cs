using System.Net;
using Agcowork.Api.Tests.Tenancy;

namespace Agcowork.Api.Tests.Comments;

/// <summary>Every comment and mention endpoint against the isolation fixture, plus foreign ids inside the own workspace.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class CommentsIsolationTests(AgcoworkApiFactory factory)
{
    private async Task<(TenancyFixture T, Guid RecordId, Guid CommentId)> AlphaAsync()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var record = await CommentsApi.SeedRecordAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        var comment = await CommentsApi.AddAsync(t.AlphaOwner.Client, t.Alpha.Slug, record.Id, "Isolation");
        return (t, record.Id, comment.Id);
    }

    [Fact]
    public async Task ListComments_Isolated()
    {
        var (t, r, _) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Get, $"/records/{r}/comments", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task AddComment_Isolated()
    {
        var (t, r, _) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Post, $"/records/{r}/comments", new { markdown = "" }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateComment_Isolated()
    {
        var (t, _, c) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Patch, $"/comments/{c}", new { }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteComment_Isolated()
    {
        var (t, _, c) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Delete, $"/comments/{c}", homeStatus: HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ResolveComment_Isolated()
    {
        var (t, _, c) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Post, $"/comments/{c}/resolve", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReopenComment_Isolated()
    {
        var (t, _, c) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Post, $"/comments/{c}/reopen", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task ListMentions_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await t.AssertIsolatedAsync(HttpMethod.Get, "/mentions", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task MarkMentionsRead_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await t.AssertIsolatedAsync(HttpMethod.Post, "/mentions/read", new { }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ForeignIds_InOwnWorkspace_NotFound_AndBetaIsUntouched()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var betaRecord = await CommentsApi.SeedRecordAsync(t.BetaOwner.Client, t.Beta.Slug);
        var betaComment = await CommentsApi.AddAsync(t.BetaOwner.Client, t.Beta.Slug, betaRecord.Id, "Beta intern");
        var client = t.AlphaOwner.Client;
        string Alpha(string relative) => TenancyFixture.Path(t.Alpha.Slug, relative);

        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Get, Alpha($"/records/{betaRecord.Id}/comments"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Post, Alpha($"/records/{betaRecord.Id}/comments"), new { markdown = "Fremd" });
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Patch, Alpha($"/comments/{betaComment.Id}"), new { markdown = "Fremd" });
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Delete, Alpha($"/comments/{betaComment.Id}"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Post, Alpha($"/comments/{betaComment.Id}/resolve"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Post, Alpha($"/comments/{betaComment.Id}/reopen"));

        // A Beta comment as parent of an Alpha reply is unknown on that record.
        var (_, alphaRecord, _) = await AlphaAsync();
        using var reply = await TenancyFixture.SendAsync(client, HttpMethod.Post, Alpha($"/records/{alphaRecord}/comments"), new { markdown = "Fremd", parentCommentId = betaComment.Id });
        Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);

        var beta = Assert.Single((await CommentsApi.ListAsync(t.BetaOwner.Client, t.Beta.Slug, betaRecord.Id)).Threads);
        Assert.Equal(("Beta intern", null, null), (beta.Markdown, beta.ResolvedAt, beta.DeletedAt));
    }
}
