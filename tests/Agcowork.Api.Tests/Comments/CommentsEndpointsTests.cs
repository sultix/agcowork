using System.Net;
using System.Net.Http.Json;
using System.Text;
using Agcowork.Api.Tests.Identity;
using Agcowork.Api.Tests.Records;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Activity;
using Agcowork.Application.Comments;
using Agcowork.Application.Records;

namespace Agcowork.Api.Tests.Comments;

/// <summary>Comments and mentions over REST (AP-029): the whole lifecycle, the error codes on the wire, the inbox.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class CommentsEndpointsTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Own workspace: owner agent, an editor agent and a seeded record (tests change memberships and archive).</summary>
    private async Task<(string Slug, TestAgent Owner, TestAgent Editor, RecordDto Record)> ArrangeAsync()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "cm");
        var editor = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, editor.ActorId, "editor");
        var record = await CommentsApi.SeedRecordAsync(owner.Client, workspace.Slug);
        return (workspace.Slug, owner, editor, record);
    }

    private static string Path(string slug, string relative) => TenancyFixture.Path(slug, relative);

    [Fact]
    public async Task Lifecycle_AddReplyEditResolveReopenDelete_OverRest()
    {
        var (slug, owner, editor, record) = await ArrangeAsync();

        var root = await CommentsApi.AddAsync(owner.Client, slug, record.Id, $"Frage an {CommentsApi.Mention(editor.ActorId, "Editor")}");
        var reply = await CommentsApi.AddAsync(editor.Client, slug, record.Id, "Antwort", root.Id);
        using (var patch = await owner.Client.PatchAsJsonAsync(Path(slug, $"/comments/{root.Id}"), new { markdown = "Frage (präzisiert)" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
            Assert.NotNull((await patch.Content.ReadFromJsonAsync<CommentDto>(AgcoworkApiFactory.Json, Ct))!.EditedAt);
        }

        using (var resolve = await editor.Client.PostAsync(new Uri(Path(slug, $"/comments/{root.Id}/resolve"), UriKind.Relative), null, Ct))
        {
            Assert.Equal(editor.ActorId, (await resolve.Content.ReadFromJsonAsync<CommentDto>(AgcoworkApiFactory.Json, Ct))!.ResolvedBy);
        }

        Assert.Empty((await CommentsApi.ListAsync(owner.Client, slug, record.Id, "?includeResolved=false")).Threads);
        using (var reopen = await editor.Client.PostAsync(new Uri(Path(slug, $"/comments/{root.Id}/reopen"), UriKind.Relative), null, Ct))
        {
            Assert.Null((await reopen.Content.ReadFromJsonAsync<CommentDto>(AgcoworkApiFactory.Json, Ct))!.ResolvedAt);
        }

        using (var delete = await owner.Client.DeleteAsync(new Uri(Path(slug, $"/comments/{root.Id}"), UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        var threads = await CommentsApi.ListAsync(owner.Client, slug, record.Id);
        var placeholder = Assert.Single(threads.Threads);
        Assert.Null(placeholder.Markdown);
        Assert.Equal(reply.Id, Assert.Single(placeholder.Replies!).Id);
        Assert.Equal(1, (await RecordsApi.GetAsync(owner.Client, slug, record.Id)).Revision);
    }

    [Fact]
    public async Task Errors_NotRoot_NotAuthor_NotMember_OnTheWire()
    {
        var (slug, owner, editor, record) = await ArrangeAsync();
        var outsider = await factory.CreateAgentAsync(cancellationToken: Ct);
        var root = await CommentsApi.AddAsync(owner.Client, slug, record.Id, "Wurzel");
        var reply = await CommentsApi.AddAsync(owner.Client, slug, record.Id, "Antwort", root.Id);

        using var nested = await owner.Client.PostAsJsonAsync(Path(slug, $"/records/{record.Id}/comments"), new { markdown = "Tiefer", parentCommentId = reply.Id }, Ct);
        Assert.Equal("not_root", ProblemAssert.Field(await ProblemAssert.IsProblemAsync(nested, HttpStatusCode.BadRequest, "validation_failed"), "parentCommentId").GetProperty("code").GetString());

        using var resolveReply = await owner.Client.PostAsync(new Uri(Path(slug, $"/comments/{reply.Id}/resolve"), UriKind.Relative), null, Ct);
        Assert.Equal("not_root", ProblemAssert.Field(await ProblemAssert.IsProblemAsync(resolveReply, HttpStatusCode.BadRequest, "validation_failed"), "resolved").GetProperty("code").GetString());

        using var foreignEdit = await editor.Client.PatchAsJsonAsync(Path(slug, $"/comments/{root.Id}"), new { markdown = "Fremd" }, Ct);
        await ProblemAssert.IsProblemAsync(foreignEdit, HttpStatusCode.Forbidden, "not_author");
        using var foreignDelete = await editor.Client.DeleteAsync(new Uri(Path(slug, $"/comments/{root.Id}"), UriKind.Relative), Ct);
        await ProblemAssert.IsProblemAsync(foreignDelete, HttpStatusCode.Forbidden, "not_author");

        using var notMember = await owner.Client.PostAsJsonAsync(Path(slug, $"/records/{record.Id}/comments"), new { markdown = $"Hallo {CommentsApi.Mention(outsider.ActorId)}" }, Ct);
        var field = ProblemAssert.Field(await ProblemAssert.IsProblemAsync(notMember, HttpStatusCode.BadRequest, "validation_failed"), "markdown");
        Assert.Equal("not_member", field.GetProperty("code").GetString());
        Assert.Contains(outsider.ActorId.ToString(), field.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchivedRecord_CommentConflict_ReadOk()
    {
        var (slug, owner, _, record) = await ArrangeAsync();
        var root = await CommentsApi.AddAsync(owner.Client, slug, record.Id, "Vorher");
        using (var archive = await owner.Client.PostAsync(new Uri(Path(slug, $"/records/{record.Id}/archive"), UriKind.Relative), null, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        }

        using var add = await owner.Client.PostAsJsonAsync(Path(slug, $"/records/{record.Id}/comments"), new { markdown = "Nachher" }, Ct);
        await ProblemAssert.IsProblemAsync(add, HttpStatusCode.Conflict, "record_archived");
        using var resolve = await owner.Client.PostAsync(new Uri(Path(slug, $"/comments/{root.Id}/resolve"), UriKind.Relative), null, Ct);
        await ProblemAssert.IsProblemAsync(resolve, HttpStatusCode.Conflict, "record_archived");
        Assert.Single((await CommentsApi.ListAsync(owner.Client, slug, record.Id)).Threads);
    }

    [Theory]
    [InlineData("a\\u0000b")]
    [InlineData("\\ud800")]
    public async Task UnstorableMarkdown_InvalidValue(string escaped)
    {
        var (slug, owner, _, record) = await ArrangeAsync();
        var root = await CommentsApi.AddAsync(owner.Client, slug, record.Id, "Gut");

        foreach (var (method, path) in new[] { (HttpMethod.Post, $"/records/{record.Id}/comments"), (HttpMethod.Patch, $"/comments/{root.Id}") })
        {
            using var request = new HttpRequestMessage(method, new Uri(Path(slug, path), UriKind.Relative))
            {
                Content = new StringContent($$"""{ "markdown": "{{escaped}}" }""", Encoding.UTF8, "application/json"),
            };
            using var response = await owner.Client.SendAsync(request, Ct);
            var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
            Assert.Equal("invalid_value", ProblemAssert.Field(problem, "markdown").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Mentions_Inbox_UnreadFilter_MarkRead_ForeignIdsChangeNothing()
    {
        var (slug, owner, editor, record) = await ArrangeAsync();
        var first = await CommentsApi.AddAsync(owner.Client, slug, record.Id, $"Eins {CommentsApi.Mention(editor.ActorId)}");
        await CommentsApi.AddAsync(owner.Client, slug, record.Id, $"Zwei {CommentsApi.Mention(editor.ActorId)}", first.Id);
        await CommentsApi.AddAsync(editor.Client, slug, record.Id, $"An dich {CommentsApi.Mention(owner.ActorId)}");

        var inbox = await CommentsApi.MentionsAsync(editor.Client, slug);
        Assert.Equal(2, inbox.Items.Count);
        Assert.Equal(2, inbox.UnreadCount);
        Assert.All(inbox.Items, m => Assert.Equal("Diskussion", m.RecordTitle));
        var ownerMention = Assert.Single((await CommentsApi.MentionsAsync(owner.Client, slug)).Items);

        using (var foreign = await editor.Client.PostAsJsonAsync(Path(slug, "/mentions/read"), new { ids = new[] { ownerMention.Id } }, Ct))
        {
            var result = (await foreign.Content.ReadFromJsonAsync<MarkMentionsReadResult>(AgcoworkApiFactory.Json, Ct))!;
            Assert.Equal((0, 2), (result.Marked, result.UnreadCount));
        }

        Assert.Null(Assert.Single((await CommentsApi.MentionsAsync(owner.Client, slug)).Items).ReadAt);

        using (var one = await editor.Client.PostAsJsonAsync(Path(slug, "/mentions/read"), new { ids = new[] { inbox.Items[0].Id } }, Ct))
        {
            Assert.Equal(1, (await one.Content.ReadFromJsonAsync<MarkMentionsReadResult>(AgcoworkApiFactory.Json, Ct))!.Marked);
        }

        var unread = await CommentsApi.MentionsAsync(editor.Client, slug, "?unreadOnly=true&limit=1");
        Assert.Equal(inbox.Items[1].Id, Assert.Single(unread.Items).Id);
        Assert.Null(unread.NextCursor);
        Assert.Equal(1, unread.UnreadCount);

        using var invalid = await editor.Client.PostAsJsonAsync(Path(slug, "/mentions/read"), new { }, Ct);
        await ProblemAssert.IsProblemAsync(invalid, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Mentions_Pagination_NoDuplicates()
    {
        var (slug, owner, editor, record) = await ArrangeAsync();
        for (var i = 0; i < 5; i++)
        {
            await CommentsApi.AddAsync(owner.Client, slug, record.Id, $"#{i} {CommentsApi.Mention(editor.ActorId)}");
        }

        var ids = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await CommentsApi.MentionsAsync(editor.Client, slug, $"?limit=2{(cursor is null ? string.Empty : $"&cursor={cursor}")}");
            ids.AddRange(page.Items.Select(m => m.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(5, ids.Distinct().Count());
        Assert.Equal(5, ids.Count);
    }

    [Fact]
    public async Task GetRecord_IncludeComments_ReturnsThreads()
    {
        var (slug, owner, _, record) = await ArrangeAsync();
        var root = await CommentsApi.AddAsync(owner.Client, slug, record.Id, "Frage");
        await CommentsApi.AddAsync(owner.Client, slug, record.Id, "Antwort", root.Id);

        var with = await RecordsApi.GetAsync(owner.Client, slug, record.Id, "?includeComments=true");
        var without = await RecordsApi.GetAsync(owner.Client, slug, record.Id, string.Empty);

        Assert.Single(Assert.Single(with.Comments!.Threads).Replies!);
        Assert.Null(without.Comments);
    }

    [Fact]
    public async Task GetRecord_MarkdownIncludeComments_AppendsCommentsSection()
    {
        var (slug, owner, _, record) = await ArrangeAsync();
        await CommentsApi.AddAsync(owner.Client, slug, record.Id, "Im Markdown sichtbar");

        var with = await owner.Client.GetStringAsync(new Uri(Path(slug, $"/records/{record.Id}?format=markdown&includeComments=true"), UriKind.Relative), Ct);
        var without = await owner.Client.GetStringAsync(new Uri(Path(slug, $"/records/{record.Id}?format=markdown"), UriKind.Relative), Ct);

        Assert.Contains("# Comments", with, StringComparison.Ordinal);
        Assert.Contains("Im Markdown sichtbar", with, StringComparison.Ordinal);
        Assert.DoesNotContain("Im Markdown sichtbar", without, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mentions_RemovedMember_NotFound()
    {
        var (slug, owner, editor, record) = await ArrangeAsync();
        await CommentsApi.AddAsync(owner.Client, slug, record.Id, $"An {CommentsApi.Mention(editor.ActorId)}");
        Assert.Single((await CommentsApi.MentionsAsync(editor.Client, slug)).Items);

        using (var remove = await owner.Client.DeleteAsync(new Uri(Path(slug, $"/members/{editor.ActorId}"), UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        }

        await TenancyFixture.AssertNotFoundAsync(editor.Client, HttpMethod.Get, Path(slug, "/mentions"));
        await TenancyFixture.AssertNotFoundAsync(editor.Client, HttpMethod.Post, Path(slug, "/mentions/read"), new { all = true });
    }

    [Fact]
    public async Task IdempotencyKey_SameKey_SameComment()
    {
        var (slug, owner, _, record) = await ArrangeAsync();

        async Task<CommentDto> SendAsync(string markdown)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Path(slug, $"/records/{record.Id}/comments"), UriKind.Relative))
            {
                Content = JsonContent.Create(new { markdown }, options: AgcoworkApiFactory.Json),
            };
            request.Headers.Add("Idempotency-Key", "comment-key-1");
            using var response = await owner.Client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<CommentDto>(AgcoworkApiFactory.Json, Ct))!;
        }

        var first = await SendAsync("Einmal");
        var second = await SendAsync("Nochmal");

        Assert.Equal(first.Id, second.Id);
        Assert.Single((await CommentsApi.ListAsync(owner.Client, slug, record.Id)).Threads);
    }

    [Fact]
    public async Task Activity_CommentEvents_WithTargetTitleAndDiff()
    {
        var (slug, owner, editor, record) = await ArrangeAsync();
        var root = await CommentsApi.AddAsync(owner.Client, slug, record.Id, $"Frage {CommentsApi.Mention(editor.ActorId)}");

        var page = (await owner.Client.GetFromJsonAsync<ActivityPage>(Path(slug, "/activity?types=comment.created"), AgcoworkApiFactory.Json, Ct))!;

        var created = Assert.Single(page.Items);
        Assert.Equal(("comment", root.Id, "Diskussion", record.CollectionId), (created.TargetType, created.TargetId, created.TargetTitle, created.CollectionId));
        Assert.Equal($"[\"{editor.ActorId}\"]", created.Diff["mentionedActorIds"]!.ToJsonString());
    }
}
