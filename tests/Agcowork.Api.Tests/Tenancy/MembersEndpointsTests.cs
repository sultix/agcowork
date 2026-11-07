using System.Net;
using System.Net.Http.Json;
using Agcowork.Api.Tests.Identity;
using Agcowork.Application.Workspaces;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;

namespace Agcowork.Api.Tests.Tenancy;

[Collection(ApiTestGroup.Name)]
public sealed class MembersEndpointsTests(AgcoworkApiFactory factory)
{
    private static readonly string[] ReadSchema = ["read", "schema"];
    private static readonly string[] AdminRead = ["admin", "read"];
    private static readonly string[] AdminReadWrite = ["admin", "read", "write"];
    private static readonly string[] AdminOnly = ["admin"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AddUpdateRemove_Owner_FullLifecycle()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var agent = await factory.CreateAgentAsync(cancellationToken: Ct);

        var added = await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, agent.ActorId, "editor");
        Assert.Equal(WorkspaceRole.Editor, added.Role);
        Assert.Equal(["read", "write"], added.Scopes);
        Assert.Equal(ActorKind.Agent, added.Kind);
        Assert.Equal(owner.ActorId, added.CreatedBy);

        using var update = await owner.Client.PatchAsJsonAsync(Members(workspace, agent.ActorId), new { role = "admin", scopes = ReadSchema }, Ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<MemberDto>(AgcoworkApiFactory.Json, Ct))!;
        Assert.Equal(WorkspaceRole.Admin, updated.Role);
        Assert.Equal(["read", "schema"], updated.Scopes);

        // The new rights apply on the member's next request.
        var seen = await agent.Client.GetFromJsonAsync<WorkspaceDto>(TenancyFixture.Path(workspace.Slug, ""), AgcoworkApiFactory.Json, Ct);
        Assert.Equal(["read", "schema"], seen!.Scopes);

        using var remove = await owner.Client.DeleteAsync(new Uri(Members(workspace, agent.ActorId), UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);

        var members = await ListAsync(owner.Client, workspace);
        Assert.DoesNotContain(members, m => m.ActorId == agent.ActorId);
        await TenancyFixture.AssertNotFoundAsync(agent.Client, HttpMethod.Get, TenancyFixture.Path(workspace.Slug, ""));
    }

