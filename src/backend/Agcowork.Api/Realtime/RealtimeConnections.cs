using System.Collections.Concurrent;
using System.Threading.Channels;
using Agcowork.Application.Activity;
using Agcowork.Application.Realtime;
using Agcowork.Infrastructure.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Agcowork.Api.Realtime;

/// <summary>
/// The live hub connections of this instance, by workspace. In-memory and per instance: more than one API instance needs a
/// SignalR backplane (architecture §5).
/// </summary>
/// <remarks>
/// Every connection has its own bounded outbox and a pump that writes to it, one message at a time and in order. The
/// dispatcher only enqueues, so a client that does not read (its writes block on back-pressure) holds up nobody: neither
/// the other connections of its workspace nor other workspaces. Its connection is closed when a write takes longer than
/// <see cref="RealtimeOptions.SendTimeout"/> or its outbox overflows. Connections still join the group <c>ws:{WorkspaceId}</c>
/// (the tenant boundary of architecture §2a); delivery goes per connection because only then a stuck receiver can be told
/// apart and cut off. The recipients are the same: the connections registered for the workspace.
/// </remarks>
internal sealed partial class RealtimeConnections(IHubContext<WorkspaceHub> hub, IOptions<RealtimeOptions> options, ILogger<RealtimeConnections> logger)
    : IRealtimeConnections
{
    /// <summary>Messages a connection may lag behind before it is closed as too slow.</summary>
    public const int OutboxCapacity = 256;

    private readonly ConcurrentDictionary<string, Entry> _byConnection = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, Entry>> _byWorkspace = new();
    private readonly TimeSpan _sendTimeout = options.Value.SendTimeout;

    /// <summary>Creates the outbox of a new connection; replaceable in tests (an outbox that fails to take events).</summary>
    internal Func<Channel<ActivityEventDto>> CreateOutbox { get; init; } = () =>
        Channel.CreateBounded<ActivityEventDto>(new BoundedChannelOptions(OutboxCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    /// <summary>SignalR group of a workspace.</summary>
    public static string Group(Guid workspaceId) => $"ws:{workspaceId}";

    public void Add(HubCallerContext context, RealtimeSubscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(subscriber);
        var outbox = CreateOutbox();
        var entry = new Entry(new RealtimeConnection(context.ConnectionId, subscriber), context, outbox);
        _byConnection[context.ConnectionId] = entry;
        _byWorkspace.GetOrAdd(subscriber.WorkspaceId, _ => new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal))[context.ConnectionId] = entry;
        _ = Task.Run(() => PumpAsync(entry));
    }

    public void Remove(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var entry))
        {
            return;
        }

        if (_byWorkspace.TryGetValue(entry.Connection.Subscriber.WorkspaceId, out var workspace))
        {
            workspace.TryRemove(connectionId, out _);
        }

        entry.Outbox.Writer.TryComplete();
    }

    public IReadOnlyList<RealtimeConnection> InWorkspace(Guid workspaceId) =>
        _byWorkspace.TryGetValue(workspaceId, out var workspace) ? [.. workspace.Values.Select(e => e.Connection)] : [];

    /// <summary>Enqueues the event for every connection of the workspace except the excluded ones; never waits for a client.</summary>
    public Task SendActivityAsync(Guid workspaceId, ActivityEventDto activity, IReadOnlyCollection<string> excludedConnectionIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(excludedConnectionIds);
        if (!_byWorkspace.TryGetValue(workspaceId, out var workspace))
        {
            return Task.CompletedTask;
        }

        foreach (var entry in workspace.Values)
        {
            if (excludedConnectionIds.Contains(entry.Connection.ConnectionId))
            {
                continue;
            }

            // Gapless or closed: a connection that does not get this event must not get the following ones either.
            try
            {
                if (entry.Outbox.Writer.TryWrite(activity))
                {
                    continue;
                }

                // Not written: the outbox is full, or it was completed because the connection is being removed right now.
                if (_byConnection.ContainsKey(entry.Connection.ConnectionId))
                {
                    LogOverflow(logger, entry.Connection.ConnectionId, OutboxCapacity);
                }
            }
            catch (Exception exception)
            {
                LogEnqueueFailed(logger, exception, entry.Connection.ConnectionId);
            }

            Drop(entry);
        }

        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(Guid workspaceId, IReadOnlyCollection<string> connectionIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectionIds);

        // Close locally first, all of them: nothing below may leave a connection open and receiving. Closes the transport; a
        // client that reconnects goes through tenant resolution again (404 without access).
        foreach (var connectionId in connectionIds)
        {
            if (_byConnection.TryGetValue(connectionId, out var entry))
            {
                Drop(entry);
            }
        }

        // The group is only the tenant boundary (delivery goes per connection), and SignalR leaves it on disconnect anyway.
        foreach (var connectionId in connectionIds)
        {
            try
            {
                await hub.Groups.RemoveFromGroupAsync(connectionId, Group(workspaceId), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                LogLeaveGroupFailed(logger, exception, connectionId);
            }
        }
    }

    /// <summary>Writes the outbox of one connection in order; a write that does not finish in time closes the connection.</summary>
    private async Task PumpAsync(Entry entry)
    {
        var connectionId = entry.Connection.ConnectionId;
        try
        {
            await foreach (var activity in entry.Outbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                // Removed (disconnected or access revoked): nothing pending goes out any more.
                if (!_byConnection.ContainsKey(connectionId))
                {
                    return;
                }

                using var timeout = new CancellationTokenSource(_sendTimeout);

                // WaitAsync as well as the token: the write must not outlive the timeout even if it ignores cancellation.
                await hub.Clients.Client(connectionId)
                    .SendAsync(WorkspaceHub.ActivityMessage, activity, timeout.Token)
                    .WaitAsync(_sendTimeout)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            LogSendTimedOut(logger, connectionId, _sendTimeout);
            Drop(entry);
        }
        catch (Exception exception)
        {
            LogSendFailed(logger, exception, connectionId);
            Drop(entry);
        }
    }

    /// <summary>Removes the connection (no further sends, pending ones are discarded) and closes its transport.</summary>
    private void Drop(Entry entry)
    {
        Remove(entry.Connection.ConnectionId);
        try
        {
            entry.Context.Abort();
        }
        catch (Exception exception)
        {
            // Already removed: it gets nothing more even if the transport did not close; the client times out and reconnects.
            LogAbortFailed(logger, exception, entry.Connection.ConnectionId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Closed realtime connection {ConnectionId}: a message took longer than {Timeout} (client not reading).")]
    private static partial void LogSendTimedOut(ILogger logger, string connectionId, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Information, Message = "Closed realtime connection {ConnectionId}: more than {Capacity} messages pending.")]
    private static partial void LogOverflow(ILogger logger, string connectionId, int capacity);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closed realtime connection {ConnectionId}: sending failed.")]
    private static partial void LogSendFailed(ILogger logger, Exception exception, string connectionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closed realtime connection {ConnectionId}: the event could not be put into its outbox.")]
    private static partial void LogEnqueueFailed(ILogger logger, Exception exception, string connectionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime connection {ConnectionId} is closed but could not leave its workspace group.")]
    private static partial void LogLeaveGroupFailed(ILogger logger, Exception exception, string connectionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime connection {ConnectionId} is removed but its transport could not be aborted.")]
    private static partial void LogAbortFailed(ILogger logger, Exception exception, string connectionId);

    private sealed record Entry(RealtimeConnection Connection, HubCallerContext Context, Channel<ActivityEventDto> Outbox);
}
