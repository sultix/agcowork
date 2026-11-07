using System.Text.Json;

namespace Agcowork.Api.Tests.Tenancy;

[Collection(ApiTestGroup.Name)]
public sealed class OpenApiTenancyTests(AgcoworkApiFactory factory)
{
    [Fact]
    public async Task OpenApiDocument_ContainsAllWorkspaceEndpointsWithDescriptions()
    {
        using var client = factory.CreateClient();
        await using var body = await client.GetStreamAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: TestContext.Current.CancellationToken);
        var paths = document.RootElement.GetProperty("paths");

        var expected = new (string Path, string Method)[]
        {
            ("/api/v1/workspaces", "get"),
            ("/api/v1/workspaces", "post"),
            ("/api/v1/w/{workspaceSlug}", "get"),
            ("/api/v1/w/{workspaceSlug}", "patch"),
            ("/api/v1/w/{workspaceSlug}/archive", "post"),
            ("/api/v1/w/{workspaceSlug}/members", "get"),
            ("/api/v1/w/{workspaceSlug}/members", "post"),
            ("/api/v1/w/{workspaceSlug}/members/{actorId}", "patch"),
            ("/api/v1/w/{workspaceSlug}/members/{actorId}", "delete"),
        };

        foreach (var (path, method) in expected)
        {
            Assert.True(paths.TryGetProperty(path, out var item), $"Missing path {path}");
            Assert.True(item.TryGetProperty(method, out var operation), $"Missing {method} {path}");
            Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("summary").GetString()), $"No summary on {method} {path}");
            Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("description").GetString()), $"No description on {method} {path}");

            if (path.Contains("{workspaceSlug}", StringComparison.Ordinal))
            {
                var parameters = operation.TryGetProperty("parameters", out var p) ? p : item.GetProperty("parameters");
                Assert.Contains(parameters.EnumerateArray(), x => x.GetProperty("name").GetString() == "workspaceSlug" && x.GetProperty("in").GetString() == "path");
                Assert.True(operation.GetProperty("responses").TryGetProperty("404", out _), $"No 404 on {method} {path}");
            }
        }
    }
}
