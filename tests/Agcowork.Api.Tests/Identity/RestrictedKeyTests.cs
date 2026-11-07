using System.Net;
using System.Net.Http.Json;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Identity.Actors;

namespace Agcowork.Api.Tests.Identity;

/// <summary>A key restricted to a workspace or to scopes carries none of the actor's instance scopes.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class RestrictedKeyTests(AgcoworkApiFactory factory)
{
    private static readonly string[] ReadOnly = ["read"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RestrictedKey_OfInstanceAdmin_NoInstanceRights_UnrestrictedKeyKeepsThem()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var agent = await factory.CreateAgentAsync(["instance:admin"], cancellationToken: Ct);
        using var restricted = await TenancyFixture.CreateKeyClientAsync(factory, agent.ActorId, new { workspaceId = t.Alpha.Id, scopes = ReadOnly });

        using var keyDenied = await restricted.PostAsJsonAsync($"/api/v1/actors/{agent.ActorId}/keys", new { }, Ct);
        var problem = await ProblemAssert.IsProblemAsync(keyDenied, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal("requires instance:admin", problem.GetProperty("detail").GetString());

        using var workspaceDenied = await restricted.PostAsJsonAsync("/api/v1/workspaces", new { slug = $"rk-{Guid.NewGuid():N}"[..15], name = "No" }, Ct);
        problem = await ProblemAssert.IsProblemAsync(workspaceDenied, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal("requires workspaces:create", problem.GetProperty("detail").GetString());

        var me = await restricted.GetFromJsonAsync<MeDto>("/api/v1/me", AgcoworkApiFactory.Json, Ct);
        Assert.Empty(me!.InstanceScopes);

        using var keyAllowed = await agent.Client.PostAsJsonAsync($"/api/v1/actors/{agent.ActorId}/keys", new { }, Ct);
        Assert.Equal(HttpStatusCode.Created, keyAllowed.StatusCode);
        using var workspaceAllowed = await agent.Client.PostAsJsonAsync("/api/v1/workspaces", new { slug = $"rk-{Guid.NewGuid():N}"[..15], name = "Yes" }, Ct);
        Assert.Equal(HttpStatusCode.Created, workspaceAllowed.StatusCode);
    }

    [Fact]
    public async Task ScopeOnlyRestrictedKey_NoInstanceRights()
    {
        var agent = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        using var restricted = await TenancyFixture.CreateKeyClientAsync(factory, agent.ActorId, new { scopes = ReadOnly });

        using var response = await restricted.PostAsJsonAsync("/api/v1/workspaces", new { slug = $"rk-{Guid.NewGuid():N}"[..15], name = "No" }, Ct);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }
}
