using Agcowork.Application.Activity;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Realtime;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Tests.Records;
using Agcowork.Domain.Activity;
using Agcowork.Infrastructure.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agcowork.Application.Tests.Realtime;

/// <summary>
/// The dispatcher's failure path (AP-028): a delivery that throws closes every connection of that workspace ("gapless or
/// disconnected"), other workspaces still get their events, and the cursor moves past the batch (no endless retry).
/// </summary>
public sealed class ActivityDispatcherTests(PostgresFixture postgres) : RecordTestBase(postgres)
{
    /// <summary>Events in Alpha (space, collection, record) and Beta (space); returns them in commit order.</summary>
    private async Task<(Arranged A, IReadOnlyList<ActivityEvent> Events)> ArrangeEventsAsync()
    {
        var a = await ArrangeAsync();
        await CreateSpaceAsync(Enter(a.Seed.Beta, a.Seed.BetaOwner), a.Seed.BetaOwner, "Beta");
        Enter(a.Seed.Alpha, a.Seed.AlphaOwner);
        await CreateRecordAsync(a, "Alpha");
        Tenant.Current = null;
        var events = await new InstanceActivityReader(Db, Instance).ReadAsync(ActivityPosition.Start, 100, Ct);
        Assert.Contains(events, e => e.WorkspaceId == a.Seed.Alpha.Id);
        Assert.Contains(events, e => e.WorkspaceId == a.Seed.Beta.Id);
        return (a, events);
    }

