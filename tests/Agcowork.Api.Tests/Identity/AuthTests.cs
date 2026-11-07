using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agcowork.Application.Identity.Actors;
using Agcowork.Domain.Identity;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agcowork.Api.Tests.Identity;

[Collection(ApiTestGroup.Name)]
public sealed class AuthTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Startup_DevConfiguration_BootstrapAdminExists()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();

        var admin = await db.Actors.AsNoTracking().SingleAsync(a => a.Email == factory.AdminEmail, Ct);

        Assert.Equal(ActorKind.Human, admin.Kind);
        Assert.Contains(InstanceScope.Admin, admin.InstanceScopes);
        Assert.True(await db.LocalLogins.AnyAsync(l => l.ActorId == admin.Id, Ct));
    }

    [Fact]
    public async Task Login_BootstrapAdmin_Returns204WithHttpOnlyLaxSessionCookie()
    {
        using var client = factory.CreateClientWithoutCookies();

        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = factory.AdminEmail, password = factory.AdminPassword }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("agcowork_session=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        // The test server runs on http://localhost, where the cookie is deliberately not Secure.
        Assert.DoesNotContain("secure", cookie.Replace("samesite", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetMe_AdminCookie_ReturnsInstanceAdmin()
    {
        using var client = await factory.CreateAdminClientAsync(Ct);

        var me = await client.GetFromJsonAsync<MeDto>("/api/v1/me", AgcoworkApiFactory.Json, Ct);

        Assert.NotNull(me);
        Assert.Equal(ActorKind.Human, me.Kind);
        Assert.Equal(factory.AdminEmail, me.Email);
        Assert.Contains(InstanceScope.Admin, me.InstanceScopes);
        Assert.Null(me.ApiKey);
    }

    [Fact]
    public async Task GetMe_AgentKey_ReturnsAgentAndKeyMetadata()
    {
        using var agent = await factory.CreateAgentAsync([InstanceScope.WorkspacesCreate], cancellationToken: Ct);

        var me = await agent.Client.GetFromJsonAsync<MeDto>("/api/v1/me", AgcoworkApiFactory.Json, Ct);

        Assert.NotNull(me);
        Assert.Equal(agent.ActorId, me.Id);
        Assert.Equal(ActorKind.Agent, me.Kind);
        Assert.Equal([InstanceScope.WorkspacesCreate], me.InstanceScopes);
        Assert.NotNull(me.ApiKey);
        Assert.Equal(agent.ApiKeyId, me.ApiKey.Id);
        Assert.StartsWith($"agck_{me.ApiKey.Prefix}_", agent.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMe_KindIsSnakeCaseString()
    {
        using var agent = await factory.CreateAgentAsync(cancellationToken: Ct);

        var json = await agent.Client.GetStringAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);

        Assert.Contains("\"kind\":\"agent\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_WrongPassword_401WithoutRevealingEmailExistence()
    {
        using var client = factory.CreateClientWithoutCookies();

        using var wrongPassword = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = factory.AdminEmail, password = "definitely-wrong" }, Ct);
        using var unknownEmail = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "nobody@agcowork.local", password = "definitely-wrong" }, Ct);

        var first = await ProblemAssert.IsProblemAsync(wrongPassword, HttpStatusCode.Unauthorized, "unauthorized");
        var second = await ProblemAssert.IsProblemAsync(unknownEmail, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.False(first.TryGetProperty("detail", out _));
        Assert.Equal(ProblemAssert.PropertyNames(first), ProblemAssert.PropertyNames(second));
        Assert.False(wrongPassword.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("agck_notreal")]
    [InlineData("agck_AAAAAAAA_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("something-else")]
    public async Task GetMe_InvalidKey_401(string token)
    {
        using var client = factory.CreateClientWithoutCookies();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task GetMe_Anonymous_401()
    {
        using var client = factory.CreateClientWithoutCookies();

        using var response = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Logout_ThenGetMe_401()
    {
        using var client = await factory.CreateAdminClientAsync(Ct);

        using var logout = await client.PostAsync(new Uri("/api/v1/auth/logout", UriKind.Relative), null, Ct);
        using var me = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task GetMe_SessionOfDeactivatedHuman_401Immediately()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        var email = $"human-{Guid.NewGuid():N}@agcowork.local";
        const string password = "a-long-enough-password";
        using var created = await admin.PostAsJsonAsync("/api/v1/actors", new { kind = "human", name = "Grace", email, password }, Ct);
        var human = (await created.Content.ReadFromJsonAsync<ActorDto>(AgcoworkApiFactory.Json, Ct))!;

        using var session = factory.CreateClient();
        await AgcoworkApiFactory.LoginAsync(session, email, password, Ct);
        using (var before = await session.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using var deactivate = await admin.PatchAsJsonAsync($"/api/v1/actors/{human.Id}", new { isActive = false }, Ct);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        using var after = await session.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Login_AsHumanWithoutAdmin_MeShowsNoInstanceScopes()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        var email = $"human-{Guid.NewGuid():N}@agcowork.local";
        const string password = "a-long-enough-password";
        using var created = await admin.PostAsJsonAsync("/api/v1/actors", new { kind = "human", name = "Grace", email, password }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var session = factory.CreateClient();
        await AgcoworkApiFactory.LoginAsync(session, email.ToUpperInvariant(), password, Ct);
        var me = await session.GetFromJsonAsync<MeDto>("/api/v1/me", AgcoworkApiFactory.Json, Ct);

        Assert.NotNull(me);
        Assert.Empty(me.InstanceScopes);
    }
}
