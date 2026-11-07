using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Agcowork.Api.Tests.Records;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Identity.Actors;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Agcowork.Api.Tests.Realtime;

/// <summary>
/// The realtime hub <c>/w/{slug}/hub</c> (AP-024) end to end: tenant resolution on connect, delivery of <c>activity</c> by the
/// dispatcher, isolation between workspaces, and connections that lose access.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class HubTests(AgcoworkApiFactory factory)
{
    private const string Password = "correct horse battery staple";
    private static readonly string[] WriteOnly = ["write"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Own workspace with an owner agent, a seeded collection and an editor agent: tests change memberships.</summary>
    private async Task<(Application.Workspaces.WorkspaceDto Workspace, TestAgent Owner, TestAgent Editor, Guid CollectionId)> ArrangeAsync()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "rt");
        var editor = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, editor.ActorId, "editor");
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        return (workspace, owner, editor, collection.Id);
    }

    [Fact]
    public async Task Hub_MemberWithRead_ReceivesActivityAfterCreateWithin2s_WithTargetTitle()
    {
        var (workspace, owner, editor, collectionId) = await ArrangeAsync();
        await using var hub = await HubClient.WithKey(factory, workspace.Slug, editor.Key).StartAsync();

        var watch = Stopwatch.StartNew();
        var record = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collectionId, "Live dabei");
        var activity = await hub.WaitForAsync(e => HubClient.Is(e, "record.created", record.Id), HubClient.Delivery);

        Assert.True(watch.Elapsed < HubClient.Delivery, $"Delivered after {watch.Elapsed}.");
        Assert.Equal("Live dabei", activity.GetProperty("targetTitle").GetString());
        Assert.Equal(owner.ActorId, activity.GetProperty("actorId").GetGuid());
        Assert.True(activity.GetProperty("seq").GetInt64() > 0);
        Assert.Equal(collectionId, activity.GetProperty("collectionId").GetGuid());
        Assert.Equal("pong", await hub.Connection.InvokeAsync<string>("Ping", Ct));
    }

    [Fact]
    public async Task Hub_SessionCookie_WebSocketsAndLongPolling_ReceiveActivity()
    {
        var (workspace, owner, _, collectionId) = await ArrangeAsync();
        var human = await CreateHumanAsync();
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, human.Id, "viewer");
        var cookie = await HubClient.SessionCookieAsync(factory, human.Email!, Password);
        await using var webSockets = await HubClient.WithCookie(factory, workspace.Slug, cookie).StartAsync();
        await using var longPolling = await HubClient.WithCookie(factory, workspace.Slug, cookie, HttpTransportType.LongPolling).StartAsync();

        var record = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collectionId, "Für Menschen");

        Assert.Equal("Für Menschen", (await webSockets.WaitForAsync(e => HubClient.Is(e, "record.created", record.Id))).GetProperty("targetTitle").GetString());
        Assert.Equal("Für Menschen", (await longPolling.WaitForAsync(e => HubClient.Is(e, "record.created", record.Id))).GetProperty("targetTitle").GetString());
    }

    [Fact]
    public async Task Hub_OutsiderUnknownSlugForeignRestrictedKey_Rejected404()
    {
        var t = await TenancyFixture.GetAsync(factory);

        await AssertRejectedAsync(HubClient.WithKey(factory, t.Alpha.Slug, t.Outsider.Key), HttpStatusCode.NotFound);
        await AssertRejectedAsync(HubClient.WithKey(factory, t.Beta.Slug, t.AlphaOwner.Key), HttpStatusCode.NotFound);
        await AssertRejectedAsync(HubClient.WithKey(factory, t.Beta.Slug, t.RestrictedToAlpha.Key), HttpStatusCode.NotFound);
        await AssertRejectedAsync(HubClient.WithKey(factory, $"missing-{Guid.NewGuid():N}"[..20], t.AlphaOwner.Key), HttpStatusCode.NotFound);
        await AssertRejectedAsync(HubClient.WithKey(factory, t.Alpha.Slug, "agck_invalid_key"), HttpStatusCode.Unauthorized);

        // Positive control: the restricted key in its own workspace.
        await using var own = await HubClient.WithKey(factory, t.Alpha.Slug, t.RestrictedToAlpha.Key).StartAsync();
    }

    [Fact]
    public async Task Hub_TransportEndpointsWithoutNegotiate_SameTenantChecks()
    {
        var t = await TenancyFixture.GetAsync(factory);

        // Negotiate and the transport URL both go through tenant resolution: a bare 404 without existence hints.
        await TenancyFixture.AssertNotFoundAsync(t.AlphaOwner.Client, HttpMethod.Post, $"{HubClient.HubPath(t.Beta.Slug)}/negotiate?negotiateVersion=1");
        await TenancyFixture.AssertNotFoundAsync(t.AlphaOwner.Client, HttpMethod.Get, HubClient.HubPath(t.Beta.Slug));
        await TenancyFixture.AssertNotFoundAsync(t.AlphaHuman.Client, HttpMethod.Get, HubClient.HubPath(t.Beta.Slug));
        await TenancyFixture.AssertNotFoundAsync(t.Outsider.Client, HttpMethod.Post, $"{HubClient.HubPath(t.Alpha.Slug)}/negotiate?negotiateVersion=1");
        using var anonymous = factory.CreateClientWithoutCookies();
        using var unauthenticated = await anonymous.PostAsync(new Uri($"{HubClient.HubPath(t.Alpha.Slug)}/negotiate?negotiateVersion=1", UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [Fact]
    public async Task Hub_KeyWithoutRead_Rejected403()
    {
        var (workspace, _, editor, _) = await ArrangeAsync();
        using var writeOnly = await TenancyFixture.CreateKeyClientAsync(factory, editor.ActorId, new { scopes = WriteOnly });

        await AssertRejectedAsync(HubClient.WithKey(factory, workspace.Slug, writeOnly.DefaultRequestHeaders.Authorization!.Parameter!), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Hub_MemberOfAlpha_ReceivesNothingFromBeta()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (_, alphaCollection) = await RecordsApi.SeedCollectionAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        var (_, betaCollection) = await RecordsApi.SeedCollectionAsync(t.BetaOwner.Client, t.Beta.Slug);
        // RestrictedToAlpha is an editor in both workspaces; its connection to Alpha must not see Beta either.
        await using var alpha = await HubClient.WithKey(factory, t.Alpha.Slug, t.AlphaOwner.Key).StartAsync();
        await using var restricted = await HubClient.WithKey(factory, t.Alpha.Slug, t.RestrictedToAlpha.Key).StartAsync();

        var beta = await RecordsApi.CreateInAsync(t.BetaOwner.Client, t.Beta.Slug, betaCollection.Id, "Geheim in Beta");
        var marker = await RecordsApi.CreateInAsync(t.AlphaOwner.Client, t.Alpha.Slug, alphaCollection.Id, "Marker in Alpha");

        // The Beta record committed first; had it leaked, it would have arrived before the marker.
        await alpha.WaitForAsync(e => HubClient.Is(e, "record.created", marker.Id));
        await restricted.WaitForAsync(e => HubClient.Is(e, "record.created", marker.Id));
        var received = alpha.Received.Concat(restricted.Received).ToList();
        Assert.NotEmpty(received);
        Assert.All(received, e => Assert.NotEqual(beta.Id, e.GetProperty("targetId").GetGuid()));
        Assert.DoesNotContain(received, e => e.TryGetProperty("collectionId", out var c) && c.GetGuid() == betaCollection.Id);
    }

    [Fact]
    public async Task Hub_MembershipRemoved_ConnectionClosed_NoFurtherEvents()
    {
        var (workspace, owner, editor, collectionId) = await ArrangeAsync();
        await using var hub = await HubClient.WithKey(factory, workspace.Slug, editor.Key).StartAsync();
        var before = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collectionId, "Vorher");
        await hub.WaitForAsync(e => HubClient.Is(e, "record.created", before.Id));

        using (var remove = await owner.Client.DeleteAsync(new Uri(TenancyFixture.Path(workspace.Slug, $"/members/{editor.ActorId}"), UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        }

        await hub.WaitClosedAsync();
        var after = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collectionId, "Nachher");
        await using var ownerHub = await HubClient.WithKey(factory, workspace.Slug, owner.Key).StartAsync();
        var marker = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collectionId, "Marker");
        await ownerHub.WaitForAsync(e => HubClient.Is(e, "record.created", marker.Id));

        var received = hub.Received;
        Assert.DoesNotContain(received, e => e.GetProperty("type").GetString() == "member.removed");
        Assert.DoesNotContain(received, e => e.GetProperty("targetId").GetGuid() == after.Id);
        await AssertRejectedAsync(HubClient.WithKey(factory, workspace.Slug, editor.Key), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Hub_ReadScopeRemovedKeyRevokedActorDeactivated_ConnectionClosedAtNextEvent()
    {
        var (workspace, owner, editor, collectionId) = await ArrangeAsync();
        var revoked = await factory.CreateAgentAsync(cancellationToken: Ct);
        var deactivated = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, revoked.ActorId, "viewer");
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, deactivated.ActorId, "viewer");
        await using var editorHub = await HubClient.WithKey(factory, workspace.Slug, editor.Key).StartAsync();
        await using var revokedHub = await HubClient.WithKey(factory, workspace.Slug, revoked.Key).StartAsync();
        await using var deactivatedHub = await HubClient.WithKey(factory, workspace.Slug, deactivated.Key).StartAsync();
        await using var ownerHub = await HubClient.WithKey(factory, workspace.Slug, owner.Key).StartAsync();

        using (var narrow = await owner.Client.PatchAsJsonAsync(TenancyFixture.Path(workspace.Slug, $"/members/{editor.ActorId}"), new { scopes = WriteOnly }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, narrow.StatusCode);
        }

        using var admin = await factory.CreateAdminClientAsync(Ct);
        using (var revoke = await admin.DeleteAsync(new Uri($"/api/v1/keys/{revoked.ApiKeyId}", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        using (var deactivate = await admin.PatchAsJsonAsync($"/api/v1/actors/{deactivated.ActorId}", new { isActive = false }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        }

        var marker = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collectionId, "Nach dem Entzug");
        await ownerHub.WaitForAsync(e => HubClient.Is(e, "record.created", marker.Id));

        await editorHub.WaitClosedAsync();
        await revokedHub.WaitClosedAsync();
        await deactivatedHub.WaitClosedAsync();
        Assert.All(
            editorHub.Received.Concat(revokedHub.Received).Concat(deactivatedHub.Received),
            e => Assert.NotEqual(marker.Id, e.GetProperty("targetId").GetGuid()));
    }

    [Fact]
    public async Task Hub_PasswordReset_SessionConnectionClosedAtNextEvent()
    {
        var (workspace, owner, _, collectionId) = await ArrangeAsync();
        var human = await CreateHumanAsync();
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, human.Id, "viewer");
        var cookie = await HubClient.SessionCookieAsync(factory, human.Email!, Password);
        await using var hub = await HubClient.WithCookie(factory, workspace.Slug, cookie).StartAsync();
        await using var ownerHub = await HubClient.WithKey(factory, workspace.Slug, owner.Key).StartAsync();

        using var admin = await factory.CreateAdminClientAsync(Ct);
        using (var reset = await admin.PutAsJsonAsync($"/api/v1/actors/{human.Id}/password", new { newPassword = "a-completely-new-password" }, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        var marker = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collectionId, "Nach dem Reset");
        await ownerHub.WaitForAsync(e => HubClient.Is(e, "record.created", marker.Id));

        await hub.WaitClosedAsync();
        Assert.DoesNotContain(hub.Received, e => e.GetProperty("targetId").GetGuid() == marker.Id);
    }

    [Fact]
    public async Task Hub_SessionCookie_ForeignOrMissingOrigin_Rejected403_BearerNeedsNoOrigin()
    {
        var (workspace, owner, _, _) = await ArrangeAsync();
        var human = await CreateHumanAsync();
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, human.Id, "viewer");
        var cookie = await HubClient.SessionCookieAsync(factory, human.Email!, Password);
        const string Evil = "https://evil.example";

        // Cross-site page opening the hub with the victim's cookie: negotiate and the WebSocket handshake are refused.
        await AssertRejectedAsync(HubClient.WithCookie(factory, workspace.Slug, cookie, origin: Evil), HttpStatusCode.Forbidden);
        await AssertRejectedAsync(HubClient.WithCookie(factory, workspace.Slug, cookie, origin: null), HttpStatusCode.Forbidden);
        var foreignSocket = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => HubClient.OpenRawWebSocketAsync(factory, workspace.Slug, ("Cookie", cookie), ("Origin", Evil)));
        var noOriginSocket = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => HubClient.OpenRawWebSocketAsync(factory, workspace.Slug, ("Cookie", cookie)));
        Assert.Contains("403", foreignSocket.Message, StringComparison.Ordinal);
        Assert.Contains("403", noOriginSocket.Message, StringComparison.Ordinal);

        // Positive controls: the own origin with the cookie, and bearer keys with or without (any) Origin.
        using (var sameOriginSocket = await HubClient.OpenRawWebSocketAsync(factory, workspace.Slug, ("Cookie", cookie), ("Origin", HubClient.SameOrigin)))
        {
            Assert.Equal(System.Net.WebSockets.WebSocketState.Open, sameOriginSocket.State);
            sameOriginSocket.Abort();
        }

        await using var session = await HubClient.WithCookie(factory, workspace.Slug, cookie).StartAsync();
        await using var agent = await HubClient.WithKey(factory, workspace.Slug, owner.Key).StartAsync();
        await using var agentWithOrigin = await HubClient.WithKey(factory, workspace.Slug, owner.Key, origin: Evil).StartAsync();
    }

    private static async Task AssertRejectedAsync(HubClient client, HttpStatusCode status)
    {
        await using (client)
        {
            var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.StartAsync());
            Assert.Equal(status, exception.StatusCode);
        }
    }

    private async Task<ActorDto> CreateHumanAsync()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        using var response = await admin.PostAsJsonAsync(
            "/api/v1/actors",
            new { kind = "human", name = "Realtime human", email = $"rt-{Guid.NewGuid():N}@example.com", password = Password },
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ActorDto>(AgcoworkApiFactory.Json, Ct))!;
    }
}
