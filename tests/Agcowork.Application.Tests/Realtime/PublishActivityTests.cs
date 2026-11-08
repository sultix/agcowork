using Agcowork.Application.Activity;
using Agcowork.Application.Realtime;
using Agcowork.Application.Tests.Records;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Tests.Realtime;

/// <summary>
/// The dispatcher's parts (AP-024): the instance-wide reader and the per-workspace delivery, which checks every subscriber's
/// access again before sending (membership, read scope, key state and restriction, active actor, session stamp).
/// </summary>
public sealed class PublishActivityTests(PostgresFixture postgres) : RecordTestBase(postgres)
{
    private static readonly byte[] Hash = new byte[ApiKey.SecretHashLength];
    private int _keys;

    private async Task<ApiKey> IssueKeyAsync(Actor actor, Guid? workspaceId = null, params string[] scopes)
    {
        var key = ApiKey.Issue(actor, $"pref{_keys++:D4}", Hash, scopes, workspaceId, expiresAt: null, Clock);
        Db.ApiKeys.Add(key);
        await Db.SaveChangesAsync(Ct);
        return key;
    }

    private async Task<Actor> AddMemberAsync(Arranged a, Actor actor, LocalLogin? login = null)
    {
        Db.Actors.Add(actor);
        if (login is not null)
        {
            Db.LocalLogins.Add(login);
        }

        Db.Memberships.Add(Membership.Create(a.Seed.Alpha.Id, actor.Id, WorkspaceRole.Editor, null, a.Seed.AlphaOwner.Id, Clock));
        await Db.SaveChangesAsync(Ct);
        return actor;
    }

    private PublishActivity Publisher(FakeConnections connections) =>
        new(Db, new RealtimeTenant(Seeded!.Seed.Alpha.Id), new RealtimeAccess(Db, new RealtimeTenant(Seeded.Seed.Alpha.Id), Clock), connections);

    private Arranged? Seeded { get; set; }

