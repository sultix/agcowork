using System.Diagnostics.CodeAnalysis;
using Agcowork.Application.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Agcowork.Api.Realtime;

/// <summary>
/// The realtime hub of a workspace, <c>/w/{workspaceSlug}/hub</c> (architecture §2a, step 6). A thin adapter: the HTTP pipeline
/// has authenticated the caller (cookie or bearer key), resolved the tenant (404 for unknown workspace, no membership, key for
/// another workspace) and authorized <c>read</c> (<see cref="RealtimeConnectMiddleware"/>); the hub only joins the connection
/// to its workspace group <c>ws:{WorkspaceId}</c>. Server to client there is one message, <c>activity</c> with an
/// <c>ActivityEventDto</c>, sent by the dispatcher. Client to server only <see cref="Ping"/>.
/// </summary>
internal sealed class WorkspaceHub(RealtimeConnections connections) : Hub
{
    /// <summary>Name of the only server-to-client message.</summary>
    public const string ActivityMessage = "activity";

    public override async Task OnConnectedAsync()
    {
        // Set by RealtimeConnectMiddleware on the request that opened the transport. Missing means the request did not pass
        // the realtime pipeline: fail closed.
        if (Context.GetHttpContext()?.Items[RealtimeConnectMiddleware.SubscriberItemKey] is not RealtimeSubscriber subscriber)
        {
            Context.Abort();
            return;
        }

        // Registered before joining the group, so the dispatcher's access check sees every connection that can receive events.
        connections.Add(Context, subscriber);
        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeConnections.Group(subscriber.WorkspaceId));
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Liveness check for clients; answers <c>"pong"</c>.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "SignalR invokes hub methods on instances only.")]
    public string Ping() => "pong";
}
