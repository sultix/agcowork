using System.IO.Pipelines;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Mcp.Stdio;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace Agcowork.Api.Tests.Mcp;

/// <summary>The stdio bridge (src/Agcowork.Mcp.Stdio) forwards tools and resources to the HTTP endpoint unchanged.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class StdioProxyTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Proxy_ForwardsToolsAndResources()
    {
        var t = await TenancyFixture.GetAsync(factory);
        using var http = factory.CreateClientWithoutCookies();
        await using var upstream = await McpClient.CreateAsync(
            StdioProxy.CreateUpstreamTransport(new Uri(http.BaseAddress!, McpClientFixture.WorkspacePath(t.Alpha.Slug)), t.AlphaOwner.Key, http),
            cancellationToken: Ct);

        // In-memory "stdio": two pipes between the proxy server and a client.
        var toServer = new Pipe();
        var toClient = new Pipe();
        await using var server = McpServer.Create(
            new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "agcowork"),
            StdioProxy.CreateServerOptions(upstream));
        var running = server.RunAsync(Ct);

        await using (var client = await McpClient.CreateAsync(new ModelContextProtocol.Protocol.StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()), cancellationToken: Ct))
        {
            var tools = await client.ListToolsAsync(cancellationToken: Ct);
            Assert.Equal(ToolsListTests.WorkspaceTools.Length, tools.Count);
            Assert.Contains("workspace_overview", client.ServerInstructions, StringComparison.Ordinal);

            var overview = await McpClientFixture.CallAsync(client, "workspace_overview");
            Assert.Equal(t.Alpha.Id, McpClientFixture.Ok(overview).GetProperty("workspace").GetProperty("id").GetGuid());

            var error = await McpClientFixture.CallAsync(client, "record_get", new { recordId = Guid.NewGuid() });
            McpClientFixture.Error(error, 404, "not_found");

            var templates = await client.ListResourceTemplatesAsync(cancellationToken: Ct);
            Assert.Equal(3, templates.Count);
        }

        toServer.Writer.Complete();
        await running.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }
}
