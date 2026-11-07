using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agcowork.Api.Tests.Tenancy;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Agcowork.Api.Tests.Mcp;

/// <summary>
/// MCP client of the SDK against the test host (<see cref="AgcoworkApiFactory"/>), plus raw JSON-RPC requests for the
/// transport-level checks (404 from the tenancy middleware, 401 for cookies), and assertions on tool results.
/// </summary>
internal static class McpClientFixture
{
    public const string InstancePath = "/mcp";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string WorkspacePath(string slug) => $"/w/{slug}/mcp";

    /// <summary>Connects (initialize/discover) to <c>/w/{slug}/mcp</c>, or to <c>/mcp</c> when <paramref name="slug"/> is null, with a bearer key.</summary>
    public static Task<McpClient> ConnectAsync(AgcoworkApiFactory factory, string key, string? slug)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return ConnectAsync(factory.CreateClientWithoutCookies(), key, slug);
    }

    /// <summary>Same with a given HTTP client (e.g. of a host with replaced services); the client is disposed with the MCP client.</summary>
    public static async Task<McpClient> ConnectAsync(HttpClient http, string key, string? slug)
    {
        ArgumentNullException.ThrowIfNull(http);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, slug is null ? InstancePath : WorkspacePath(slug)),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" },
            },
            http,
            loggerFactory: null,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    /// <summary>Calls a tool; arguments are serialized with the API's JSON settings and sent as JSON elements.</summary>
    public static async Task<CallToolResult> CallAsync(McpClient client, string tool, object? arguments = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var args = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(arguments, AgcoworkApiFactory.Json).EnumerateObject())
            {
                args[property.Name] = property.Value;
            }
        }

        return await client.CallToolAsync(tool, args, cancellationToken: Ct);
    }

    /// <summary>Asserts a successful tool result with a text block and returns its structured content.</summary>
    public static JsonElement Ok(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Assert.True(result.IsError != true, $"Tool failed: {Text(result)}");
        Assert.NotEmpty(Text(result));
        return result.StructuredContent ?? throw new Xunit.Sdk.XunitException("No structured content.");
    }

    /// <summary>Calls a tool and deserializes the structured content (fails unless successful).</summary>
    public static async Task<T> OkAsync<T>(McpClient client, string tool, object? arguments = null) =>
        Ok(await CallAsync(client, tool, arguments)).Deserialize<T>(AgcoworkApiFactory.Json)!;

    /// <summary>Asserts an <c>isError</c> result in the ProblemDetails shape with <paramref name="code"/>; returns the problem.</summary>
    public static JsonElement Error(CallToolResult result, int status, string code)
    {
        ArgumentNullException.ThrowIfNull(result);
        Assert.True(result.IsError == true, $"Expected an error, got: {Text(result)}");
        var problem = result.StructuredContent ?? throw new Xunit.Sdk.XunitException("No structured content.");
        Assert.Equal(status, problem.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Contains(code, Text(result), StringComparison.Ordinal);
        return problem;
    }

    public static string Text(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
    }

    /// <summary>Sends one raw JSON-RPC request (as an MCP client would) with the given HTTP client.</summary>
    public static async Task<HttpResponseMessage> PostRawAsync(HttpClient client, string path, string method, object? @params = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = @params ?? new { } }, AgcoworkApiFactory.Json);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        return await client.SendAsync(request, Ct);
    }

    /// <summary>
    /// Isolation of one workspace tool (architecture §2a for MCP): Alpha's owner in Beta, the outsider in Alpha, the
    /// Alpha-restricted key in Beta and an unknown slug all get a bare HTTP 404 before any MCP answer; Alpha's human member
    /// with a session cookie gets 401 even in Alpha (MCP accepts API keys only). The positive control calls the tool as
    /// Alpha's owner in Alpha with the SDK client and expects a tool result (success or tool error).
    /// </summary>
    public static async Task AssertToolIsolatedAsync(TenancyFixture t, McpClient alphaOwner, string tool, object? arguments = null)
    {
        ArgumentNullException.ThrowIfNull(t);
        var call = new { name = tool, arguments = arguments ?? new { } };
        await AssertTransportNotFoundAsync(t.AlphaOwner.Client, WorkspacePath(t.Beta.Slug), call);
        await AssertTransportNotFoundAsync(t.Outsider.Client, WorkspacePath(t.Alpha.Slug), call);
        await AssertTransportNotFoundAsync(t.RestrictedToAlpha.Client, WorkspacePath(t.Beta.Slug), call);
        await AssertTransportNotFoundAsync(t.AlphaOwner.Client, WorkspacePath($"missing-{Guid.NewGuid():N}"[..20]), call);
        using (var cookie = await PostRawAsync(t.AlphaHuman.Client, WorkspacePath(t.Alpha.Slug), "tools/call", call))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, cookie.StatusCode);
        }

        // Positive control: the route exists and the tool answers (it may be a tool error, but never a transport error).
        var result = await CallAsync(alphaOwner, tool, arguments);
        Assert.NotNull(result);
    }

    public static async Task AssertTransportNotFoundAsync(HttpClient client, string path, object @params, string method = "tools/call")
    {
        using var response = await PostRawAsync(client, path, method, @params);
        var problem = await Identity.ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound, "not_found");
        Assert.False(problem.TryGetProperty("detail", out _), $"404 for {path} must not carry a detail.");
    }
}
