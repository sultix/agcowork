using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Agcowork.Api.Realtime;
using Agcowork.Application.Activity;
using Agcowork.Application.Realtime;
using Agcowork.Infrastructure.Realtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agcowork.Api.Tests.Realtime;

/// <summary>
/// Transport rules of the realtime hub without a server (AP-024 review): a client that does not read holds up nobody and is
/// closed; the origin policy of session connections; the dispatcher's backoff.
/// </summary>
public sealed class RealtimeTransportTests
{
    private static readonly Guid WorkspaceA = Guid.CreateVersion7();
    private static readonly Guid WorkspaceB = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RealtimeConnections Connections(FakeHub hub, TimeSpan sendTimeout) =>
        new(hub, Options.Create(new RealtimeOptions { SendTimeout = sendTimeout }), NullLogger<RealtimeConnections>.Instance);

    private static FakeCaller Connect(RealtimeConnections connections, string id, Guid workspaceId)
    {
        var caller = new FakeCaller(id);
        connections.Add(caller, new RealtimeSubscriber(workspaceId, Guid.CreateVersion7(), Guid.CreateVersion7(), null, null));
        return caller;
    }

    private static ActivityEventDto Event(long seq) =>
        new(seq, Guid.CreateVersion7(), "record.created", Guid.CreateVersion7(), "record", Guid.CreateVersion7(), "T", null, null, new JsonObject(), DateTimeOffset.UtcNow);

    [Fact]
    public async Task Send_ClientNotReading_NobodyWaits_ConnectionClosedAfterTimeout()
    {
        var hub = new FakeHub();
        hub.Block("slow");
        var connections = Connections(hub, TimeSpan.FromMilliseconds(200));
        var slow = Connect(connections, "slow", WorkspaceA);
        Connect(connections, "fast", WorkspaceA);
        Connect(connections, "other", WorkspaceB);

        Assert.True(connections.SendActivityAsync(WorkspaceA, Event(1), [], Ct).IsCompletedSuccessfully);
        Assert.True(connections.SendActivityAsync(WorkspaceA, Event(2), [], Ct).IsCompletedSuccessfully);
        Assert.True(connections.SendActivityAsync(WorkspaceB, Event(3), [], Ct).IsCompletedSuccessfully);

        Assert.Equal([1L, 2L], await hub.WaitForAsync("fast", 2));
        Assert.Equal([3L], await hub.WaitForAsync("other", 1));
        await slow.Aborted.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.DoesNotContain(connections.InWorkspace(WorkspaceA), c => c.ConnectionId == "slow");
        Assert.Single(connections.InWorkspace(WorkspaceA));
        Assert.Equal(1, hub.Attempts("slow"));
    }

    [Fact]
    public async Task Send_OutboxOverflows_ConnectionClosedAtOnce()
    {
        var hub = new FakeHub();
        hub.Block("slow");
        var connections = Connections(hub, TimeSpan.FromMinutes(1));
        var slow = Connect(connections, "slow", WorkspaceA);

        for (var seq = 1; seq <= RealtimeConnections.OutboxCapacity + 2; seq++)
        {
            await connections.SendActivityAsync(WorkspaceA, Event(seq), [], Ct);
        }

        await slow.Aborted.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.Empty(connections.InWorkspace(WorkspaceA));
    }

