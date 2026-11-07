using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Agcowork.Application.Identity.Actors;
using Microsoft.AspNetCore.Authentication;

namespace Agcowork.Api.Tests.Identity;

/// <summary>PATCH /me/password, PUT /actors/{id}/password, session invalidation and mustChangePassword (AP-022).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PasswordTests(AgcoworkApiFactory factory)
{
    private const string InitialPassword = "initial-password-123";
    private const string NewPassword = "brand-new-password-456";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ChangeMyPassword_Correct_204_OldPasswordRejected_NewAccepted()
    {
        var (_, email) = await CreateHumanAsync();
        using var session = await LoginAsync(email, InitialPassword);

        using var response = await ChangeAsync(session, InitialPassword, NewPassword);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var anonymous = factory.CreateClientWithoutCookies();
        using var old = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password = InitialPassword }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using var fresh = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password = NewPassword }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, fresh.StatusCode);
    }

    [Fact]
    public async Task ChangeMyPassword_CookieOfResponseWorks_OtherSessionOfSameActor401()
    {
        var (human, email) = await CreateHumanAsync();
        using var session = await LoginAsync(email, InitialPassword);
        using var stolen = await LoginAsync(email, InitialPassword);
        await AssertMeAsync(stolen, HttpStatusCode.OK);

        using (var response = await ChangeAsync(session, InitialPassword, NewPassword))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("agcowork_session=", StringComparison.Ordinal));
        }

        // Immediately after the change, i.e. typically within the same second and on Linux possibly the same microsecond.
        var me = await session.GetFromJsonAsync<MeDto>("/api/v1/me", AgcoworkApiFactory.Json, Ct);
        Assert.Equal(human.Id, me!.Id);
        await AssertMeAsync(stolen, HttpStatusCode.Unauthorized);
        await AssertMeAsync(session, HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChangeMyPassword_WrongCurrent_400InvalidCredentialsOnField()
    {
        var (_, email) = await CreateHumanAsync();
        using var session = await LoginAsync(email, InitialPassword);

        using var response = await ChangeAsync(session, "not-my-password", NewPassword);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("invalid_credentials", ProblemAssert.Field(problem, "currentPassword").GetProperty("code").GetString());
        await AssertMeAsync(session, HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChangeMyPassword_TooShort_400TooShort()
    {
        var (_, email) = await CreateHumanAsync();
        using var session = await LoginAsync(email, InitialPassword);

        using var response = await ChangeAsync(session, InitialPassword, "short");

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("too_short", ProblemAssert.Field(problem, "newPassword").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ChangeMyPassword_SamePassword_400Unchanged()
    {
        var (_, email) = await CreateHumanAsync();
        using var session = await LoginAsync(email, InitialPassword);

        using var response = await ChangeAsync(session, InitialPassword, InitialPassword);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("unchanged", ProblemAssert.Field(problem, "newPassword").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ChangeMyPassword_ApiKey_403WithDetail()
    {
        using var agent = await factory.CreateAgentAsync(cancellationToken: Ct);

        using var response = await ChangeAsync(agent.Client, InitialPassword, NewPassword);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.Contains("API keys", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangeMyPassword_Anonymous_401()
    {
        using var anonymous = factory.CreateClientWithoutCookies();

        using var response = await ChangeAsync(anonymous, InitialPassword, NewPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_Admin_204_AllSessionsOfTarget401_NewLoginMustChange()
    {
        var (human, email) = await CreateHumanAsync();
        using var first = await LoginAsync(email, InitialPassword);
        using var second = await LoginAsync(email, InitialPassword);
        using var admin = await factory.CreateAdminClientAsync(Ct);

        using var response = await admin.PutAsJsonAsync($"/api/v1/actors/{human.Id}/password", new { newPassword = NewPassword }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertMeAsync(first, HttpStatusCode.Unauthorized);
        await AssertMeAsync(second, HttpStatusCode.Unauthorized);
        await AssertMeAsync(admin, HttpStatusCode.OK);
        using var fresh = await LoginAsync(email, NewPassword);
        var me = await fresh.GetFromJsonAsync<MeDto>("/api/v1/me", AgcoworkApiFactory.Json, Ct);
        Assert.True(me!.MustChangePassword);

        // The human picks their own password: the flag is gone.
        using (var change = await ChangeAsync(fresh, NewPassword, "my-very-own-password"))
        {
            Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        }

        Assert.False((await fresh.GetFromJsonAsync<MeDto>("/api/v1/me", AgcoworkApiFactory.Json, Ct))!.MustChangePassword);
    }

    [Fact]
    public async Task ResetPassword_MustChangeFalse_FlagNotSet()
    {
        var (human, email) = await CreateHumanAsync();
        using var admin = await factory.CreateAdminClientAsync(Ct);

        using var response = await admin.PutAsJsonAsync($"/api/v1/actors/{human.Id}/password", new { newPassword = NewPassword, mustChange = false }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var session = await LoginAsync(email, NewPassword);
        Assert.False((await session.GetFromJsonAsync<MeDto>("/api/v1/me", AgcoworkApiFactory.Json, Ct))!.MustChangePassword);
    }

    [Fact]
    public async Task ResetPassword_HumanWithoutPassword_CreatesLogin()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        var email = $"human-{Guid.NewGuid():N}@agcowork.local";
        using var created = await admin.PostAsJsonAsync("/api/v1/actors", new { kind = "human", name = "Grace", email }, Ct);
        var human = (await created.Content.ReadFromJsonAsync<ActorDto>(AgcoworkApiFactory.Json, Ct))!;

        using var response = await admin.PutAsJsonAsync($"/api/v1/actors/{human.Id}/password", new { newPassword = NewPassword }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var session = await LoginAsync(email, NewPassword);
        Assert.True((await session.GetFromJsonAsync<MeDto>("/api/v1/me", AgcoworkApiFactory.Json, Ct))!.MustChangePassword);
    }

    [Fact]
    public async Task ResetPassword_NonAdminHuman_403()
    {
        var (human, email) = await CreateHumanAsync();
        using var session = await LoginAsync(email, InitialPassword);

        using var response = await session.PutAsJsonAsync($"/api/v1/actors/{human.Id}/password", new { newPassword = NewPassword }, Ct);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task ResetPassword_AgentWithoutAdminScope_403()
    {
        var (human, _) = await CreateHumanAsync();
        using var agent = await factory.CreateAgentAsync(cancellationToken: Ct);

        using var response = await agent.Client.PutAsJsonAsync($"/api/v1/actors/{human.Id}/password", new { newPassword = NewPassword }, Ct);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task ResetPassword_TargetIsAgent_400NotHuman()
    {
        using var agent = await factory.CreateAgentAsync(cancellationToken: Ct);
        using var admin = await factory.CreateAdminClientAsync(Ct);

        using var response = await admin.PutAsJsonAsync($"/api/v1/actors/{agent.ActorId}/password", new { newPassword = NewPassword }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("not_human", ProblemAssert.Field(problem, "actorId").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ResetPassword_UnknownId_404()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);

        using var response = await admin.PutAsJsonAsync($"/api/v1/actors/{Guid.CreateVersion7()}/password", new { newPassword = NewPassword }, Ct);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task GetMe_FreshBootstrapAdmin_MustChangePassword()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);

        var me = await admin.GetFromJsonAsync<MeDto>("/api/v1/me", AgcoworkApiFactory.Json, Ct);

        Assert.True(me!.MustChangePassword);
    }

    [Fact]
    public async Task GetMe_Agent_MustChangePasswordFalse()
    {
        using var agent = await factory.CreateAgentAsync(cancellationToken: Ct);

        var json = await agent.Client.GetStringAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);

        Assert.Contains("\"mustChangePassword\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticationPropertiesIssuedUtc_KeepsWholeSecondsOnly()
    {
        // Why the session cookie carries its own issue time: IssuedUtc cannot tell "just before" from "just after"
        // a password change within the same second.
        var issued = DateTimeOffset.Parse("2026-11-07T12:00:00.9876543Z", CultureInfo.InvariantCulture);

        var properties = new AuthenticationProperties { IssuedUtc = issued };

        Assert.Equal(DateTimeOffset.Parse("2026-11-07T12:00:00Z", CultureInfo.InvariantCulture), properties.IssuedUtc);
    }

    private async Task<(ActorDto Actor, string Email)> CreateHumanAsync()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        var email = $"human-{Guid.NewGuid():N}@agcowork.local";
        using var created = await admin.PostAsJsonAsync("/api/v1/actors", new { kind = "human", name = "Grace", email, password = InitialPassword }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return ((await created.Content.ReadFromJsonAsync<ActorDto>(AgcoworkApiFactory.Json, Ct))!, email);
    }

    private async Task<HttpClient> LoginAsync(string email, string password)
    {
        var client = factory.CreateClient();
        await AgcoworkApiFactory.LoginAsync(client, email, password, Ct);
        return client;
    }

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, string currentPassword, string newPassword) =>
        client.PatchAsJsonAsync("/api/v1/me/password", new { currentPassword, newPassword }, Ct);

    private static async Task AssertMeAsync(HttpClient client, HttpStatusCode expected)
    {
        using var response = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);
        Assert.Equal(expected, response.StatusCode);
    }
}