    [Fact]
    public async Task List_OwnersFirstWithNames()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var viewer = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, viewer.ActorId, "viewer");

        var members = await ListAsync(viewer.Client, workspace);

        Assert.Equal([owner.ActorId, viewer.ActorId], members.Select(m => m.ActorId));
        Assert.All(members, m => Assert.False(string.IsNullOrWhiteSpace(m.Name)));
    }

    [Fact]
    public async Task Admin_CanManageEditorsButNotOwners()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var admin = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, admin.ActorId, "admin");
        var newcomer = await factory.CreateAgentAsync(cancellationToken: Ct);

        await TenancyFixture.AddMemberAsync(admin.Client, workspace.Slug, newcomer.ActorId, "editor");

        using var addOwner = await admin.Client.PostAsJsonAsync(
            TenancyFixture.Path(workspace.Slug, "/members"), new { actorId = (await factory.CreateAgentAsync(cancellationToken: Ct)).ActorId, role = "owner" }, Ct);
        var problem = await ProblemAssert.IsProblemAsync(addOwner, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal("requires role owner", problem.GetProperty("detail").GetString());

        using var promote = await admin.Client.PatchAsJsonAsync(Members(workspace, newcomer.ActorId), new { role = "owner" }, Ct);
        await ProblemAssert.IsProblemAsync(promote, HttpStatusCode.Forbidden, "forbidden");

        using var demoteOwner = await admin.Client.PatchAsJsonAsync(Members(workspace, owner.ActorId), new { role = "viewer" }, Ct);
        await ProblemAssert.IsProblemAsync(demoteOwner, HttpStatusCode.Forbidden, "forbidden");

        using var removeOwner = await admin.Client.DeleteAsync(new Uri(Members(workspace, owner.ActorId), UriKind.Relative), Ct);
        await ProblemAssert.IsProblemAsync(removeOwner, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Editor_CannotManageMembers()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var editor = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, editor.ActorId, "editor");
        var newcomer = await factory.CreateAgentAsync(cancellationToken: Ct);

        using var response = await editor.Client.PostAsJsonAsync(TenancyFixture.Path(workspace.Slug, "/members"), new { actorId = newcomer.ActorId, role = "viewer" }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal("requires admin", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Owner_CanAddOwnerAndThenStepDown()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var second = await factory.CreateAgentAsync(cancellationToken: Ct);

        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, second.ActorId, "owner");
        using var stepDown = await owner.Client.PatchAsJsonAsync(Members(workspace, owner.ActorId), new { role = "editor" }, Ct);

        Assert.Equal(HttpStatusCode.OK, stepDown.StatusCode);
        var members = await ListAsync(second.Client, workspace);
        Assert.Equal(WorkspaceRole.Owner, Assert.Single(members, m => m.ActorId == second.ActorId).Role);
        Assert.Equal(WorkspaceRole.Editor, Assert.Single(members, m => m.ActorId == owner.ActorId).Role);
    }

    [Fact]
    public async Task LastOwner_CannotBeDemotedOrRemoved()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();

        using var demote = await owner.Client.PatchAsJsonAsync(Members(workspace, owner.ActorId), new { role = "admin" }, Ct);
        var problem = await ProblemAssert.IsProblemAsync(demote, HttpStatusCode.Conflict, "last_owner");
        Assert.Equal("conflict", ProblemAssert.Field(problem, "role").GetProperty("code").GetString());

        using var remove = await owner.Client.DeleteAsync(new Uri(Members(workspace, owner.ActorId), UriKind.Relative), Ct);
        await ProblemAssert.IsProblemAsync(remove, HttpStatusCode.Conflict, "last_owner");

        Assert.Equal(WorkspaceRole.Owner, Assert.Single(await ListAsync(owner.Client, workspace)).Role);
    }

    [Fact]
    public async Task Add_ExistingMember_Conflict()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();

        using var response = await owner.Client.PostAsJsonAsync(TenancyFixture.Path(workspace.Slug, "/members"), new { actorId = owner.ActorId, role = "viewer" }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "already_member");
        Assert.Equal("conflict", ProblemAssert.Field(problem, "actorId").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Add_InvalidInput_ValidationErrors()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();

        using var missing = await owner.Client.PostAsJsonAsync(TenancyFixture.Path(workspace.Slug, "/members"), new { role = "boss" }, Ct);
        var problem = await ProblemAssert.IsProblemAsync(missing, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("required", ProblemAssert.Field(problem, "actorId").GetProperty("code").GetString());
        var role = ProblemAssert.Field(problem, "role");
        Assert.Equal("not_in_options", role.GetProperty("code").GetString());
        Assert.Equal(["owner", "admin", "editor", "viewer"], role.GetProperty("allowed").EnumerateArray().Select(v => v.GetString()));

        using var unknown = await owner.Client.PostAsJsonAsync(TenancyFixture.Path(workspace.Slug, "/members"), new { actorId = Guid.CreateVersion7(), role = "viewer" }, Ct);
        problem = await ProblemAssert.IsProblemAsync(unknown, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("not_found", ProblemAssert.Field(problem, "actorId").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Update_UnknownMember_NotFound()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();

        await TenancyFixture.AssertNotFoundAsync(owner.Client, HttpMethod.Patch, Members(workspace, Guid.CreateVersion7()), new { role = "viewer" });
        await TenancyFixture.AssertNotFoundAsync(owner.Client, HttpMethod.Delete, Members(workspace, Guid.CreateVersion7()));
    }

    [Fact]
    public async Task Add_DeactivatedActor_ValidationErrorActorInactive()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var agent = await factory.CreateAgentAsync(cancellationToken: Ct);
        using (var admin = await factory.CreateAdminClientAsync(Ct))
        {
            using var deactivate = await admin.PatchAsJsonAsync($"/api/v1/actors/{agent.ActorId}", new { isActive = false }, Ct);
            Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        }

        using var response = await owner.Client.PostAsJsonAsync(TenancyFixture.Path(workspace.Slug, "/members"), new { actorId = agent.ActorId, role = "viewer" }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("actor_inactive", ProblemAssert.Field(problem, "actorId").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UpdateSelf_Widening_ConflictSelfEscalation_NarrowingAndLeavingAllowed()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var admin = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, admin.ActorId, "admin", AdminRead);
        var self = Members(workspace, admin.ActorId);

        using var addScope = await admin.Client.PatchAsJsonAsync(self, new { scopes = AdminReadWrite }, Ct);
        var problem = await ProblemAssert.IsProblemAsync(addScope, HttpStatusCode.Conflict, "self_escalation");
        Assert.Equal("conflict", ProblemAssert.Field(problem, "scopes").GetProperty("code").GetString());

        // Re-applying the role would reset to its defaults, i.e. widen the narrowed membership.
        using var resetRole = await admin.Client.PatchAsJsonAsync(self, new { role = "admin" }, Ct);
        problem = await ProblemAssert.IsProblemAsync(resetRole, HttpStatusCode.Conflict, "self_escalation");
        Assert.Equal("conflict", ProblemAssert.Field(problem, "role").GetProperty("code").GetString());

        using var toOwner = await admin.Client.PatchAsJsonAsync(self, new { role = "owner" }, Ct);
        await ProblemAssert.IsProblemAsync(toOwner, HttpStatusCode.Conflict, "self_escalation");

        using var narrow = await admin.Client.PatchAsJsonAsync(self, new { scopes = AdminOnly }, Ct);
        Assert.Equal(HttpStatusCode.OK, narrow.StatusCode);
        Assert.Equal(["admin"], (await narrow.Content.ReadFromJsonAsync<MemberDto>(AgcoworkApiFactory.Json, Ct))!.Scopes);

        using var leave = await admin.Client.DeleteAsync(new Uri(self, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);
    }

    [Fact]
    public async Task UpdateSelf_OwnerDemotesSelfWithSecondOwner_Allowed()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var second = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, second.ActorId, "owner");

        using var response = await owner.Client.PatchAsJsonAsync(Members(workspace, owner.ActorId), new { role = "viewer" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ConcurrentSelfDemotion_TwoOwners_ExactlyOneSucceedsAndOneOwnerRemains()
    {
        for (var round = 0; round < 3; round++)
        {
            var (first, second, workspace) = await TwoOwnersAsync();

            var responses = await Task.WhenAll(
                first.Client.PatchAsJsonAsync(Members(workspace, first.ActorId), new { role = "admin" }, Ct),
                second.Client.PatchAsJsonAsync(Members(workspace, second.ActorId), new { role = "admin" }, Ct));

            try
            {
                Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
                await ProblemAssert.IsProblemAsync(Assert.Single(responses, r => r.StatusCode != HttpStatusCode.OK), HttpStatusCode.Conflict, "last_owner");
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            Assert.Single(await ListAsync(first.Client, workspace), m => m.Role == WorkspaceRole.Owner);
        }
    }

    [Fact]
    public async Task ConcurrentMutualDemotion_TwoOwners_OneSucceedsAndOneOwnerRemains()
    {
        var (first, second, workspace) = await TwoOwnersAsync();

        var responses = await Task.WhenAll(
            first.Client.PatchAsJsonAsync(Members(workspace, second.ActorId), new { role = "admin" }, Ct),
            second.Client.PatchAsJsonAsync(Members(workspace, first.ActorId), new { role = "admin" }, Ct));

        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            // The loser is no owner any more once the lock is released: 403 (re-checked under the lock) or 409 last_owner.
            var loser = Assert.Single(responses, r => r.StatusCode != HttpStatusCode.OK);
            Assert.Contains(loser.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.Conflict });
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        Assert.Single(await ListAsync(first.Client, workspace), m => m.Role == WorkspaceRole.Owner);
    }

    private async Task<(TestAgent First, TestAgent Second, WorkspaceDto Workspace)> TwoOwnersAsync()
    {
        var (first, workspace) = await OwnWorkspaceAsync();
        var second = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(first.Client, workspace.Slug, second.ActorId, "owner");
        return (first, second, workspace);
    }

    private async Task<(TestAgent Owner, WorkspaceDto Workspace)> OwnWorkspaceAsync()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        return (owner, await TenancyFixture.CreateWorkspaceAsync(owner.Client, "mem"));
    }

    private static string Members(WorkspaceDto workspace, Guid actorId) => TenancyFixture.Path(workspace.Slug, $"/members/{actorId}");

    private static async Task<List<MemberDto>> ListAsync(HttpClient client, WorkspaceDto workspace) =>
        (await client.GetFromJsonAsync<List<MemberDto>>(TenancyFixture.Path(workspace.Slug, "/members"), AgcoworkApiFactory.Json, Ct))!;
}
