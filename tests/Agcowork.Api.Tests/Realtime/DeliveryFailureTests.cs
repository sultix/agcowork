using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Agcowork.Api.Realtime;
using Agcowork.Api.Tests.Records;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Activity;
using Agcowork.Application.Realtime;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Agcowork.Infrastructure.Realtime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agcowork.Api.Tests.Realtime;

/// <summary>
/// "Gapless or disconnected" end to end (AP-028): when the delivery to a workspace fails, its hub connections are closed, the
/// client catches up with <c>afterSeq</c> after reconnecting, other workspaces keep receiving, and the transport closes
/// connections even when leaving the group fails.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class DeliveryFailureTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Application.Workspaces.WorkspaceDto Workspace, TestAgent Owner, Guid CollectionId)> ArrangeAsync(string prefix)
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, prefix);
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        return (workspace, owner, collection.Id);
    }

    /// <summary>
    /// Creates records until one arrives live: the dispatcher of a fresh host starts at the head of the log once it runs, so
    /// a record committed just before that is skipped (by design, no replay).
    /// </summary>
    private static async Task<JsonElement> FirstLiveRecordAsync(HubClient hub, TestAgent owner, string slug, Guid collectionId)
    {
        for (var attempt = 1; ; attempt++)
        {
            var record = await RecordsApi.CreateInAsync(owner.Client, slug, collectionId, $"Vorher {attempt}");
            try
            {
                return await hub.WaitForAsync(e => HubClient.Is(e, "record.created", record.Id), TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException) when (attempt < 5)
            {
            }
        }
    }

    [Fact]
    public async Task DeliveryFails_ConnectionsOfThatWorkspaceClosed_ClientCatchesUpWithAfterSeq_OthersKeepReceiving()
    {
        var (workspace, owner, collectionId) = await ArrangeAsync("rtf");
        var (other, otherOwner, otherCollectionId) = await ArrangeAsync("rto");
        var faults = new PublishFaults();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(faults);
            services.AddScoped<IActivityPublisher, FaultyPublisher>();
        }));
        await using var hub = await HubClient.WithKey(host, workspace.Slug, owner.Key).StartAsync();
        await using var otherHub = await HubClient.WithKey(host, other.Slug, otherOwner.Key).StartAsync();
        var seen = (await FirstLiveRecordAsync(hub, owner, workspace.Slug, collectionId)).GetProperty("seq").GetInt64();
        await FirstLiveRecordAsync(otherHub, otherOwner, other.Slug, otherCollectionId);

        // The delivery of the next batch to the workspace throws (e.g. a database error in the access check).
        faults.Failing[workspace.Id] = true;
        var missed = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collectionId, "Verpasst");
        await hub.WaitClosedAsync();
        Assert.DoesNotContain(hub.Received, e => HubClient.Is(e, "record.created", missed.Id));

        // Another workspace is not affected.
        var otherRecord = await RecordsApi.CreateInAsync(otherOwner.Client, other.Slug, otherCollectionId, "Weiter");
        await otherHub.WaitForAsync(e => HubClient.Is(e, "record.created", otherRecord.Id), HubClient.Delivery);
        Assert.Equal(HubConnectionState.Connected, otherHub.Connection.State);

        // The client reconnects and reads forward from the last event it saw: the missed event is there.
        faults.Failing.Clear();
        await using var reconnected = await HubClient.WithKey(host, workspace.Slug, owner.Key).StartAsync();
        var page = await owner.Client.GetFromJsonAsync<JsonElement>(TenancyFixture.Path(workspace.Slug, $"/activity?afterSeq={seen}"), AgcoworkApiFactory.Json, Ct);
        Assert.Contains(page.GetProperty("items").EnumerateArray(), e => HubClient.Is(e, "record.created", missed.Id));

        // Delivery goes on after the failed batch, which is not sent again.
        var after = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collectionId, "Danach");
        await reconnected.WaitForAsync(e => HubClient.Is(e, "record.created", after.Id), HubClient.Delivery);
        Assert.DoesNotContain(reconnected.Received, e => HubClient.Is(e, "record.created", missed.Id));
    }

    [Fact]
    public async Task Disconnect_LeavingTheGroupFails_EveryConnectionStillClosed()
    {
        var connections = new RealtimeConnections(new ThrowingGroupsHub(), Options.Create(new RealtimeOptions()), NullLogger<RealtimeConnections>.Instance);
        var workspaceId = Guid.CreateVersion7();
        var first = Connect(connections, "first", workspaceId);
        var second = Connect(connections, "second", workspaceId);

        await connections.DisconnectAsync(workspaceId, ["first", "second"], Ct);

        Assert.True(first.Aborted);
        Assert.True(second.Aborted);
        Assert.Empty(connections.InWorkspace(workspaceId));

        // Removed connections get nothing more.
        await connections.SendActivityAsync(workspaceId, Event(), [], Ct);
        Assert.Empty(connections.InWorkspace(workspaceId));
    }

    [Fact]
    public async Task Disconnect_AbortThrows_ConnectionStillRemoved_OthersGetTheEvent()
    {
        var hub = new ThrowingGroupsHub();
        var connections = new RealtimeConnections(hub, Options.Create(new RealtimeOptions()), NullLogger<RealtimeConnections>.Instance);
        var workspaceId = Guid.CreateVersion7();
        var broken = Connect(connections, "broken", workspaceId, abortThrows: true);
        Connect(connections, "fine", workspaceId);

        await connections.DisconnectAsync(workspaceId, ["broken"], Ct);
        await connections.SendActivityAsync(workspaceId, Event(), [], Ct);

        Assert.True(broken.Aborted);
        Assert.Equal(["fine"], connections.InWorkspace(workspaceId).Select(c => c.ConnectionId));
        Assert.Equal(["fine"], await hub.WaitForSentAsync(1));
    }

    [Fact]
    public async Task Send_OutboxThrows_OnlyThatConnectionClosed_OthersGetTheEvent()
    {
        var hub = new ThrowingGroupsHub();
        var outboxes = 0;
        var connections = new RealtimeConnections(hub, Options.Create(new RealtimeOptions()), NullLogger<RealtimeConnections>.Instance)
        {
            // The first connection gets an outbox that throws when an event is put into it.
            CreateOutbox = () => Interlocked.Increment(ref outboxes) == 1 ? new ThrowingOutbox() : Channel.CreateUnbounded<ActivityEventDto>(),
        };
        var workspaceId = Guid.CreateVersion7();
        var broken = Connect(connections, "broken", workspaceId);
        var fine = Connect(connections, "fine", workspaceId);

        await connections.SendActivityAsync(workspaceId, Event(), [], Ct);

        Assert.True(broken.Aborted);
        Assert.False(fine.Aborted);
        Assert.Equal(["fine"], connections.InWorkspace(workspaceId).Select(c => c.ConnectionId));
        Assert.Equal(["fine"], await hub.WaitForSentAsync(1));
    }

    /// <summary>An outbox whose writer throws; its reader never yields anything.</summary>
    private sealed class ThrowingOutbox : Channel<ActivityEventDto>
    {
        public ThrowingOutbox()
        {
            var inner = Channel.CreateUnbounded<ActivityEventDto>();
            Reader = inner.Reader;
            Writer = new ThrowingWriter(inner.Writer);
        }

        private sealed class ThrowingWriter(ChannelWriter<ActivityEventDto> inner) : ChannelWriter<ActivityEventDto>
        {
            public override bool TryWrite(ActivityEventDto item) => throw new InvalidOperationException("Simulated outbox failure.");

            public override bool TryComplete(Exception? error = null) => inner.TryComplete(error);

            public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        }
    }

    private static FakeCaller Connect(RealtimeConnections connections, string id, Guid workspaceId, bool abortThrows = false)
    {
        var caller = new FakeCaller(id, abortThrows);
        connections.Add(caller, new RealtimeSubscriber(workspaceId, Guid.CreateVersion7(), Guid.CreateVersion7(), null, null));
        return caller;
    }

    private static ActivityEventDto Event() =>
        new(1, Guid.CreateVersion7(), "record.created", Guid.CreateVersion7(), "record", Guid.CreateVersion7(), "T", null, null, new JsonObject(), DateTimeOffset.UtcNow);

    /// <summary>Workspaces whose delivery fails; switched by the test.</summary>
    private sealed class PublishFaults
    {
        public ConcurrentDictionary<Guid, bool> Failing { get; } = new();
    }

    /// <summary>The real publisher, except for workspaces in <see cref="PublishFaults"/>: there it throws like a failing database.</summary>
    private sealed class FaultyPublisher(PublishActivity inner, ITenantContext tenant, PublishFaults faults) : IActivityPublisher
    {
        public Task<int> PublishAsync(IReadOnlyList<ActivityEvent> events, CancellationToken cancellationToken) =>
            faults.Failing.ContainsKey(tenant.WorkspaceId)
                ? throw new InvalidOperationException("Simulated database failure in the realtime access check.")
                : inner.PublishAsync(events, cancellationToken);
    }

    /// <summary>Hub context whose group operations throw; per-connection sends are recorded.</summary>
    private sealed class ThrowingGroupsHub : IHubContext<WorkspaceHub>
    {
        private readonly ConcurrentQueue<string> _sent = new();

        public ThrowingGroupsHub() => Clients = new SendingClients(this);

        public IHubClients Clients { get; }

        public IGroupManager Groups { get; } = new ThrowingGroups();

        public async Task<IReadOnlyList<string>> WaitForSentAsync(int count)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (_sent.Count < count)
            {
                await Task.Delay(10, timeout.Token);
            }

            return [.. _sent];
        }

        private sealed class SendingClients(ThrowingGroupsHub hub) : IHubClients
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

        private sealed class Proxy(ThrowingGroupsHub hub, string connectionId) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            {
                hub._sent.Enqueue(connectionId);
                return Task.CompletedTask;
            }
        }

        private sealed class ThrowingGroups : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("Simulated group store failure.");
        }
    }

    private sealed class FakeCaller(string connectionId, bool abortThrows) : HubCallerContext
    {
        public bool Aborted { get; private set; }

        public override string ConnectionId { get; } = connectionId;

        public override string? UserIdentifier => null;

        public override ClaimsPrincipal? User => null;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
            Aborted = true;
            if (abortThrows)
            {
                throw new InvalidOperationException("Simulated transport failure on abort.");
            }
        }
    }
}
