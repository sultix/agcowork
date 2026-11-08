using System.Net;
using System.Text;
using System.Text.Json;
using Agcowork.Api.Tests.Collections;
using Agcowork.Api.Tests.Tenancy;

namespace Agcowork.Api.Tests.Records;

/// <summary>
/// NUL and unpaired surrogates beyond the content (AP-028 review): workspaces, actors, templates and the login e-mail answer
/// 400 <c>invalid_value</c> on the field instead of 500. The login check is about the input only and says nothing about
/// whether an account exists.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class UnstorableTextMoreEndpointsTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private enum Caller
    {
        Owner,
        Admin,
        Anonymous,
    }

    /// <summary>
    /// (caller, method, path, body, field). Tokens: <c>{X}</c> the bad text, <c>{W}</c> workspace slug, <c>{C}</c> collection,
    /// <c>{T}</c> template, <c>{A}</c> actor. Paths are absolute.
    /// </summary>
    private static readonly (Caller Caller, string Method, string Path, string Body, string Field)[] Cases =
    [
        (Caller.Owner, "POST", "/api/v1/workspaces", """{ "slug": "x-{N}", "name": "W{X}" }""", "name"),
        (Caller.Owner, "PATCH", "/api/v1/w/{W}", """{ "name": "{X}" }""", "name"),
        (Caller.Owner, "PATCH", "/api/v1/w/{W}", """{ "description": "{X}" }""", "description"),
        (Caller.Admin, "POST", "/api/v1/actors", """{ "kind": "agent", "name": "{X}" }""", "name"),
        (Caller.Admin, "POST", "/api/v1/actors", """{ "kind": "agent", "name": "Agent", "role": "{X}" }""", "role"),
        (Caller.Admin, "POST", "/api/v1/actors", """{ "kind": "agent", "name": "Agent", "email": "a{X}@example.com" }""", "email"),
        (Caller.Admin, "PATCH", "/api/v1/actors/{A}", """{ "name": "A{X}" }""", "name"),
        (Caller.Owner, "POST", "/api/v1/w/{W}/collections/{C}/templates", """{ "name": "{X}" }""", "name"),
        (Caller.Owner, "POST", "/api/v1/w/{W}/collections/{C}/templates", """{ "name": "Neu", "bodyMarkdown": "## {X}" }""", "bodyMarkdown"),
        (Caller.Owner, "PATCH", "/api/v1/w/{W}/collections/{C}/templates/{T}", """{ "description": "{X}" }""", "description"),
        (Caller.Owner, "PATCH", "/api/v1/w/{W}/collections/{C}/templates/{T}", """{ "bodyMarkdown": "{X}" }""", "bodyMarkdown"),
        (Caller.Anonymous, "POST", "/api/v1/auth/login", """{ "email": "{X}@example.com", "password": "irrelevant" }""", "email"),
    ];

    [Theory]
    [InlineData("\\u0000")]
    [InlineData("\\ud800")]
    [InlineData("\\udfff")]
    public async Task Endpoints_NulOrUnpairedSurrogate_400InvalidValueOnTheField(string escape)
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "utx");
        var (_, collection, template) = await SchemaApi.SeedAsync(owner.Client, workspace.Slug);
        using var admin = await factory.CreateAdminClientAsync(Ct);
        using var anonymous = factory.CreateClientWithoutCookies();

        var failures = new List<string>();
        foreach (var (caller, method, path, body, field) in Cases)
        {
            string Fill(string text) => text
                .Replace("{X}", escape, StringComparison.Ordinal)
                .Replace("{N}", Guid.NewGuid().ToString("N")[..8], StringComparison.Ordinal)
                .Replace("{W}", workspace.Slug, StringComparison.Ordinal)
                .Replace("{C}", collection.Id.ToString(), StringComparison.Ordinal)
                .Replace("{T}", template.Id.ToString(), StringComparison.Ordinal)
                .Replace("{A}", owner.ActorId.ToString(), StringComparison.Ordinal);

            var client = caller switch
            {
                Caller.Admin => admin,
                Caller.Anonymous => anonymous,
                _ => owner.Client,
            };
            using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(Fill(path), UriKind.Relative))
            {
                Content = new StringContent(Fill(body), Encoding.UTF8, "application/json"),
            };
            using var response = await client.SendAsync(request, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            if (response.StatusCode != HttpStatusCode.BadRequest || !HasFieldError(text, field))
            {
                failures.Add($"{method} {path} ({field}): {(int)response.StatusCode} {text}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static bool HasFieldError(string body, string field)
    {
        using var problem = JsonDocument.Parse(body);
        return problem.RootElement.TryGetProperty("code", out var code) && code.GetString() == "validation_failed"
            && problem.RootElement.TryGetProperty("errors", out var errors)
            && errors.EnumerateArray().Any(e => e.GetProperty("field").GetString() == field && e.GetProperty("code").GetString() == "invalid_value");
    }
}
