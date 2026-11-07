using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agcowork.Application.Identity.Actors;
using Agcowork.Application.Identity.ApiKeys;
using Agcowork.Infrastructure;
using Agcowork.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Agcowork.Api.Tests;

/// <summary>
/// Hosts the API against a throwaway PostgreSQL container. One container per test collection;
/// the API applies migrations and creates the bootstrap admin (from appsettings.Development.json)
/// on startup exactly as in production.
/// </summary>
/// <remarks>
/// All tests of the collection share one database. Create your own actors (unique names/emails)
/// instead of relying on a clean state. Helpers for authenticated calls:
/// <see cref="CreateAdminClientAsync"/> (bootstrap admin, session cookie) and
/// <see cref="CreateAgentAsync"/> / <see cref="CreateAgentClientAsync"/> (new agent + bearer key).
/// </remarks>
public sealed class AgcoworkApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("agcowork")
        .Build();

    /// <summary>JSON settings matching the API (web defaults, snake_case enums).</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public string AdminEmail => Bootstrap.AdminEmail ?? throw new InvalidOperationException("Bootstrap admin email not configured.");

    public string AdminPassword => Bootstrap.AdminPassword ?? throw new InvalidOperationException("Bootstrap admin password not configured.");

    private BootstrapOptions Bootstrap =>
        Services.GetRequiredService<IConfiguration>().GetSection(BootstrapOptions.SectionName).Get<BootstrapOptions>()
        ?? throw new InvalidOperationException("Bootstrap section missing.");

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(TestContext.Current.CancellationToken);

        // Starts the host, which runs the migrations and the identity bootstrap.
        _ = Server;
    }

    /// <summary>Anonymous client without cookie handling (to inspect Set-Cookie or send headers manually).</summary>
    public HttpClient CreateClientWithoutCookies() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    /// <summary>Client logged in as the bootstrap admin (<c>instance:admin</c>) via session cookie.</summary>
    public async Task<HttpClient> CreateAdminClientAsync(CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        await LoginAsync(client, AdminEmail, AdminPassword, cancellationToken);
        return client;
    }

    /// <summary>Logs the client in; fails the test unless the API answers 204.</summary>
    public static async Task LoginAsync(HttpClient client, string email, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password }, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>
    /// Creates an agent with the given instance scopes and an API key (via the admin API) and
    /// returns a client that authenticates with <c>Authorization: Bearer</c>.
    /// </summary>
    public async Task<TestAgent> CreateAgentAsync(
        IReadOnlyList<string>? instanceScopes = null,
        object? keyOptions = null,
        CancellationToken cancellationToken = default)
    {
        using var admin = await CreateAdminClientAsync(cancellationToken);

        using var actorResponse = await admin.PostAsJsonAsync(
            "/api/v1/actors",
            new { kind = "agent", name = $"agent-{Guid.NewGuid():N}", instanceScopes = instanceScopes ?? [] },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, actorResponse.StatusCode);
        var actor = (await actorResponse.Content.ReadFromJsonAsync<ActorDto>(Json, cancellationToken))!;

        using var keyResponse = await admin.PostAsJsonAsync($"/api/v1/actors/{actor.Id}/keys", keyOptions ?? new { }, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, keyResponse.StatusCode);
        var key = (await keyResponse.Content.ReadFromJsonAsync<CreatedApiKeyDto>(Json, cancellationToken))!;

        var client = CreateClientWithoutCookies();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Key);
        return new TestAgent(actor.Id, key.ApiKey.Id, key.Key, client);
    }

    /// <summary>Shortcut for <see cref="CreateAgentAsync"/> when only the client is needed.</summary>
    public async Task<HttpClient> CreateAgentClientAsync(params string[] instanceScopes) =>
        (await CreateAgentAsync(instanceScopes, cancellationToken: TestContext.Current.CancellationToken)).Client;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting($"ConnectionStrings:{DependencyInjection.ConnectionStringName}", _postgres.GetConnectionString());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

/// <summary>An agent created for a test, with a client that sends its key.</summary>
public sealed record TestAgent(Guid ActorId, Guid ApiKeyId, string Key, HttpClient Client) : IDisposable
{
    public void Dispose() => Client.Dispose();
}

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<AgcoworkApiFactory>
{
    public const string Name = "api";
}
