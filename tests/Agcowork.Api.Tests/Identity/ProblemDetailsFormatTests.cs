using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Agcowork.Api.Tests.Identity;

/// <summary>
/// Pins the error wire format all endpoints share (RFC 9457 + <c>code</c> + <c>errors[]</c>).
/// Changing these expectations is an API break for agents.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ProblemDetailsFormatTests(AgcoworkApiFactory factory)
{
    private static readonly string[] InvalidScopes = ["workspaces:create", "instance:root"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ValidationError_HasErrorsArrayWithFieldCodeMessageAndAllowed()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);

        using var response = await admin.PostAsJsonAsync(
            "/api/v1/actors",
            new { kind = "robot", name = "", instanceScopes = InvalidScopes },
            Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Superset(new HashSet<string> { "type", "title", "status", "detail", "code", "errors" }, ProblemAssert.PropertyNames(problem));
        Assert.Equal(JsonValueKind.Array, problem.GetProperty("errors").ValueKind);

        var kind = ProblemAssert.Field(problem, "kind");
        Assert.Equal(new HashSet<string> { "field", "code", "message", "allowed" }, ProblemAssert.PropertyNames(kind));
        Assert.Equal("not_in_options", kind.GetProperty("code").GetString());
        Assert.Equal(["human", "agent"], kind.GetProperty("allowed").EnumerateArray().Select(v => v.GetString()));

        var name = ProblemAssert.Field(problem, "name");
        Assert.Equal(new HashSet<string> { "field", "code", "message" }, ProblemAssert.PropertyNames(name));
        Assert.Equal("required", name.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(name.GetProperty("message").GetString()));

        var scope = ProblemAssert.Field(problem, "instanceScopes[1]");
        Assert.Equal("not_in_options", scope.GetProperty("code").GetString());
        Assert.Equal(["instance:admin", "workspaces:create"], scope.GetProperty("allowed").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public async Task ValidationError_HumanWithoutEmail_RequiredOnEmail()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);

        using var response = await admin.PostAsJsonAsync("/api/v1/actors", new { kind = "human", name = "Ada" }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("required", ProblemAssert.Field(problem, "email").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Forbidden_HasDetailNamingMissingScope()
    {
        using var agent = await factory.CreateAgentClientAsync();

        using var response = await agent.GetAsync(new Uri("/api/v1/actors", UriKind.Relative), Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal("requires instance:admin", problem.GetProperty("detail").GetString());
        Assert.False(problem.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Unauthorized_HasNoDetail()
    {
        using var client = factory.CreateClientWithoutCookies();

        using var response = await client.GetAsync(new Uri("/api/v1/actors", UriKind.Relative), Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.False(problem.TryGetProperty("detail", out _));
        Assert.False(problem.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task NotFound_HasNoDetail()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);

        using var response = await admin.DeleteAsync(new Uri($"/api/v1/keys/{Guid.CreateVersion7()}", UriKind.Relative), Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound, "not_found");
        Assert.False(problem.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task MalformedJson_400ProblemDetails()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        using var content = new StringContent("{ \"kind\": ", Encoding.UTF8, "application/json");

        using var response = await admin.PostAsync(new Uri("/api/v1/actors", UriKind.Relative), content, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "bad_request");
        Assert.Contains("JSON", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }
}