    [Fact]
    public async Task Send_ExcludedConnection_GetsNothing_OthersInOrder()
    {
        var hub = new FakeHub();
        var connections = Connections(hub, TimeSpan.FromSeconds(5));
        Connect(connections, "kept", WorkspaceA);
        Connect(connections, "revoked", WorkspaceA);

        for (var seq = 1; seq <= 20; seq++)
        {
            await connections.SendActivityAsync(WorkspaceA, Event(seq), ["revoked"], Ct);
        }

        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), await hub.WaitForAsync("kept", 20));
        Assert.Equal(0, hub.Attempts("revoked"));
    }

    [Theory]
    [InlineData("http://localhost", "GET", true, "localhost", true)]
    [InlineData("https://agcowork.example", "POST", false, "agcowork.example", true)] // TLS proxy in front: scheme not compared.
    [InlineData("http://localhost:8080", "GET", true, "localhost:8080", true)]
    [InlineData("http://localhost:8080", "GET", true, "localhost", false)] // Port differs.
    [InlineData("https://evil.example", "GET", true, "agcowork.example", false)]
    [InlineData("https://agcowork.example.evil.example", "POST", false, "agcowork.example", false)]
    [InlineData("null", "GET", true, "agcowork.example", false)]
    [InlineData("", "GET", true, "agcowork.example", false)] // WebSocket handshake without Origin.
    [InlineData("", "POST", false, "agcowork.example", false)] // Negotiate without Origin.
    [InlineData("", "GET", false, "agcowork.example", true)] // Same-origin long poll / SSE: browsers send no Origin.
    [InlineData("https://ui.example", "GET", true, "api.internal", true)] // Allowlist.
    [InlineData("http://ui.example", "GET", true, "api.internal", false)] // Allowlist compares the scheme.
    public void OriginPolicy_SessionConnections(string origin, string method, bool isWebSocket, string host, bool allowed)
    {
        Assert.Equal(allowed, RealtimeOriginPolicy.IsAllowed(origin, method, isWebSocket, new HostString(host), ["https://ui.example"]));
    }

    [Fact]
    public void Dispatcher_Backoff_DoublesUpToCap()
    {
        var interval = TimeSpan.FromMilliseconds(250);

        Assert.Equal(TimeSpan.FromMilliseconds(500), ActivityDispatcher.Backoff(interval, 1));
        Assert.Equal(TimeSpan.FromSeconds(16), ActivityDispatcher.Backoff(interval, 6));
        Assert.Equal(ActivityDispatcher.MaxBackoff, ActivityDispatcher.Backoff(interval, 7));
        Assert.Equal(ActivityDispatcher.MaxBackoff, ActivityDispatcher.Backoff(interval, 1000));
    }

    /// <summary>Hub context whose per-connection sends are recorded, or never complete (a client that does not read).</summary>
    private sealed class FakeHub : IHubContext<WorkspaceHub>
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<long>> _received = new();
        private readonly ConcurrentDictionary<string, int> _attempts = new();
        private readonly ConcurrentDictionary<string, bool> _blocked = new();

        public FakeHub() => Clients = new FakeClients(this);

        public IHubClients Clients { get; }

        public IGroupManager Groups { get; } = new NoGroups();

        public void Block(string connectionId) => _blocked[connectionId] = true;

        public int Attempts(string connectionId) => _attempts.GetValueOrDefault(connectionId);

        public async Task<IReadOnlyList<long>> WaitForAsync(string connectionId, int count)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (_received.GetValueOrDefault(connectionId)?.Count is not { } n || n < count)
            {
                await Task.Delay(10, timeout.Token);
            }

            return [.. _received[connectionId]];
        }

        private sealed class FakeClients(FakeHub hub) : IHubClients
        {
            public IClientProxy Client(string connectionId) => new Proxy(hub, connectionId);

            public IClientProxy All => throw new NotSupportedException();

            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

            public IClientProxy Group(string groupName) => throw new NotSupportedException();

            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

            public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

            public IClientProxy User(string userId) => throw new NotSupportedException();

            public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
        }

        private sealed class Proxy(FakeHub hub, string connectionId) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            {
                hub._attempts.AddOrUpdate(connectionId, 1, (_, n) => n + 1);
                if (hub._blocked.ContainsKey(connectionId))
                {
                    // Back-pressure that ignores the token: only the transport's own timeout can end it.
                    return new TaskCompletionSource().Task;
                }

                hub._received.GetOrAdd(connectionId, _ => new ConcurrentQueue<long>()).Enqueue(((ActivityEventDto)args[0]!).Seq);
                return Task.CompletedTask;
            }
        }

        private sealed class NoGroups : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }

    private sealed class FakeCaller(string connectionId) : HubCallerContext
    {
        private readonly TaskCompletionSource _aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Aborted => _aborted.Task;

        public override string ConnectionId { get; } = connectionId;

        public override string? UserIdentifier => null;

        public override ClaimsPrincipal? User => null;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort() => _aborted.TrySetResult();
    }
}