    [Fact]
    public async Task Publish_ChecksEverySubscriberAgain_SendsToTheRestWithTitles()
    {
        var a = Seeded = await ArrangeAsync();
        var record = await CreateRecordAsync(a, "Live");
        var alpha = a.Seed.Alpha.Id;

        var ownerKey = await IssueKeyAsync(a.Seed.AlphaOwner);
        var writeOnlyKey = await IssueKeyAsync(a.Seed.MemberOfBoth, null, "write");
        var betaOnlyKey = await IssueKeyAsync(a.Seed.MemberOfBoth, a.Seed.Beta.Id);
        var revokedKey = await IssueKeyAsync(a.Seed.AlphaOwner);
        revokedKey.Revoke(Clock);
        var outsiderKey = await IssueKeyAsync(a.Seed.BetaOwner);
        var deactivated = await AddMemberAsync(a, Actor.CreateAgent("Deaktiviert", null, null, [], Clock));
        var deactivatedKey = await IssueKeyAsync(deactivated);
        deactivated.Deactivate();
        var human = Actor.CreateHuman("Mensch", "mensch@example.com", null, [], Clock);
        var login = LocalLogin.Create(human, "hash", Clock);
        await AddMemberAsync(a, human, login);
        await Db.SaveChangesAsync(Ct);

        var connections = new FakeConnections(
            ("owner", new RealtimeSubscriber(alpha, a.Seed.AlphaOwner.Id, ownerKey.Id, null, null)),
            ("write-only", new RealtimeSubscriber(alpha, a.Seed.MemberOfBoth.Id, writeOnlyKey.Id, null, null)),
            ("beta-key", new RealtimeSubscriber(alpha, a.Seed.MemberOfBoth.Id, betaOnlyKey.Id, null, null)),
            ("revoked", new RealtimeSubscriber(alpha, a.Seed.AlphaOwner.Id, revokedKey.Id, null, null)),
            ("no-member", new RealtimeSubscriber(alpha, a.Seed.BetaOwner.Id, outsiderKey.Id, null, null)),
            ("deactivated", new RealtimeSubscriber(alpha, deactivated.Id, deactivatedKey.Id, null, null)),
            ("session", new RealtimeSubscriber(alpha, human.Id, null, login.UpdatedAt, null)),
            ("old-password", new RealtimeSubscriber(alpha, human.Id, null, login.UpdatedAt.AddSeconds(-1), null)),
            ("foreign-key", new RealtimeSubscriber(alpha, human.Id, ownerKey.Id, null, null)));
        var events = await Db.ActivityEvents.AsNoTracking().Where(e => e.TargetId == record.Id).ToListAsync(Ct);

        var dropped = await Publisher(connections).PublishAsync(events, Ct);

        string[] revoked = ["beta-key", "deactivated", "foreign-key", "no-member", "old-password", "revoked", "write-only"];
        Assert.Equal(revoked.Length, dropped);
        Assert.Equal(revoked, connections.Disconnected.Order(StringComparer.Ordinal));
        var sent = Assert.Single(connections.Sent);
        Assert.Equal(revoked, sent.Excluded.Order(StringComparer.Ordinal));
        Assert.Equal((ActivityTypes.RecordCreated, record.Id, "Live"), (sent.Activity.Type, sent.Activity.TargetId, sent.Activity.TargetTitle));
        Assert.Equal(["owner", "session"], connections.Live.Select(c => c.ConnectionId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Publish_NoConnections_NothingSent()
    {
        Seeded = await ArrangeAsync();
        var connections = new FakeConnections();
        var events = await Db.ActivityEvents.AsNoTracking().ToListAsync(Ct);

        Assert.Equal(0, await Publisher(connections).PublishAsync(events, Ct));
        Assert.Empty(connections.Sent);
    }

    [Fact]
    public async Task Publish_EventOfAnotherWorkspace_Throws()
    {
        var a = Seeded = await ArrangeAsync();
        var foreign = ActivityEvent.Create(a.Seed.Beta.Id, ActivityTypes.SpaceCreated, a.Seed.BetaOwner.Id, ActivityTargets.Space, Guid.CreateVersion7(), null, null, null, Clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Publisher(new FakeConnections()).PublishAsync([foreign], Ct));
    }

    [Fact]
    public async Task InstanceReader_AllWorkspaces_InCommitOrder_HeadIsLast()
    {
        var a = await ArrangeAsync();
        var beta = Enter(a.Seed.Beta, a.Seed.BetaOwner);
        await CreateSpaceAsync(beta, a.Seed.BetaOwner, "Beta");
        Enter(a.Seed.Alpha, a.Seed.AlphaOwner);
        await CreateRecordAsync(a, "Alpha");
        Tenant.Current = null;
        var reader = new InstanceActivityReader(Db, Instance);

        var all = await reader.ReadAsync(ActivityPosition.Start, 100, Ct);
        var afterFirst = await reader.ReadAsync(ActivityLog.PositionOf(all[0]), 2, Ct);

        Assert.Equal(
            [ActivityTypes.SpaceCreated, ActivityTypes.CollectionCreated, ActivityTypes.SpaceCreated, ActivityTypes.RecordCreated],
            all.Select(e => e.Type));
        Assert.Equal([a.Seed.Alpha.Id, a.Seed.Alpha.Id, a.Seed.Beta.Id, a.Seed.Alpha.Id], all.Select(e => e.WorkspaceId));
        Assert.Equal(all.Skip(1).Take(2).Select(e => e.Id), afterFirst.Select(e => e.Id));
        Assert.Equal(ActivityLog.PositionOf(all[^1]), await reader.HeadAsync(Ct));
        Assert.Empty(await reader.ReadAsync(await reader.HeadAsync(Ct), 100, Ct));
    }

    /// <summary>Records what the publisher does with the transport.</summary>
    private sealed class FakeConnections(params (string Id, RealtimeSubscriber Subscriber)[] connections) : IRealtimeConnections
    {
        public List<RealtimeConnection> Live { get; } = [.. connections.Select(c => new RealtimeConnection(c.Id, c.Subscriber))];

        public List<(ActivityEventDto Activity, IReadOnlyCollection<string> Excluded)> Sent { get; } = [];

        public List<string> Disconnected { get; } = [];

        public IReadOnlyList<RealtimeConnection> InWorkspace(Guid workspaceId) => [.. Live.Where(c => c.Subscriber.WorkspaceId == workspaceId)];

        public Task SendActivityAsync(Guid workspaceId, ActivityEventDto activity, IReadOnlyCollection<string> excludedConnectionIds, CancellationToken cancellationToken)
        {
            Sent.Add((activity, excludedConnectionIds));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(Guid workspaceId, IReadOnlyCollection<string> connectionIds, CancellationToken cancellationToken)
        {
            Disconnected.AddRange(connectionIds);
            Live.RemoveAll(c => connectionIds.Contains(c.ConnectionId));
            return Task.CompletedTask;
        }
    }
}
