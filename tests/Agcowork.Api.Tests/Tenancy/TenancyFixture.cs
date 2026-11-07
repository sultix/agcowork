using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agcowork.Api.Tests.Identity;
using Agcowork.Application.Identity.ApiKeys;
using Agcowork.Application.Workspaces;

namespace Agcowork.Api.Tests.Tenancy;

/// <summary>
/// Isolation fixture (architecture §2a, "Isolationstests"): every workspace endpoint must answer 404
/// for callers outside the workspace. Use it for every new endpoint under <c>/api/v1/w/{slug}</c>:
/// <code>
/// var t = await TenancyFixture.GetAsync(factory);
/// await t.AssertIsolatedAsync(HttpMethod.Get, "/records", homeStatus: HttpStatusCode.OK);              // whole route
/// await TenancyFixture.AssertNotFoundAsync(t.AlphaOwner.Client, HttpMethod.Get,
///     TenancyFixture.Path(t.Alpha.Slug, $"/records/{betaRecordId}"));                                    // foreign id
/// </code>
/// </summary>
/// <remarks>
/// Layout, created once per <see cref="AgcoworkApiFactory"/> through the public API:
/// <list type="bullet">
/// <item><see cref="Alpha"/> and <see cref="Beta"/>: two workspaces with unique slugs (<c>alpha-…</c>, <c>beta-…</c>).</item>
/// <item><see cref="AlphaOwner"/> / <see cref="BetaOwner"/>: agents that created them (owner, the only one) with unrestricted keys.</item>
/// <item><see cref="Outsider"/>: agent without any membership.</item>
/// <item><see cref="RestrictedToAlpha"/>: editor in both workspaces whose key is restricted to Alpha.</item>
/// <item><see cref="AlphaHuman"/>: a human, editor in Alpha, calling with a session cookie (the browser path).</item>
/// </list>
/// The fixture is shared by all tests of the collection: never change it. Tests that mutate
/// (archive, change owners) create their own workspace with <see cref="CreateWorkspaceAsync"/>.
/// Seed data for new modules (records, collections, …) into Alpha and Beta through their owners.
/// </remarks>
public sealed class TenancyFixture
{
    private static readonly ConcurrentDictionary<AgcoworkApiFactory, Lazy<Task<TenancyFixture>>> Instances = new();

    private TenancyFixture(
        AgcoworkApiFactory factory,
        WorkspaceDto alpha,
        WorkspaceDto beta,
        TestAgent alphaOwner,
        TestAgent betaOwner,
        TestAgent outsider,
        TestAgent restricted,
        TestHuman alphaHuman)
    {
        AlphaHuman = alphaHuman;
        Factory = factory;
        Alpha = alpha;
        Beta = beta;
        AlphaOwner = alphaOwner;
        BetaOwner = betaOwner;
        Outsider = outsider;
        RestrictedToAlpha = restricted;
    }

    public AgcoworkApiFactory Factory { get; }

    public WorkspaceDto Alpha { get; }

    public WorkspaceDto Beta { get; }

    /// <summary>Sole owner of <see cref="Alpha"/>; no membership in Beta.</summary>
    public TestAgent AlphaOwner { get; }

    /// <summary>Sole owner of <see cref="Beta"/>; no membership in Alpha.</summary>
    public TestAgent BetaOwner { get; }

    /// <summary>Authenticated, but member of no workspace.</summary>
    public TestAgent Outsider { get; }

    /// <summary>Editor in Alpha and Beta, but the key is restricted to Alpha: Beta must look nonexistent.</summary>
    public TestAgent RestrictedToAlpha { get; }

