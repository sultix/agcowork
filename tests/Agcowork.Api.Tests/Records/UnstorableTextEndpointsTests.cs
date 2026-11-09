using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Agcowork.Api.Tests.Collections;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Api.Tests.Views;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Agcowork.Api.Tests.Records;

/// <summary>
/// NUL and unpaired surrogates in text over REST (AP-028): 400 <c>validation_failed</c> with <c>invalid_value</c> on the field
/// that carries them, never a 500. NUL reaches the use cases; an escaped unpaired surrogate in a string field is refused by
/// the JSON binder and reported on the field of its JSON path; in property values (JSON) the use case reports it.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class UnstorableTextEndpointsTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// (method, path, body, field). Tokens: <c>{X}</c> the bad text (a JSON escape), <c>{S}</c> space, <c>{C}</c> collection,
    /// <c>{R}</c> record, <c>{V}</c> view.
    /// </summary>
    private static readonly (string Method, string Path, string Body, string Field)[] Cases =
    [
        ("POST", "/records", """{ "collectionId": "{C}", "title": "a{X}" }""", "title"),
        ("POST", "/records", """{ "collectionId": "{C}", "title": "Ok", "body": "# Text{X}" }""", "body"),
        ("POST", "/records", """{ "collectionId": "{C}", "title": "Ok", "properties": { "notes": "{X}" } }""", "properties.notes"),
        ("POST", "/records", """{ "collectionId": "{C}", "properties": { "title": "{X}" } }""", "properties.title"),
        ("POST", "/records", """{ "collectionId": "{C}", "title": "Ok", "properties": { "tags": ["db", "{X}"] } }""", "properties.tags[1]"),
        ("POST", "/records", """{ "collectionId": "{C}", "title": "Ok", "properties": { "link": "https://example.com/{X}" } }""", "properties.link"),
        ("POST", "/records", """{ "collectionId": "{C}", "title": "Ok", "properties": { "data": { "k": ["{X}"] } } }""", "properties.data"),
        ("POST", "/records", """{ "spaceId": "{S}", "title": "{X}" }""", "title"),
        ("PATCH", "/records/{R}", """{ "title": "{X}" }""", "title"),
        ("PATCH", "/records/{R}", """{ "body": "{X}" }""", "body"),
        ("PATCH", "/records/{R}", """{ "properties": { "notes": "{X}" } }""", "properties.notes"),
        ("PATCH", "/records/{R}", """{ "title": "Neu", "reason": "{X}" }""", "reason"),
        ("POST", "/records/{R}/append", """{ "markdown": "- {X}" }""", "markdown"),
        ("POST", "/records/{R}/append", """{ "markdown": "- ok", "section": "Log{X}" }""", "section"),
        ("POST", "/spaces", """{ "name": "{X}" }""", "name"),
        ("PATCH", "/spaces/{S}", """{ "description": "{X}" }""", "description"),
        ("POST", "/collections", """{ "spaceId": "{S}", "name": "{X}", "description": "Desc." }""", "name"),
        ("PATCH", "/collections/{C}", """{ "description": "{X}" }""", "description"),
        ("POST", "/collections", """{ "spaceId": "{S}", "name": "Neu", "description": "Desc.", "schema": [{ "key": "p", "name": "P", "type": "select", "options": [{ "value": "a", "label": "{X}" }] }] }""", "schema[0].options[0].label"),
        ("PATCH", "/collections/{C}/schema", """{ "add": [{ "key": "extra", "name": "{X}", "type": "text" }] }""", "add[0].name"),
        ("PATCH", "/collections/{C}/schema", """{ "update": [{ "key": "tags", "options": [{ "value": "db" }, { "value": "{X}" }] }] }""", "update[0].options[1].value"),
        ("POST", "/collections/{C}/views", """{ "name": "{X}", "type": "table" }""", "name"),
        ("PATCH", "/views/{V}", """{ "name": "V{X}" }""", "name"),
    ];

    [Theory]
    [InlineData("\\u0000")]
    [InlineData("\\ud800")]
    [InlineData("\\udfff")]
    [InlineData("\\ude00\\ud83d")]
    public async Task Endpoints_NulOrUnpairedSurrogate_400InvalidValueOnTheField(string escape)
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "utf");
        var space = await SchemaApi.CreateSpaceAsync(owner.Client, workspace.Slug, "Raum");
        var collection = await SchemaApi.CreateCollectionAsync(owner.Client, workspace.Slug, space.Id, schema:
        [
            new { key = "notes", name = "Notes", type = "markdown" },
            new { key = "tags", name = "Tags", type = "multi_select", options = new[] { new { value = "db" } } },
            new { key = "link", name = "Link", type = "url" },
            new { key = "data", name = "Data", type = "json" },
        ]);
        var record = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collection.Id, "Vorher", "Text");
        var view = await ViewsApi.CreateAsync(owner.Client, workspace.Slug, collection.Id, new { name = "Alle", type = "table" });

        var failures = new List<string>();
        foreach (var (method, path, body, field) in Cases)
        {
            string Fill(string template) => template
                .Replace("{X}", escape, StringComparison.Ordinal)
                .Replace("{S}", space.Id.ToString(), StringComparison.Ordinal)
                .Replace("{C}", collection.Id.ToString(), StringComparison.Ordinal)
                .Replace("{R}", record.Id.ToString(), StringComparison.Ordinal)
                .Replace("{V}", view.Id.ToString(), StringComparison.Ordinal);

            using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(TenancyFixture.Path(workspace.Slug, Fill(path)), UriKind.Relative))
            {
                Content = new StringContent(Fill(body), Encoding.UTF8, "application/json"),
            };
            using var response = await owner.Client.SendAsync(request, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            if (response.StatusCode != HttpStatusCode.BadRequest || !HasFieldError(text, field))
            {
                failures.Add($"{method} {path} ({field}): {(int)response.StatusCode} {text}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));

        // Nothing was changed.
        var unchanged = await RecordsApi.GetAsync(owner.Client, workspace.Slug, record.Id);
        Assert.Equal(("Vorher", "Text", 1), (unchanged.Title, unchanged.Body, unchanged.Revision));
    }

    [Fact]
    public async Task CreateRecord_UnpairedSurrogateInPropertyKey_InvalidValueOnProperties_PairsAreFine()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "utf");
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);

        using var bad = await PostAsync(owner.Client, workspace.Slug, $$"""{ "collectionId": "{{collection.Id}}", "title": "Ok", "properties": { "a\ud800": 1 } }""");
        using var paired = await PostAsync(owner.Client, workspace.Slug, $$"""{ "collectionId": "{{collection.Id}}", "title": "Rakete 🚀" }""");

        var problem = await Identity.ProblemAssert.IsProblemAsync(bad, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("invalid_value", Identity.ProblemAssert.Field(problem, "properties").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, paired.StatusCode);
    }

    [Fact]
    public async Task Binder_WrongTypeWithPath_FieldError_MalformedJsonStaysBadRequest()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "utf");
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);

        using var wrongType = await PostAsync(owner.Client, workspace.Slug, $$"""{ "collectionId": "{{collection.Id}}", "title": 5 }""");
        var problem = await Identity.ProblemAssert.IsProblemAsync(wrongType, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("invalid_value", Identity.ProblemAssert.Field(problem, "title").GetProperty("code").GetString());

        using var badId = await PostAsync(owner.Client, workspace.Slug, """{ "collectionId": "nope", "title": "x" }""");
        problem = await Identity.ProblemAssert.IsProblemAsync(badId, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("invalid_value", Identity.ProblemAssert.Field(problem, "collectionId").GetProperty("code").GetString());

        using var malformed = await PostAsync(owner.Client, workspace.Slug, """{ "title": """);
        problem = await Identity.ProblemAssert.IsProblemAsync(malformed, HttpStatusCode.BadRequest, "bad_request");
        Assert.False(problem.TryGetProperty("errors", out _));
    }

    /// <summary>
    /// The test host runs in Development, where minimal APIs throw binding failures anyway; in Production they only do so
    /// because the API sets <c>ThrowOnBadRequest</c>. Without it this would be a bare 400 <c>bad_request</c> without errors.
    /// </summary>
    [Fact]
    public async Task Production_BinderErrors_FieldErrorOrBadRequest_NoErrorLog()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "utp");
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        var logs = new CapturingLoggerProvider();
        using var production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production")
            .UseSetting("Agcowork:Bootstrap:AdminEmail", factory.AdminEmail)
            .UseSetting("Agcowork:Bootstrap:AdminPassword", factory.AdminPassword)
            .UseSetting("Agcowork:CredentialThrottling:Enabled", "true")
            .ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = production.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = owner.Client.DefaultRequestHeaders.Authorization;

        using var surrogate = await PostAsync(client, workspace.Slug, $$"""{ "collectionId": "{{collection.Id}}", "title": "a\ud800" }""");
        var problem = await Identity.ProblemAssert.IsProblemAsync(surrogate, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("invalid_value", Identity.ProblemAssert.Field(problem, "title").GetProperty("code").GetString());

        using var badId = await PostAsync(client, workspace.Slug, """{ "collectionId": "nope", "title": "x" }""");
        problem = await Identity.ProblemAssert.IsProblemAsync(badId, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Contains("invalid format", Identity.ProblemAssert.Field(problem, "collectionId").GetProperty("message").GetString(), StringComparison.Ordinal);

        using var malformed = await PostAsync(client, workspace.Slug, """{ "title": """);
        problem = await Identity.ProblemAssert.IsProblemAsync(malformed, HttpStatusCode.BadRequest, "bad_request");
        Assert.Contains("JSON", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
    }

    [Theory]
    [InlineData("$.title", "title")]
    [InlineData("$.schema[0].options[1].label", "schema[0].options[1].label")]
    [InlineData("$['odd key'].x", "odd key.x")]
    [InlineData("$.properties['it's']", "properties.it's")]
    [InlineData("$.d['a.b'][0]", "d.a.b[0]")]
    [InlineData("$[2]", "[2]")]
    [InlineData("$", null)]
    [InlineData("", null)]
    public void FieldOfJsonPath_Mapping(string path, string? field) =>
        Assert.Equal(field, Http.ProblemDetailsMapping.FieldOfJsonPath(path));

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string slug, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync(new Uri(TenancyFixture.Path(slug, "/records"), UriKind.Relative), content, Ct);
    }

    private static bool HasFieldError(string body, string field)
    {
        using var problem = JsonDocument.Parse(body);
        return problem.RootElement.TryGetProperty("code", out var code) && code.GetString() == "validation_failed"
            && problem.RootElement.TryGetProperty("errors", out var errors)
            && errors.EnumerateArray().Any(e => e.GetProperty("field").GetString() == field && e.GetProperty("code").GetString() == "invalid_value");
    }

    /// <summary>Collects every log entry of a host.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                provider.Entries.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}