    private ActivityDispatcher Dispatcher(FakeConnections connections, FakePublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgcoworkDbContext>(Db);
        services.AddScoped<InstanceActivityReader>();
        services.AddSingleton(Instance);
        services.AddScoped<ITenantContextAccessor, TestTenantAccessor>();
        services.AddSingleton<IActivityPublisher>(publisher);
        var provider = services.BuildServiceProvider();
        return new ActivityDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            connections,
            Options.Create(new RealtimeOptions()),
            TimeProvider.System,
            NullLogger<ActivityDispatcher>.Instance);
    }

    public static TheoryData<string> Failures => ["database", "cancelled-command", "bug"];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Dispatch_PublishThrowsForA_AllOfADisconnected_BStillReceives_CursorMovesOn(string failure)
    {
        var (a, events) = await ArrangeEventsAsync();
        var alpha = a.Seed.Alpha.Id;
        var beta = a.Seed.Beta.Id;
        var connections = new FakeConnections(("a1", alpha), ("a2", alpha), ("b1", beta));
        var publisher = new FakePublisher(alpha, failure switch
        {
            "database" => new InvalidOperationException("Simulated database failure in the access check."),
            "cancelled-command" => new OperationCanceledException("Simulated command timeout (not the host stopping)."),
            _ => new KeyNotFoundException("Simulated bug."),
        });
        var dispatcher = Dispatcher(connections, publisher);

        var (cursor, more) = await dispatcher.DispatchOnceAsync(ActivityPosition.Start, Ct);

        Assert.Equal(["a1", "a2"], connections.Disconnected.Order(StringComparer.Ordinal));
        Assert.Equal(["b1"], connections.Live.Select(c => c.Id));
        var delivered = Assert.Single(publisher.Delivered);
        Assert.Equal(events.Where(e => e.WorkspaceId == beta).Select(e => e.Id), delivered.Select(e => e.Id));
        Assert.Equal(ActivityLog.PositionOf(events[^1]), cursor);
        Assert.False(more);

        // The failed batch is not read again: the next poll finds nothing and publishes nothing.
        var (next, _) = await dispatcher.DispatchOnceAsync(cursor, Ct);
        Assert.Equal(cursor, next);
        Assert.Equal(1, publisher.FailedCalls);
        Assert.Single(publisher.Delivered);
    }

    [Fact]
    public async Task Dispatch_DisconnectFailsToo_NoExceptionOtherWorkspacesStillServed()
    {
        var (a, events) = await ArrangeEventsAsync();
        var connections = new FakeConnections(("a1", a.Seed.Alpha.Id), ("b1", a.Seed.Beta.Id)) { DisconnectThrows = true };
        var publisher = new FakePublisher(a.Seed.Alpha.Id, new InvalidOperationException("Simulated."));

        var (cursor, _) = await Dispatcher(connections, publisher).DispatchOnceAsync(ActivityPosition.Start, Ct);

        Assert.Equal(ActivityLog.PositionOf(events[^1]), cursor);
        Assert.Single(publisher.Delivered);
    }

    [Fact]
    public async Task Dispatch_HostStopping_PropagatesWithoutDisconnecting()
    {
        var (a, _) = await ArrangeEventsAsync();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var connections = new FakeConnections(("a1", a.Seed.Alpha.Id));
        var publisher = new FakePublisher(a.Seed.Alpha.Id, null) { OnFail = stopping.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Dispatcher(connections, publisher).DispatchOnceAsync(ActivityPosition.Start, stopping.Token));

        Assert.Empty(connections.Disconnected);
    }

    [Fact]
    public async Task Dispatch_FailuresInARow_CountedPerWorkspace_ResetBySuccess()
    {
        var (a, _) = await ArrangeEventsAsync();
        var alpha = a.Seed.Alpha.Id;
        var connections = new FakeConnections(("a1", alpha), ("b1", a.Seed.Beta.Id));
        var publisher = new FakePublisher(alpha, new InvalidOperationException("Simulated lasting failure."));
        var dispatcher = Dispatcher(connections, publisher);

        await dispatcher.DispatchOnceAsync(ActivityPosition.Start, Ct);
        connections.Live.Add(("a2", alpha));
        await dispatcher.DispatchOnceAsync(ActivityPosition.Start, Ct);

        Assert.Equal(2, dispatcher.FailuresInARow(alpha));
        Assert.Equal(0, dispatcher.FailuresInARow(a.Seed.Beta.Id));

        publisher.Failing = false;
        connections.Live.Add(("a3", alpha));
        await dispatcher.DispatchOnceAsync(ActivityPosition.Start, Ct);

        Assert.Equal(0, dispatcher.FailuresInARow(alpha));
    }

    [Fact]
    public async Task Dispatch_NoConnectionsInWorkspace_NotPublished()
    {
        var (a, _) = await ArrangeEventsAsync();
        var connections = new FakeConnections(("b1", a.Seed.Beta.Id));
        var publisher = new FakePublisher(a.Seed.Alpha.Id, new InvalidOperationException("Never called for Alpha."));

        await Dispatcher(connections, publisher).DispatchOnceAsync(ActivityPosition.Start, Ct);

        Assert.Equal(0, publisher.FailedCalls);
        Assert.Empty(connections.Disconnected);
        Assert.Single(publisher.Delivered);
    }

    /// <summary>Throws for one workspace (or cancels the host token via <see cref="OnFail"/>), records the others.</summary>
    private sealed class FakePublisher(Guid failingWorkspace, Exception? failure) : IActivityPublisher
    {
        public List<IReadOnlyList<ActivityEvent>> Delivered { get; } = [];

        public int FailedCalls { get; private set; }

        public Action? OnFail { get; init; }

        public bool Failing { get; set; } = true;

        public Task<int> PublishAsync(IReadOnlyList<ActivityEvent> events, CancellationToken cancellationToken)
        {
            if (events[0].WorkspaceId != failingWorkspace || !Failing)
            {
                Delivered.Add(events);
                return Task.FromResult(0);
            }

            FailedCalls++;
            OnFail?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            throw failure!;
        }
    }

    /// <summary>Connections by workspace; records disconnects.</summary>
    private sealed class FakeConnections(params (string Id, Guid WorkspaceId)[] connections) : IRealtimeConnections
    {
        public List<(string Id, Guid WorkspaceId)> Live { get; } = [.. connections];

        public List<string> Disconnected { get; } = [];

        public bool DisconnectThrows { get; init; }

        public IReadOnlyList<RealtimeConnection> InWorkspace(Guid workspaceId) =>
        [
            .. Live.Where(c => c.WorkspaceId == workspaceId)
                .Select(c => new RealtimeConnection(c.Id, new RealtimeSubscriber(workspaceId, Guid.CreateVersion7(), Guid.CreateVersion7(), null, null))),
        ];

        public Task SendActivityAsync(Guid workspaceId, ActivityEventDto activity, IReadOnlyCollection<string> excludedConnectionIds, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DisconnectAsync(Guid workspaceId, IReadOnlyCollection<string> connectionIds, CancellationToken cancellationToken)
        {
            if (DisconnectThrows)
            {
                throw new InvalidOperationException("Simulated transport failure.");
            }

            Disconnected.AddRange(connectionIds);
            Live.RemoveAll(c => connectionIds.Contains(c.Id));
            return Task.CompletedTask;
        }
    }
}
