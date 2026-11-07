using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Agcowork.Application.Identity.Actors;
using Agcowork.Application.Identity.ApiKeys;
using Agcowork.Domain.Identity;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agcowork.Api.Tests.Identity;

[Collection(ApiTestGroup.Name)]
public sealed class ApiKeysEndpointsTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PostKeys_Admin_201WithPlainTextOnceAndOnlyHashInDatabase()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        var agent = await CreateAgentActorAsync(admin);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30);

        using var response = await admin.PostAsJsonAsync(
            $"/api/v1/actors/{agent.Id}/keys",
            new { scopes = new[] { KeyScope.Read }, expiresAt },
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreatedApiKeyDto>(AgcoworkApiFactory.Json, Ct))!;
        Assert.True(ApiKeyToken.TryParse(created.Key, out var prefix, out var secret));
        Assert.Equal(prefix, created.ApiKey.Prefix);
        Assert.Equal([KeyScope.Read], created.ApiKey.Scopes);

        // Database: neither token nor secret appear in any column; the hash matches the secret.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();
            var row = await db.Database
                .SqlQuery<string>($"SELECT row_to_json(k)::text AS \"Value\" FROM api_keys k WHERE k.id = {created.ApiKey.Id}")
                .SingleAsync(Ct);
            Assert.DoesNotContain(secret, row, StringComparison.Ordinal);
            var stored = await db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == created.ApiKey.Id, Ct);
            Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), stored.SecretHash);
        }

        // Listing shows metadata only.
        using var list = await admin.GetAsync(new Uri($"/api/v1/actors/{agent.Id}/keys", UriKind.Relative), Ct);
        var listJson = await list.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains(prefix, listJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, listJson, StringComparison.Ordinal);
        Assert.DoesNotContain("hash", listJson, StringComparison.OrdinalIgnoreCase);
        var keys = (await list.Content.ReadFromJsonAsync<List<ApiKeyDto>>(AgcoworkApiFactory.Json, Ct))!;
        Assert.Equal(created.ApiKey.Id, Assert.Single(keys).Id);
    }

    [Fact]
    public async Task DeleteKey_ThenSameKey_401AndListedAsRevoked()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        using var agent = await factory.CreateAgentAsync(cancellationToken: Ct);
        using (var before = await agent.Client.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using var revoke = await admin.DeleteAsync(new Uri($"/api/v1/keys/{agent.ApiKeyId}", UriKind.Relative), Ct);
        using var after = await agent.Client.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);
        using var again = await admin.DeleteAsync(new Uri($"/api/v1/keys/{agent.ApiKeyId}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        await ProblemAssert.IsProblemAsync(after, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        var keys = await admin.GetFromJsonAsync<List<ApiKeyDto>>($"/api/v1/actors/{agent.ActorId}/keys", AgcoworkApiFactory.Json, Ct);
        Assert.NotNull(Assert.Single(keys!).RevokedAt);
    }

    [Fact]
    public async Task GetMe_KeyExpiredInThePast_401()
    {
        using var agent = await factory.CreateAgentAsync(keyOptions: new { expiresAt = DateTimeOffset.UtcNow.AddHours(1) }, cancellationToken: Ct);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();
            var past = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.ApiKeys.Where(k => k.Id == agent.ApiKeyId).ExecuteUpdateAsync(s => s.SetProperty(k => k.ExpiresAt, past), Ct);
        }

        using var response = await agent.Client.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task GetMe_KeyOfDeactivatedAgent_401()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        using var agent = await factory.CreateAgentAsync(cancellationToken: Ct);

        using var deactivate = await admin.PatchAsJsonAsync($"/api/v1/actors/{agent.ActorId}", new { isActive = false }, Ct);
        using var response = await agent.Client.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task GetMe_WithKey_SetsLastUsedAt()
    {
        using var agent = await factory.CreateAgentAsync(cancellationToken: Ct);

        using var response = await agent.Client.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();
        var lastUsedAt = await db.ApiKeys.AsNoTracking().Where(k => k.Id == agent.ApiKeyId).Select(k => k.LastUsedAt).SingleAsync(Ct);
        Assert.NotNull(lastUsedAt);
        Assert.InRange(lastUsedAt.Value, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task PostKeys_ForHuman_400OnActorId()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        var me = (await admin.GetFromJsonAsync<MeDto>("/api/v1/me", AgcoworkApiFactory.Json, Ct))!;

        using var response = await admin.PostAsJsonAsync($"/api/v1/actors/{me.Id}/keys", new { }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("not_allowed", ProblemAssert.Field(problem, "actorId").GetProperty("code").GetString());
    }

    [Fact]
    public async Task PostKeys_WithoutBody_201()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        var agent = await CreateAgentActorAsync(admin);

        using var response = await admin.PostAsync(new Uri($"/api/v1/actors/{agent.Id}/keys", UriKind.Relative), null, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task KeyEndpoints_UnknownIds_404()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        var unknown = Guid.CreateVersion7();

        using var create = await admin.PostAsJsonAsync($"/api/v1/actors/{unknown}/keys", new { }, Ct);
        using var list = await admin.GetAsync(new Uri($"/api/v1/actors/{unknown}/keys", UriKind.Relative), Ct);
        using var revoke = await admin.DeleteAsync(new Uri($"/api/v1/keys/{unknown}", UriKind.Relative), Ct);

        await ProblemAssert.IsProblemAsync(create, HttpStatusCode.NotFound, "not_found");
        await ProblemAssert.IsProblemAsync(list, HttpStatusCode.NotFound, "not_found");
        await ProblemAssert.IsProblemAsync(revoke, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task KeyEndpoints_AgentWithoutInstanceAdmin_403()
    {
        using var agent = await factory.CreateAgentAsync(cancellationToken: Ct);

        using var create = await agent.Client.PostAsJsonAsync($"/api/v1/actors/{agent.ActorId}/keys", new { }, Ct);
        using var list = await agent.Client.GetAsync(new Uri($"/api/v1/actors/{agent.ActorId}/keys", UriKind.Relative), Ct);
        using var revoke = await agent.Client.DeleteAsync(new Uri($"/api/v1/keys/{agent.ApiKeyId}", UriKind.Relative), Ct);

        await ProblemAssert.IsProblemAsync(create, HttpStatusCode.Forbidden, "forbidden");
        await ProblemAssert.IsProblemAsync(list, HttpStatusCode.Forbidden, "forbidden");
        await ProblemAssert.IsProblemAsync(revoke, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task KeyEndpoints_AgentWithInstanceAdmin_CanManageKeys()
    {
        using var adminAgent = await factory.CreateAgentAsync([InstanceScope.Admin], cancellationToken: Ct);

        using var response = await adminAgent.Client.PostAsJsonAsync($"/api/v1/actors/{adminAgent.ActorId}/keys", new { }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<ActorDto> CreateAgentActorAsync(HttpClient admin)
    {
        using var response = await admin.PostAsJsonAsync("/api/v1/actors", new { kind = "agent", name = $"agent-{Guid.NewGuid():N}" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ActorDto>(AgcoworkApiFactory.Json, Ct))!;
    }
}
