using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Agcowork.Api.Tests.Identity;
using Agcowork.Api.Tests.Records;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Schema;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Agcowork.Api.Tests.Mcp;

/// <summary>
/// Robustness of the tool pipeline: wrong argument types are named input errors, bugs are bare internal errors (as REST 500),
/// and the shared tool collections do not collect handlers across stateless requests.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class RobustnessTests(AgcoworkApiFactory factory)
{
    private const string Secret = "secret-internal-detail-4711";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("record_get", "recordId", "abc")]
    [InlineData("records_list", "limit", "x")]
    [InlineData("records_list", "includeArchived", "yes")]
    [InlineData("activity_recent", "since", "yesterday")]
    public async Task WrongArgumentType_InvalidValueOnTheParameter(string tool, string parameter, string value)
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug);
        var arguments = new Dictionary<string, object?> { [parameter] = value };
        if (tool == "records_list")
        {
            arguments["collectionId"] = Guid.NewGuid();
        }

        var problem = McpClientFixture.Error(await McpClientFixture.CallAsync(client, tool, arguments), 400, "validation_failed");

        var field = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal(parameter, field.GetProperty("field").GetString());
        Assert.Equal("invalid_value", field.GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingRequiredArguments_AllNamed()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug);

        var problem = McpClientFixture.Error(await McpClientFixture.CallAsync(client, "view_create", new { name = "X" }), 400, "validation_failed");

        Assert.Equal(
            ["collectionId", "type"],
            problem.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("field").GetString()).Order(StringComparer.Ordinal));
        Assert.All(problem.GetProperty("errors").EnumerateArray(), e => Assert.Equal("required", e.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task NestedValueOfWrongType_FieldIsParameterPlusJsonPath()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (space, _) = await RecordsApi.SeedCollectionAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug);

        var problem = McpClientFixture.Error(
            await McpClientFixture.CallAsync(client, "collection_create", new
            {
                spaceId = space.Id,
                name = "Kaputt",
                description = "Verschachtelt falsch.",
                schema = new object[] { new { key = "x", name = "X", type = "text", required = "yes" } },
            }),
            400,
            "validation_failed");

        var field = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal("schema[0].required", field.GetProperty("field").GetString());
        Assert.Equal("invalid_value", field.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RecordCreate_InvalidIdempotencyKey_FieldIsTheMcpArgument()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (_, collection) = await RecordsApi.SeedCollectionAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug);

        var problem = McpClientFixture.Error(
            await McpClientFixture.CallAsync(client, "record_create", new { collectionId = collection.Id, title = "X", idempotencyKey = " " }), 400, "validation_failed");

        var field = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal("idempotencyKey", field.GetProperty("field").GetString());
        Assert.DoesNotContain("header", field.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InternalException_InternalErrorWithoutDetails_LikeRest500()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (_, collection) = await RecordsApi.SeedCollectionAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        using var broken = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddScoped<ISchemaProvider, ThrowingSchemaProvider>()));

        // MCP: a bug in the use case's dependencies is internal_error, not an input error, and carries nothing of the exception.
        await using (var client = await McpClientFixture.ConnectAsync(
            broken.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false }), t.AlphaOwner.Key, t.Alpha.Slug))
        {
            var result = await McpClientFixture.CallAsync(client, "records_query", new { collectionId = collection.Id });
            var problem = McpClientFixture.Error(result, 500, "internal_error");

            Assert.Equal(["code", "status", "traceId"], McpProblemMembers(problem));
            Assert.DoesNotContain(Secret, McpClientFixture.Text(result), StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, problem.GetRawText(), StringComparison.Ordinal);
        }

        // REST: the same failure is a 500 without details.
        using var rest = broken.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        rest.DefaultRequestHeaders.Authorization = t.AlphaOwner.Client.DefaultRequestHeaders.Authorization;
        using var response = await rest.PostAsJsonAsync(TenancyFixture.Path(t.Alpha.Slug, $"/collections/{collection.Id}/query"), new { }, Ct);
        var restProblem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.DoesNotContain(Secret, restProblem.GetRawText(), StringComparison.Ordinal);
        Assert.False(restProblem.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task ManyStatelessRequests_SharedCollectionsCollectNoHandlers()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using (var workspace = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug))
        await using (var instance = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, slug: null))
        {
            for (var i = 0; i < 250; i++)
            {
                await workspace.ListToolsAsync(cancellationToken: Ct);
                await instance.ListToolsAsync(cancellationToken: Ct);
            }

            McpClientFixture.Ok(await McpClientFixture.CallAsync(workspace, "workspace_overview"));
        }

        var catalogType = typeof(Program).Assembly.GetType("Agcowork.Api.Mcp.McpCatalog", throwOnError: true)!;
        var catalog = factory.Services.GetRequiredService(catalogType);
        foreach (var name in new[] { "WorkspaceTools", "InstanceTools", "WorkspaceResources" })
        {
            var collection = catalogType.GetProperty(name)!.GetValue(catalog)!;
            Assert.Equal(0, ChangedHandlerCount(collection));
        }
    }

    private static string[] McpProblemMembers(System.Text.Json.JsonElement problem) =>
        [.. problem.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    /// <summary>Subscribers of <c>McpServerPrimitiveCollection&lt;T&gt;.Changed</c> (the compiler-generated backing field).</summary>
    private static int ChangedHandlerCount(object collection)
    {
        for (var type = collection.GetType(); type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(McpServerPrimitiveCollection<>))
            {
                var field = type.GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!;
                return (field.GetValue(collection) as Delegate)?.GetInvocationList().Length ?? 0;
            }
        }

        throw new InvalidOperationException("Not a primitive collection.");
    }

    private sealed class ThrowingSchemaProvider : ISchemaProvider
    {
        public Task<CollectionSchemaSnapshot?> GetSchemaAsync(Guid collectionId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Secret);

        public void Invalidate(Guid collectionId)
        {
        }
    }
}
