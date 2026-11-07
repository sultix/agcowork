using System.Text.Json;

namespace Agcowork.Api.Tests.Identity;

[Collection(ApiTestGroup.Name)]
public sealed class OpenApiIdentityTests(AgcoworkApiFactory factory)
{
    [Fact]
    public async Task OpenApiDocument_ContainsAllIdentityEndpointsWithDescriptions()
    {
        using var client = factory.CreateClient();
        await using var body = await client.GetStreamAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: TestContext.Current.CancellationToken);
        var paths = document.RootElement.GetProperty("paths");

        var expected = new (string Path, string Method)[]
        {
            ("/api/v1/auth/login", "post"),
            ("/api/v1/auth/logout", "post"),
            ("/api/v1/me", "get"),
            ("/api/v1/me/password", "patch"),
            ("/api/v1/actors/{id}/password", "put"),
            ("/api/v1/actors", "get"),
            ("/api/v1/actors", "post"),
            ("/api/v1/actors/{id}", "get"),
            ("/api/v1/actors/{id}", "patch"),
            ("/api/v1/actors/{id}/keys", "post"),
            ("/api/v1/actors/{id}/keys", "get"),
            ("/api/v1/keys/{id}", "delete"),
        };

        foreach (var (path, method) in expected)
        {
            Assert.True(paths.TryGetProperty(path, out var item), $"Missing path {path}");
            Assert.True(item.TryGetProperty(method, out var operation), $"Missing {method} {path}");
            Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("summary").GetString()), $"No summary on {method} {path}");
            Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("description").GetString()), $"No description on {method} {path}");
        }
    }

    [Theory]
    [InlineData("UpdateActorBody", "")]
    [InlineData("CreateApiKeyBody", "")]
    [InlineData("UpdateMemberBody", "")]
    [InlineData("UpdateWorkspaceRequest", "")]
    [InlineData("CreateActorRequest", "kind,name")]
    [InlineData("AddMemberRequest", "actorId,role")]
    [InlineData("CreateWorkspaceRequest", "slug,name")]
    [InlineData("LoginRequest", "email,password")]
    [InlineData("ChangeOwnPasswordRequest", "currentPassword,newPassword")]
    [InlineData("ResetPasswordBody", "newPassword")]
    public async Task OpenApiDocument_RequestBodies_RequireOnlyRealRequiredFields(string schema, string required)
    {
        using var client = factory.CreateClient();
        await using var body = await client.GetStreamAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: TestContext.Current.CancellationToken);
        var definition = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(schema);

        var actual = definition.TryGetProperty("required", out var list) ? list.EnumerateArray().Select(e => e.GetString()!).ToArray() : [];

        Assert.Equal(required.Split(',', StringSplitOptions.RemoveEmptyEntries), actual);
    }

    [Fact]
    public async Task OpenApiDocument_ResetPasswordBody_MustChangeDefaultsToTrue()
    {
        using var client = factory.CreateClient();
        await using var body = await client.GetStreamAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: TestContext.Current.CancellationToken);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        Assert.True(schemas.GetProperty("ResetPasswordBody").GetProperty("properties").GetProperty("mustChange").GetProperty("default").GetBoolean());
        Assert.Contains("mustChangePassword", schemas.GetProperty("MeDto").GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task OpenApiDocument_ProblemDetailsSchema_DocumentsCodeErrorsAndTraceId()
    {
        using var client = factory.CreateClient();
        await using var body = await client.GetStreamAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: TestContext.Current.CancellationToken);
        var properties = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("ProblemDetails").GetProperty("properties");

        Assert.Equal("string", properties.GetProperty("code").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("traceId").GetProperty("type").GetString());
        var errors = properties.GetProperty("errors");
        Assert.Equal("array", errors.GetProperty("type").GetString());
        var item = errors.GetProperty("items").GetProperty("properties");
        foreach (var name in new[] { "field", "code", "message", "allowed" })
        {
            Assert.True(item.TryGetProperty(name, out _), $"errors[] item lacks '{name}'");
        }
    }
}
