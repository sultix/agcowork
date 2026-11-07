using System.Net;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace Agcowork.Mcp.Stdio;

/// <summary>
/// Forwards an MCP server over stdio to an agcowork HTTP endpoint (<c>/w/{slug}/mcp</c> or <c>/mcp</c>): tools and
/// resources are listed and invoked upstream unchanged, so results, structured errors and isolation are the server's.
/// </summary>
public static class StdioProxy
{
    /// <summary>Environment variable holding the API key.</summary>
    public const string KeyVariable = "AGCOWORK_API_KEY";

    /// <summary>The HTTP status of a failed connect, searched through inner exceptions; null if there is none.</summary>
    public static HttpStatusCode? FindHttpStatus(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } status })
            {
                return status;
            }
        }

        return null;
    }

    /// <summary>A one-line explanation of a rejected connect, without stack trace.</summary>
    public static string DescribeConnectFailure(Uri endpoint, HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => $"agcowork-mcp-stdio: {endpoint} rejected the API key (401). Check {KeyVariable}: the key must be valid, not revoked and not expired.",
        HttpStatusCode.NotFound => $"agcowork-mcp-stdio: {endpoint} answered 404. Either the workspace slug is wrong, or the key's actor is no member of it, or the key is restricted to another workspace.",
        HttpStatusCode.Forbidden => $"agcowork-mcp-stdio: {endpoint} answered 403: the key lacks a required right.",
        _ => $"agcowork-mcp-stdio: {endpoint} answered {(int)status} {status}.",
    };

    public static McpServerOptions CreateServerOptions(McpClient upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        var options = new McpServerOptions
        {
            ServerInfo = upstream.ServerInfo,
            ServerInstructions = upstream.ServerInstructions,
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (request, cancellationToken) => upstream.ListToolsAsync(request.Params ?? new(), cancellationToken),
                CallToolHandler = (request, cancellationToken) => upstream.CallToolAsync(
                    request.Params ?? throw new ArgumentException("tools/call needs params.", nameof(request)), cancellationToken),
            },
        };

        if (upstream.ServerCapabilities.Resources is not null)
        {
            options.Handlers.ListResourcesHandler = (request, cancellationToken) => upstream.ListResourcesAsync(request.Params ?? new(), cancellationToken);
            options.Handlers.ListResourceTemplatesHandler = (request, cancellationToken) => upstream.ListResourceTemplatesAsync(request.Params ?? new(), cancellationToken);
            options.Handlers.ReadResourceHandler = (request, cancellationToken) => upstream.ReadResourceAsync(
                request.Params ?? throw new ArgumentException("resources/read needs params.", nameof(request)), cancellationToken);
        }

        return options;
    }

    /// <summary>The upstream transport: Streamable HTTP with the API key as bearer token.</summary>
    public static HttpClientTransport CreateUpstreamTransport(Uri url, string key, HttpClient? httpClient = null)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = url,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" },
        };
        return httpClient is null
            ? new HttpClientTransport(options, loggerFactory: null)
            : new HttpClientTransport(options, httpClient, loggerFactory: null, ownsHttpClient: false);
    }
}
