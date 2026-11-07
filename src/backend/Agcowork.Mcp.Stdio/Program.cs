using Agcowork.Mcp.Stdio;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

// agcowork-mcp-stdio --url https://agcowork.local/w/projekt-alpha/mcp   (API key from the environment variable AGCOWORK_API_KEY)
// stdout carries the MCP protocol; messages go to stderr.
string? url = null;
var key = Environment.GetEnvironmentVariable(StdioProxy.KeyVariable);
for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--url":
            url = args[++i];
            break;
        case "--key":
            // Supported for quick tests only: command lines end up in process lists and shell histories.
            key = args[++i];
            break;
    }
}

if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || string.IsNullOrWhiteSpace(key))
{
    await Console.Error.WriteLineAsync(
        $"Usage: {StdioProxy.KeyVariable}=agck_… agcowork-mcp-stdio --url <https://host/w/<workspace>/mcp | https://host/mcp>\n"
        + $"Set the API key in the environment variable {StdioProxy.KeyVariable} (recommended; --key <key> works but shows the key "
        + "in process lists).");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

McpClient upstream;
try
{
    upstream = await McpClient.CreateAsync(StdioProxy.CreateUpstreamTransport(endpoint, key), cancellationToken: cancellation.Token);
}
catch (Exception exception) when (StdioProxy.FindHttpStatus(exception) is { } status)
{
    await Console.Error.WriteLineAsync(StdioProxy.DescribeConnectFailure(endpoint, status));
    return 1;
}
catch (HttpRequestException exception)
{
    await Console.Error.WriteLineAsync($"agcowork-mcp-stdio: cannot reach {endpoint}: {exception.Message}");
    return 1;
}

await using (upstream)
{
    await using var server = McpServer.Create(new StdioServerTransport("agcowork"), StdioProxy.CreateServerOptions(upstream));
    await server.RunAsync(cancellation.Token);
}

return 0;
