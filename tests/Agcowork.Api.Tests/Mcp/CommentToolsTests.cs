using System.Text.Json;
using Agcowork.Api.Tests.Comments;
using Agcowork.Api.Tests.Records;
using Agcowork.Api.Tests.Tenancy;

namespace Agcowork.Api.Tests.Mcp;

/// <summary>The comment tools (AP-029) end to end over MCP: an agent asks, another one finds the question in its inbox and answers.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class CommentToolsTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Slug, TestAgent Owner, TestAgent Editor, Guid RecordId)> ArrangeAsync()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "cmt");
        var editor = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, editor.ActorId, "editor");
        var record = await CommentsApi.SeedRecordAsync(owner.Client, workspace.Slug, "Datenbank wählen");
        return (workspace.Slug, owner, editor, record.Id);
    }

    [Fact]
    public async Task Agents_AskMentionFindAnswerResolve_OverMcp()
    {
        var (slug, owner, editor, recordId) = await ArrangeAsync();
        await using var asker = await McpClientFixture.ConnectAsync(factory, owner.Key, slug);
        await using var answerer = await McpClientFixture.ConnectAsync(factory, editor.Key, slug);

        // The overview names the members with their actor ids, so the asker can mention.
        var overview = McpClientFixture.Text(await McpClientFixture.CallAsync(asker, "workspace_overview"));
        Assert.Contains($"actorId {editor.ActorId}", overview, StringComparison.Ordinal);
        Assert.Contains("mentions_list", overview, StringComparison.Ordinal);

        var question = McpClientFixture.Ok(await McpClientFixture.CallAsync(asker, "comment_add", new { recordId, markdown = $"Postgres oder Mongo, {CommentsApi.Mention(editor.ActorId, "Editor")}?" }));
        var questionId = question.GetProperty("id").GetGuid();

        var inbox = await McpClientFixture.CallAsync(answerer, "mentions_list", new { unreadOnly = true });
        var mention = Assert.Single(McpClientFixture.Ok(inbox).GetProperty("items").EnumerateArray());
        Assert.Equal(questionId, mention.GetProperty("commentId").GetGuid());
        Assert.Equal("Datenbank wählen", mention.GetProperty("recordTitle").GetString());
        Assert.Contains("UNREAD", McpClientFixture.Text(inbox), StringComparison.Ordinal);

        var answer = McpClientFixture.Ok(await McpClientFixture.CallAsync(answerer, "comment_add", new { recordId, markdown = "Postgres.", parentCommentId = questionId }));
        Assert.Equal(questionId, answer.GetProperty("parentCommentId").GetGuid());
        var marked = McpClientFixture.Ok(await McpClientFixture.CallAsync(answerer, "mentions_mark_read", new { mentionIds = new[] { mention.GetProperty("id").GetGuid() } }));
        Assert.Equal((1, 0), (marked.GetProperty("marked").GetInt32(), marked.GetProperty("unreadCount").GetInt32()));

        McpClientFixture.Ok(await McpClientFixture.CallAsync(asker, "comment_update", new { commentId = questionId, resolved = true }));
        var open = McpClientFixture.Ok(await McpClientFixture.CallAsync(asker, "comments_list", new { recordId, includeResolved = false }));
        Assert.Empty(open.GetProperty("threads").EnumerateArray());

        var all = await McpClientFixture.CallAsync(asker, "comments_list", new { recordId });
        var thread = Assert.Single(McpClientFixture.Ok(all).GetProperty("threads").EnumerateArray());
        Assert.Equal("Postgres.", Assert.Single(thread.GetProperty("replies").EnumerateArray()).GetProperty("markdown").GetString());
        Assert.Contains("RESOLVED", McpClientFixture.Text(all), StringComparison.Ordinal);

        // The record did not change.
        Assert.Equal(1, (await RecordsApi.GetAsync(owner.Client, slug, recordId)).Revision);
    }

    [Fact]
    public async Task RecordGet_IncludeComments_MarkdownAndJson()
    {
        var (slug, owner, _, recordId) = await ArrangeAsync();
        await using var client = await McpClientFixture.ConnectAsync(factory, owner.Key, slug);
        McpClientFixture.Ok(await McpClientFixture.CallAsync(client, "comment_add", new { recordId, markdown = "Wichtiger Hinweis" }));

        var markdown = await McpClientFixture.CallAsync(client, "record_get", new { recordId, includeComments = true });
        var json = McpClientFixture.Ok(await McpClientFixture.CallAsync(client, "record_get", new { recordId, format = "json", includeComments = true }));
        var plain = McpClientFixture.Ok(await McpClientFixture.CallAsync(client, "record_get", new { recordId }));

        Assert.Contains("Wichtiger Hinweis", McpClientFixture.Text(markdown), StringComparison.Ordinal);
        Assert.Single(McpClientFixture.Ok(markdown).GetProperty("comments").GetProperty("threads").EnumerateArray());
        Assert.Equal("Wichtiger Hinweis", json.GetProperty("comments").GetProperty("threads")[0].GetProperty("markdown").GetString());
        Assert.False(plain.TryGetProperty("comments", out _));
    }

    [Fact]
    public async Task CommentUpdate_EditDelete_Errors()
    {
        var (slug, owner, editor, recordId) = await ArrangeAsync();
        await using var ownerClient = await McpClientFixture.ConnectAsync(factory, owner.Key, slug);
        await using var editorClient = await McpClientFixture.ConnectAsync(factory, editor.Key, slug);
        var root = McpClientFixture.Ok(await McpClientFixture.CallAsync(ownerClient, "comment_add", new { recordId, markdown = "Wurzel" })).GetProperty("id").GetGuid();
        var reply = McpClientFixture.Ok(await McpClientFixture.CallAsync(editorClient, "comment_add", new { recordId, markdown = "Antwort", parentCommentId = root })).GetProperty("id").GetGuid();

        McpClientFixture.Error(await McpClientFixture.CallAsync(editorClient, "comment_update", new { commentId = root, markdown = "Fremd" }), 403, "not_author");
        McpClientFixture.Error(await McpClientFixture.CallAsync(editorClient, "comment_update", new { commentId = root, deleted = true }), 403, "not_author");
        McpClientFixture.Error(await McpClientFixture.CallAsync(ownerClient, "comment_update", new { commentId = reply, resolved = true }), 400, "validation_failed");
        McpClientFixture.Error(await McpClientFixture.CallAsync(ownerClient, "comment_add", new { recordId, markdown = "Tiefer", parentCommentId = reply }), 400, "validation_failed");
        McpClientFixture.Error(await McpClientFixture.CallAsync(ownerClient, "comment_update", new { commentId = root, deleted = false }), 400, "validation_failed");
        McpClientFixture.Error(await McpClientFixture.CallAsync(ownerClient, "comment_update", new { commentId = root, deleted = true, resolved = true }), 400, "validation_failed");

        var edited = McpClientFixture.Ok(await McpClientFixture.CallAsync(ownerClient, "comment_update", new { commentId = root, markdown = "Wurzel (neu)", resolved = true }));
        Assert.Equal(JsonValueKind.String, edited.GetProperty("editedAt").ValueKind);
        Assert.Equal(JsonValueKind.String, edited.GetProperty("resolvedAt").ValueKind);

        // The owner (admin scope) deletes the editor's reply.
        var deleted = McpClientFixture.Ok(await McpClientFixture.CallAsync(ownerClient, "comment_update", new { commentId = reply, deleted = true }));
        Assert.True(deleted.GetProperty("deleted").GetBoolean());
        var thread = Assert.Single(McpClientFixture.Ok(await McpClientFixture.CallAsync(ownerClient, "comments_list", new { recordId })).GetProperty("threads").EnumerateArray());
        Assert.Empty(thread.GetProperty("replies").EnumerateArray());
    }

    [Fact]
    public async Task CommentAdd_NotMember_IdempotencyKey_Archived()
    {
        var (slug, owner, _, recordId) = await ArrangeAsync();
        var outsider = await factory.CreateAgentAsync(cancellationToken: Ct);
        await using var client = await McpClientFixture.ConnectAsync(factory, owner.Key, slug);

        var notMember = McpClientFixture.Error(await McpClientFixture.CallAsync(client, "comment_add", new { recordId, markdown = $"Hallo {CommentsApi.Mention(outsider.ActorId)}" }), 400, "validation_failed");
        Assert.Equal("not_member", notMember.GetProperty("errors")[0].GetProperty("code").GetString());

        var first = McpClientFixture.Ok(await McpClientFixture.CallAsync(client, "comment_add", new { recordId, markdown = "Einmal", idempotencyKey = "mcp-key" }));
        var second = McpClientFixture.Ok(await McpClientFixture.CallAsync(client, "comment_add", new { recordId, markdown = "Zweimal", idempotencyKey = "mcp-key" }));
        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        var invalidKey = McpClientFixture.Error(await McpClientFixture.CallAsync(client, "comment_add", new { recordId, markdown = "x", idempotencyKey = new string('k', 300) }), 400, "validation_failed");
        Assert.Equal("idempotencyKey", invalidKey.GetProperty("errors")[0].GetProperty("field").GetString());

        McpClientFixture.Ok(await McpClientFixture.CallAsync(client, "record_archive", new { recordId }));
        McpClientFixture.Error(await McpClientFixture.CallAsync(client, "comment_add", new { recordId, markdown = "Zu spät" }), 409, "record_archived");
        Assert.Single(McpClientFixture.Ok(await McpClientFixture.CallAsync(client, "comments_list", new { recordId })).GetProperty("threads").EnumerateArray());
    }
}