    /// <summary>Human editor in Alpha only, authenticated by session cookie.</summary>
    public TestHuman AlphaHuman { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The shared fixture of this factory, created on first use.</summary>
    public static Task<TenancyFixture> GetAsync(AgcoworkApiFactory factory) =>
        Instances.GetOrAdd(factory, f => new Lazy<Task<TenancyFixture>>(() => CreateAsync(f))).Value;

    /// <summary>Absolute path of a workspace endpoint: <c>Path("alpha", "/members")</c> → <c>/api/v1/w/alpha/members</c>.</summary>
    public static string Path(string slug, string relativePath) => $"/api/v1/w/{slug}{relativePath}";

    /// <summary>
    /// Asserts the standard isolation cases for one workspace route (<paramref name="relativePath"/> below
    /// <c>/w/{slug}</c>, e.g. <c>"/members"</c> or <c>""</c>), each must be a bare 404:
    /// Alpha's owner in Beta, Alpha's human member (cookie session) in Beta, the outsider in Alpha, the
    /// Alpha-restricted key in Beta (although a member), and an unknown slug. With <paramref name="homeStatus"/>, Alpha's owner in Alpha must get exactly that status:
    /// the positive control that proves the route exists (pick a request that does not change the fixture,
    /// e.g. an invalid body yielding 400).
    /// </summary>
    public async Task AssertIsolatedAsync(HttpMethod method, string relativePath, object? body = null, HttpStatusCode? homeStatus = null)
    {
        await AssertNotFoundAsync(AlphaOwner.Client, method, Path(Beta.Slug, relativePath), body);
        await AssertNotFoundAsync(AlphaHuman.Client, method, Path(Beta.Slug, relativePath), body);
        await AssertNotFoundAsync(Outsider.Client, method, Path(Alpha.Slug, relativePath), body);
        await AssertNotFoundAsync(RestrictedToAlpha.Client, method, Path(Beta.Slug, relativePath), body);
        await AssertNotFoundAsync(AlphaOwner.Client, method, Path($"missing-{Guid.NewGuid():N}"[..20], relativePath), body);

        if (homeStatus is { } expected)
        {
            using var response = await SendAsync(AlphaOwner.Client, method, Path(Alpha.Slug, relativePath), body);
            Assert.Equal(expected, response.StatusCode);
        }
    }

    /// <summary>Asserts a 404 ProblemDetails with code <c>not_found</c> and nothing that hints at existence.</summary>
    public static async Task AssertNotFoundAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var response = await SendAsync(client, method, path, body);
        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound, "not_found");
        Assert.False(problem.TryGetProperty("detail", out _), $"404 for {method} {path} must not carry a detail.");
        Assert.False(problem.TryGetProperty("errors", out _), $"404 for {method} {path} must not carry errors.");
    }

    /// <summary>Sends a JSON request; POST/PATCH/PUT without <paramref name="body"/> send <c>{}</c>.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null || method == HttpMethod.Post || method == HttpMethod.Patch || method == HttpMethod.Put)
        {
            request.Content = JsonContent.Create(body ?? new { }, options: AgcoworkApiFactory.Json);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>Creates a workspace with a unique slug (<paramref name="slugPrefix"/>-…) as <paramref name="creator"/>, who becomes owner.</summary>
    public static async Task<WorkspaceDto> CreateWorkspaceAsync(HttpClient creator, string slugPrefix = "ws")
    {
        ArgumentNullException.ThrowIfNull(creator);
        var slug = $"{slugPrefix}-{Guid.NewGuid():N}"[..Math.Min(slugPrefix.Length + 13, 63)];
        using var response = await creator.PostAsJsonAsync("/api/v1/workspaces", new { slug, name = $"Workspace {slug}" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WorkspaceDto>(AgcoworkApiFactory.Json, Ct))!;
    }

    /// <summary>Adds <paramref name="actorId"/> with <paramref name="role"/>; fails the test unless 201.</summary>
    public static async Task<MemberDto> AddMemberAsync(HttpClient manager, string slug, Guid actorId, string role, IReadOnlyList<string>? scopes = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        using var response = await manager.PostAsJsonAsync(Path(slug, "/members"), new { actorId, role, scopes }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MemberDto>(AgcoworkApiFactory.Json, Ct))!;
    }

    /// <summary>Issues another key for an existing agent (e.g. with <c>new { scopes = new[] { "read" } }</c>) and returns a client using it.</summary>
    public static async Task<HttpClient> CreateKeyClientAsync(AgcoworkApiFactory factory, Guid actorId, object keyOptions)
    {
        ArgumentNullException.ThrowIfNull(factory);
        using var admin = await factory.CreateAdminClientAsync(Ct);
        using var response = await admin.PostAsJsonAsync($"/api/v1/actors/{actorId}/keys", keyOptions, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var key = (await response.Content.ReadFromJsonAsync<CreatedApiKeyDto>(AgcoworkApiFactory.Json, Ct))!;

        var client = factory.CreateClientWithoutCookies();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Key);
        return client;
    }

    private static async Task<TenancyFixture> CreateAsync(AgcoworkApiFactory factory)
    {
        var alphaOwner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var betaOwner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var outsider = await factory.CreateAgentAsync(cancellationToken: Ct);

        var alpha = await CreateWorkspaceAsync(alphaOwner.Client, "alpha");
        var beta = await CreateWorkspaceAsync(betaOwner.Client, "beta");

        // The key restriction references the workspace, so Alpha has to exist first.
        var restricted = await factory.CreateAgentAsync(keyOptions: new { workspaceId = alpha.Id }, cancellationToken: Ct);
        await AddMemberAsync(alphaOwner.Client, alpha.Slug, restricted.ActorId, "editor");
        await AddMemberAsync(betaOwner.Client, beta.Slug, restricted.ActorId, "editor");

        var human = await CreateHumanAsync(factory);
        await AddMemberAsync(alphaOwner.Client, alpha.Slug, human.ActorId, "editor");

        return new TenancyFixture(factory, alpha, beta, alphaOwner, betaOwner, outsider, restricted, human);
    }

    /// <summary>Creates a human with a password (via the admin API) and returns a client logged in by session cookie.</summary>
    public static async Task<TestHuman> CreateHumanAsync(AgcoworkApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var email = $"human-{Guid.NewGuid():N}@example.com";
        const string password = "correct horse battery staple";
        using (var admin = await factory.CreateAdminClientAsync(Ct))
        {
            using var response = await admin.PostAsJsonAsync("/api/v1/actors", new { kind = "human", name = "Human member", email, password }, Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var actor = (await response.Content.ReadFromJsonAsync<Application.Identity.Actors.ActorDto>(AgcoworkApiFactory.Json, Ct))!;

            var client = factory.CreateClient();
            await AgcoworkApiFactory.LoginAsync(client, email, password, Ct);
            return new TestHuman(actor.Id, client);
        }
    }
}

/// <summary>A human created for a test, with a client that carries its session cookie.</summary>
public sealed record TestHuman(Guid ActorId, HttpClient Client) : IDisposable
{
    public void Dispose() => Client.Dispose();
}
