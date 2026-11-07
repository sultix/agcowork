using Agcowork.Application.Activity;
using Agcowork.Application.Collections;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Agcowork.Application.Spaces;
using Agcowork.Application.Tests.Records;
using Agcowork.Application.Workspaces;
using Agcowork.Domain.Activity;

namespace Agcowork.Application.Tests.Activity;

public sealed class ListActivityTests(PostgresFixture postgres) : RecordTestBase(postgres)
{
    private async Task<ActivityPage> ListAsync(Arranged a, ListActivityRequest request)
    {
        var result = await new ListActivity(Db, a.Tenant).HandleAsync(request, Ct);
        Assert.True(result.IsSuccess, Describe(result.Error));
        return result.Value;
    }

    [Fact]
    public async Task List_NewestFirst_WithRetrofittedSpaceCollectionAndMemberEvents()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a);
        Assert.True((await UpdateAsync(a, new UpdateRecordRequest(record.Id, Properties: Props("""{ "status": "done" }""")))).IsSuccess);
        Assert.True((await new UpdateMember(Db, a.Tenant, Activity).HandleAsync(new UpdateMemberRequest(a.Seed.MemberOfBoth.Id, "viewer"), Ct)).IsSuccess);

        var page = await ListAsync(a, new ListActivityRequest());

        Assert.Equal(
            [ActivityTypes.MemberUpdated, ActivityTypes.RecordUpdated, ActivityTypes.RecordCreated, ActivityTypes.CollectionCreated, ActivityTypes.SpaceCreated],
            page.Items.Select(e => e.Type));
        Assert.True(page.Items.Zip(page.Items.Skip(1)).All(p => p.First.Seq > p.Second.Seq));
        Assert.All(page.Items, e => Assert.Equal(a.Seed.AlphaOwner.Id, e.ActorId));
        var member = page.Items[0];
        Assert.Equal((ActivityTargets.Member, a.Seed.MemberOfBoth.Id), (member.TargetType, member.TargetId));
        AssertChange(member.Diff["role"], "\"editor\"", "\"viewer\"");
        Assert.Equal("Aufgaben", page.Items[3].Diff["name"]!.GetValue<string>());
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task List_SchemaSpaceAndCollectionChanges_Recorded()
    {
        var a = await ArrangeAsync();
        Db.ChangeTracker.Clear();
        Assert.True((await new UpdateSchema(Db, a.Tenant, [], Activity).HandleAsync(
            new UpdateSchemaRequest(a.Collection.Id, Add: [new("due", "Due", "date")], Remove: ["notes"], ConfirmRemoval: true), Ct)).IsSuccess);
        Db.ChangeTracker.Clear();
        Assert.True((await new UpdateCollection(Db, a.Tenant, Activity).HandleAsync(new UpdateCollectionRequest(a.Collection.Id, "Tasks"), Ct)).IsSuccess);
        Db.ChangeTracker.Clear();
        Assert.True((await new UpdateSpace(Db, a.Tenant, Activity).HandleAsync(new UpdateSpaceRequest(a.SpaceId, Icon: "📦"), Ct)).IsSuccess);
        Assert.True((await new ArchiveSpace(Db, a.Tenant, Clock, Activity).HandleAsync(new ArchiveSpaceRequest(a.SpaceId), Ct)).IsSuccess);

        var page = await ListAsync(a, new ListActivityRequest(SpaceId: a.SpaceId));

        Assert.Equal(
            [ActivityTypes.SpaceArchived, ActivityTypes.SpaceUpdated, ActivityTypes.CollectionUpdated, ActivityTypes.CollectionSchemaChanged, ActivityTypes.CollectionCreated, ActivityTypes.SpaceCreated],
            page.Items.Select(e => e.Type));
        var schema = page.Items[3].Diff;
        AssertChange(schema["schemaVersion"], "1", "2");
        Assert.Equal("""["due"]""", schema["added"]!.ToJsonString());
        Assert.Equal("""["notes"]""", schema["removed"]!.ToJsonString());
        AssertChange(page.Items[2].Diff["name"], "\"Aufgaben\"", "\"Tasks\"");
        AssertChange(page.Items[1].Diff["icon"], "null", "\"📦\"");
    }

    [Fact]
    public async Task List_Filters_TypesCollectionActorSince()
    {
        var a = await ArrangeAsync();
        var mine = await CreateRecordAsync(a, "Meins");
        Clock.Advance(TimeSpan.FromHours(1));
        var since = Clock.GetUtcNow();
        Db.ChangeTracker.Clear();
        var theirs = await CreateUseCase(Enter(a.Seed.Alpha, a.Seed.MemberOfBoth), a.Seed.MemberOfBoth)
            .HandleAsync(new CreateRecordRequest(SpaceId: a.SpaceId, Title: "Seins"), Ct);
        Enter(a.Seed.Alpha, a.Seed.AlphaOwner);

        var records = await ListAsync(a, new ListActivityRequest(Types: [ActivityTypes.RecordCreated]));
        var inCollection = await ListAsync(a, new ListActivityRequest(CollectionId: a.Collection.Id, Types: [ActivityTypes.RecordCreated]));
        var byOther = await ListAsync(a, new ListActivityRequest(ActorId: a.Seed.MemberOfBoth.Id));
        var recent = await ListAsync(a, new ListActivityRequest(Since: since));

        Assert.Equal([theirs.Value.Id, mine.Id], records.Items.Select(e => e.TargetId));
        Assert.Equal([mine.Id], inCollection.Items.Select(e => e.TargetId));
        Assert.Equal([theirs.Value.Id], byOther.Items.Select(e => e.TargetId));
        Assert.Equal([theirs.Value.Id], recent.Items.Select(e => e.TargetId));
    }

    [Fact]
    public async Task List_CursorPagination_NoGapsNoDuplicates()
    {
        var a = await ArrangeAsync();
        for (var i = 0; i < 5; i++)
        {
            await CreateRecordAsync(a, $"R{i}");
        }

        var all = await ListAsync(a, new ListActivityRequest(Limit: 200));
        var seen = new List<long>();
        string? cursor = null;
        do
        {
            var page = await ListAsync(a, new ListActivityRequest(Limit: 2, Cursor: cursor));
            Assert.True(page.Items.Count <= 2);
            seen.AddRange(page.Items.Select(e => e.Seq));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(all.Items.Select(e => e.Seq), seen);
        Assert.Equal(7, seen.Count);
    }

    [Fact]
    public async Task List_InvalidInput_FieldErrors()
    {
        var a = await ArrangeAsync();

        var result = await new ListActivity(Db, a.Tenant).HandleAsync(new ListActivityRequest(Types: ["record.deleted"], Limit: 0, Cursor: "garbage!"), Ct);

        var error = Assert.IsType<ValidationError>(result.Error);
        Assert.Equal(ActivityTypes.All, Assert.Single(error.Errors, e => e.Field == "types[0]").Allowed);
        Assert.Contains(error.Errors, e => e.Field == "limit" && e.Code == ErrorCodes.OutOfRange);
        Assert.Contains(error.Errors, e => e.Field == "cursor" && e.Code == ErrorCodes.InvalidFormat);
    }

    [Fact]
    public async Task List_OtherWorkspace_Invisible()
    {
        var a = await ArrangeAsync();
        var beta = Enter(a.Seed.Beta, a.Seed.BetaOwner);
        await CreateSpaceAsync(beta, a.Seed.BetaOwner, "Beta-Space");

        var page = await new ListActivity(Db, beta).HandleAsync(new ListActivityRequest(), Ct);

        Assert.Equal([ActivityTypes.SpaceCreated], page.Value.Items.Select(e => e.Type));
        Assert.Equal("Beta-Space", page.Value.Items[0].Diff["name"]!.GetValue<string>());
    }
    [Fact]
    public async Task List_TargetTitle_CurrentTitleOfRecordSpaceAndCollection_NullForMembersAndUnknown()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, "Alter Titel");
        Assert.True((await UpdateAsync(a, new UpdateRecordRequest(record.Id, Title: "Neuer Titel"))).IsSuccess);
        Assert.True((await new UpdateMember(Db, a.Tenant, Activity).HandleAsync(new UpdateMemberRequest(a.Seed.MemberOfBoth.Id, "viewer"), Ct)).IsSuccess);
        Db.ActivityEvents.Add(ActivityEvent.Create(
            a.Seed.Alpha.Id, ActivityTypes.RecordArchived, a.Seed.AlphaOwner.Id, ActivityTargets.Record, Guid.CreateVersion7(), null, null, null, TimeProvider.System));
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();

        var page = await ListAsync(a, new ListActivityRequest());

        Assert.Null(page.Items[0].TargetTitle); // Target never existed (or was deleted).
        Assert.Equal((ActivityTypes.MemberUpdated, (string?)null), (page.Items[1].Type, page.Items[1].TargetTitle));
        Assert.Equal(["Neuer Titel", "Neuer Titel"], page.Items.Where(e => e.TargetId == record.Id).Select(e => e.TargetTitle));
        Assert.Equal("Aufgaben", Assert.Single(page.Items, e => e.Type == ActivityTypes.CollectionCreated).TargetTitle);
        Assert.Equal("Produkt", Assert.Single(page.Items, e => e.Type == ActivityTypes.SpaceCreated).TargetTitle);
    }

    [Fact]
    public async Task List_TargetTitle_TargetInOtherWorkspace_NotResolved()
    {
        var a = await ArrangeAsync();
        var beta = Enter(a.Seed.Beta, a.Seed.BetaOwner);
        var betaSpace = await CreateSpaceAsync(beta, a.Seed.BetaOwner, "Beta-Geheim");
        Db.ChangeTracker.Clear();
        var betaRecord = await CreateUseCase(beta, a.Seed.BetaOwner).HandleAsync(new CreateRecordRequest(SpaceId: betaSpace.Id, Title: "Geheimer Titel"), Ct);
        Assert.True(betaRecord.IsSuccess);
        Enter(a.Seed.Alpha, a.Seed.AlphaOwner);
        // An Alpha event pointing at Beta's targets (cannot happen through the use cases; guards the title lookup).
        Db.ActivityEvents.Add(ActivityEvent.Create(
            a.Seed.Alpha.Id, ActivityTypes.RecordUpdated, a.Seed.AlphaOwner.Id, ActivityTargets.Record, betaRecord.Value.Id, null, null, null, TimeProvider.System));
        Db.ActivityEvents.Add(ActivityEvent.Create(
            a.Seed.Alpha.Id, ActivityTypes.SpaceUpdated, a.Seed.AlphaOwner.Id, ActivityTargets.Space, betaSpace.Id, null, null, null, TimeProvider.System));
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();

        var page = await ListAsync(a, new ListActivityRequest(Limit: 2));

        // Both events come from one SaveChanges, so their seq order is not defined.
        Assert.Equal(new HashSet<Guid> { betaSpace.Id, betaRecord.Value.Id }, page.Items.Select(e => e.TargetId).ToHashSet());
        Assert.All(page.Items, e => Assert.Null(e.TargetTitle));
    }
}
