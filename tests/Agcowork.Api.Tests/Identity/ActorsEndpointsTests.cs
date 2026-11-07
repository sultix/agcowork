using System.Net;
using System.Net.Http.Json;
using Agcowork.Application.Identity.Actors;
using Agcowork.Domain.Identity;

namespace Agcowork.Api.Tests.Identity;

[Collection(ApiTestGroup.Name)]
public sealed class ActorsEndpointsTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PostActors_AdminCreatesAgent_201WithLocationAndReadable()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);

        using var response = await admin.PostAsJsonAsync(
            "/api/v1/actors",
            new { kind = "agent", name = "Claude", role = "Reviewer", instanceScopes = new[] { InstanceScope.WorkspacesCreate } },
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ActorDto>(AgcoworkApiFactory.Json, Ct))!;
        Assert.Equal(ActorKind.Agent, created.Kind);
        Assert.Equal("Reviewer", created.Role);
        Assert.Equal($"/api/v1/actors/{created.Id}", response.Headers.Location?.OriginalString);

        var loaded = (await admin.GetFromJsonAsync<ActorDto>($"/api/v1/actors/{created.Id}", AgcoworkApiFactory.Json, Ct))!;
        // Records compare lists by reference; compare them separately.
        Assert.Equal(created, loaded with { InstanceScopes = created.InstanceScopes });
        Assert.Equal(created.InstanceScopes, loaded.InstanceScopes);

        var all = await admin.GetFromJsonAsync<List<ActorDto>>("/api/v1/actors", AgcoworkApiFactory.Json, Ct);
        Assert.Contains(all!, a => a.Id == created.Id);
    }

    [Fact]
    public async Task PostActors_AgentWithoutInstanceAdmin_403ProblemDetails()
    {
        using var agent = await factory.CreateAgentClientAsync(InstanceScope.WorkspacesCreate);

        using var response = await agent.PostAsJsonAsync("/api/v1/actors", new { kind = "agent", name = "Sneaky" }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal("requires instance:admin", problem.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("GET", "/api/v1/actors")]
    [InlineData("GET", "/api/v1/actors/0193f0a0-0000-7000-8000-000000000000")]
    [InlineData("PATCH", "/api/v1/actors/0193f0a0-0000-7000-8000-000000000000")]
    public async Task ActorEndpoints_AgentWithoutInstanceAdmin_403(string method, string path)
    {
        using var agent = await factory.CreateAgentClientAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { name = "x" }) };

        using var response = await agent.SendAsync(request, Ct);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task GetActor_UnknownId_404WithoutDetail()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);

        using var response = await admin.GetAsync(new Uri($"/api/v1/actors/{Guid.CreateVersion7()}", UriKind.Relative), Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound, "not_found");
        Assert.False(problem.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task PatchActor_ChangesGivenFieldsOnly()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        using var agent = await factory.CreateAgentAsync(cancellationToken: Ct);

        using var response = await admin.PatchAsJsonAsync(
            $"/api/v1/actors/{agent.ActorId}",
            new { role = "Tech-Lead", instanceScopes = new[] { InstanceScope.WorkspacesCreate } },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<ActorDto>(AgcoworkApiFactory.Json, Ct))!;
        Assert.Equal("Tech-Lead", updated.Role);
        Assert.Equal([InstanceScope.WorkspacesCreate], updated.InstanceScopes);
        Assert.StartsWith("agent-", updated.Name, StringComparison.Ordinal);
        Assert.True(updated.IsActive);
    }

    [Fact]
    public async Task PatchActor_AdminDeactivatesSelf_409SelfLockout()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        var me = (await admin.GetFromJsonAsync<MeDto>("/api/v1/me", AgcoworkApiFactory.Json, Ct))!;

        using var response = await admin.PatchAsJsonAsync($"/api/v1/actors/{me.Id}", new { isActive = false }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "self_lockout");
        Assert.Equal("conflict", ProblemAssert.Field(problem, "isActive").GetProperty("code").GetString());
    }

    [Fact]
    public async Task PostActors_DuplicateEmail_409EmailTaken()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);

        using var response = await admin.PostAsJsonAsync("/api/v1/actors", new { kind = "human", name = "Copy", email = factory.AdminEmail.ToUpperInvariant() }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "email_taken");
        Assert.Equal("conflict", ProblemAssert.Field(problem, "email").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PostActors_AgentWithBlankEmail_201WithoutEmail(string email)
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);

        using var response = await admin.PostAsJsonAsync("/api/v1/actors", new { kind = "agent", name = "Blank", email }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ActorDto>(AgcoworkApiFactory.Json, Ct))!;
        Assert.Null(created.Email);
    }

    [Fact]
    public async Task PostActors_SameEmailInParallel_One201AndOne409EmailTaken()
    {
        using var first = await factory.CreateAdminClientAsync(Ct);
        using var second = await factory.CreateAdminClientAsync(Ct);
        var body = new { kind = "human", name = "Twin", email = $"twin-{Guid.NewGuid():N}@agcowork.local" };

        var responses = await Task.WhenAll(
            first.PostAsJsonAsync("/api/v1/actors", body, Ct),
            second.PostAsJsonAsync("/api/v1/actors", body, Ct));

        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            var conflict = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
            await ProblemAssert.IsProblemAsync(conflict, HttpStatusCode.Conflict, "email_taken");
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task PatchActor_UnknownInstanceScope_400WithAllowed()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        using var agent = await factory.CreateAgentAsync(cancellationToken: Ct);

        using var response = await admin.PatchAsJsonAsync($"/api/v1/actors/{agent.ActorId}", new { instanceScopes = UnknownScopes }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        var field = ProblemAssert.Field(problem, "instanceScopes[0]");
        Assert.Equal("not_in_options", field.GetProperty("code").GetString());
        Assert.Equal(InstanceScope.All, field.GetProperty("allowed").EnumerateArray().Select(v => v.GetString()!));
    }

    private static readonly string[] UnknownScopes = ["instance:root"];
}
