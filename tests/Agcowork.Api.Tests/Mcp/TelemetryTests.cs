using System.Collections.Concurrent;
using System.Diagnostics;
using Agcowork.Api.Tests.Tenancy;

namespace Agcowork.Api.Tests.Mcp;

/// <summary>Every tool call is a span with tool, actor_id and workspace_id (architecture §5 Observability).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class TelemetryTests(AgcoworkApiFactory factory)
{
    [Fact]
    public async Task ToolCall_EmitsSpanWithActorWorkspaceAndTool()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var spans = new ConcurrentQueue<System.Diagnostics.Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Agcowork.Api",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName.StartsWith("mcp.tool ", StringComparison.Ordinal))
                {
                    spans.Enqueue(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await using (var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug))
        {
            McpClientFixture.Ok(await McpClientFixture.CallAsync(client, "workspace_overview"));
            McpClientFixture.Error(await McpClientFixture.CallAsync(client, "record_get", new { recordId = Guid.NewGuid() }), 404, "not_found");
        }

        await using (var instance = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, slug: null))
        {
            McpClientFixture.Ok(await McpClientFixture.CallAsync(instance, "me"));
        }

        var actor = t.AlphaOwner.ActorId.ToString();
        var overview = Assert.Single(spans, s => s.OperationName == "mcp.tool workspace_overview" && (string?)s.GetTagItem("actor_id") == actor);
        Assert.Equal("workspace_overview", overview.GetTagItem("tool"));
        Assert.Equal(t.Alpha.Id.ToString(), overview.GetTagItem("workspace_id"));
        Assert.Equal(ActivityStatusCode.Unset, overview.Status);

        var failed = Assert.Single(spans, s => s.OperationName == "mcp.tool record_get" && (string?)s.GetTagItem("actor_id") == actor);
        Assert.Equal(ActivityStatusCode.Error, failed.Status);
        Assert.Equal("not_found", failed.GetTagItem("error_code"));

        var me = Assert.Single(spans, s => s.OperationName == "mcp.tool me" && (string?)s.GetTagItem("actor_id") == actor);
        Assert.Null(me.GetTagItem("workspace_id"));
    }
}
