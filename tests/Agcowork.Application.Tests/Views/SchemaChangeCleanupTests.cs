using Agcowork.Application.Collections;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Views;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Views;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Tests.Views;

public sealed class SchemaChangeCleanupTests(PostgresFixture postgres) : ViewTestBase(postgres)
{
    private Task<Result<CollectionSchemaDto>> UpdateSchemaAsync(TenantContext tenant, UpdateSchemaRequest request)
    {
        Db.ChangeTracker.Clear();
        return new UpdateSchema(Db, tenant, [new ViewSchemaHook(Db)], Activity).HandleAsync(request, Ct);
    }

    [Fact]
    public async Task UpdateSchema_RemovedProperty_IsCleanedFromViews_BoardNeedsAttention()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var filter = new ViewFilterNode { And = [Cond("summary"), new ViewFilterNode { Or = [Cond("due", "is_empty"), Cond("summary", "contains")] }] };
        var board = await NewViewAsync(seed, tenant, collection.Id, "Board", "board", "status", filter);
        Db.ChangeTracker.Clear();
        var table = (await ListAsync(tenant, collection.Id)).Single(v => v.Type == "table");
        var configured = await UpdateView(tenant).HandleAsync(
            new UpdateViewRequest(table.Id, Sorts: [new ViewSortInput("summary", "asc"), new ViewSortInput("due", "desc")], VisibleProperties: ["title", "summary", "due"]), Ct);
        Assert.True(configured.IsSuccess, configured.Error?.ToString());

        var result = await UpdateSchemaAsync(tenant, new UpdateSchemaRequest(collection.Id, Remove: ["status", "summary"], ConfirmRemoval: true));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var views = await ListAsync(tenant, collection.Id);
        var cleanedBoard = views.Single(v => v.Id == board.Id);
        Assert.Equal(("board", null, true), (cleanedBoard.Type, cleanedBoard.GroupBy, cleanedBoard.NeedsAttention));
        Assert.Equal("due", Assert.Single(Assert.Single(cleanedBoard.Filter!.And!).Or!).Prop);
        var cleanedTable = views.Single(v => v.Id == table.Id);
        Assert.Equal([new ViewSort("due", "desc")], cleanedTable.Sorts);
        Assert.Equal(["title", "due"], cleanedTable.VisibleProperties);
        Assert.All(views.Where(v => v.Id != board.Id), v => Assert.False(v.NeedsAttention));

        var changed = await Db.ActivityEvents.AsNoTracking().SingleAsync(e => e.Type == ActivityTypes.CollectionSchemaChanged, Ct);
        Assert.Equal(2, changed.Diff["affectedViews"]!.GetValue<int>());
    }

    [Fact]
    public async Task UpdateSchema_BoardWithNewGroupBy_NoLongerNeedsAttention()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var board = await NewViewAsync(seed, tenant, collection.Id, "Board", "board", "status");
        await UpdateSchemaAsync(tenant, new UpdateSchemaRequest(collection.Id, Remove: ["status"], ConfirmRemoval: true));

        var fixedUp = await UpdateView(tenant).HandleAsync(new UpdateViewRequest(board.Id, GroupBy: "priority"), Ct);

        Assert.True(fixedUp.IsSuccess, fixedUp.Error?.ToString());
        Assert.False(fixedUp.Value.NeedsAttention);
        Assert.Equal("priority", fixedUp.Value.GroupBy);
    }

    [Fact]
    public async Task UpdateSchema_AdditiveChange_LeavesViewsAndEventUntouched()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        await NewViewAsync(seed, tenant, collection.Id, "Board", "board", "status");

        var result = await UpdateSchemaAsync(tenant, new UpdateSchemaRequest(collection.Id, Add: [new("extra", "Extra", "text")]));

        Assert.True(result.IsSuccess);
        Assert.All(await ListAsync(tenant, collection.Id), v => Assert.False(v.NeedsAttention));
        var changed = await Db.ActivityEvents.AsNoTracking().SingleAsync(e => e.Type == ActivityTypes.CollectionSchemaChanged, Ct);
        Assert.False(changed.Diff.ContainsKey("affectedViews"));
    }

    [Fact]
    public async Task UpdateSchema_CoverPropertyChangesToTypeWithoutCover_CoverIsRemoved_OtherCoversStay()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var markdownCover = await UpdateViewCoverAsync(seed, tenant, collection.Id, "Galerie A", "summary");
        var selectCover = await UpdateViewCoverAsync(seed, tenant, collection.Id, "Galerie B", "priority");

        var result = await UpdateSchemaAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("summary", Type: "markdown")]));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var views = await ListAsync(tenant, collection.Id);
        Assert.Null(views.Single(v => v.Id == markdownCover).CardCoverProperty);
        Assert.Equal("priority", views.Single(v => v.Id == selectCover).CardCoverProperty);
        var changed = await Db.ActivityEvents.AsNoTracking().SingleAsync(e => e.Type == ActivityTypes.CollectionSchemaChanged, Ct);
        Assert.Equal(1, changed.Diff["affectedViews"]!.GetValue<int>());
    }

    private async Task<Guid> UpdateViewCoverAsync(Agcowork.Application.Tests.Tenancy.TenancySeed seed, TenantContext tenant, Guid collectionId, string name, string cover)
    {
        var view = await NewViewAsync(seed, tenant, collectionId, name, "gallery");
        Db.ChangeTracker.Clear();
        var updated = await UpdateView(tenant).HandleAsync(new UpdateViewRequest(view.Id, CardCoverProperty: cover), Ct);
        Assert.True(updated.IsSuccess, updated.Error?.ToString());
        return view.Id;
    }

    [Fact]
    public async Task UpdateView_CoverOnMarkdownProperty_Rejected_ClearWithEmptyStringStillWorks()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        Assert.True((await new UpdateSchema(Db, tenant, [], Activity).HandleAsync(new UpdateSchemaRequest(collection.Id, Add: [new("notes", "Notes", "markdown")]), Ct)).IsSuccess);
        var view = await NewViewAsync(seed, tenant, collection.Id, "Galerie", "gallery");
        Db.ChangeTracker.Clear();

        var rejected = await UpdateView(tenant).HandleAsync(new UpdateViewRequest(view.Id, CardCoverProperty: "notes"), Ct);
        var created = await CreateView(seed, tenant).HandleAsync(new CreateViewRequest(collection.Id, "Zweite", "gallery", CardCoverProperty: "notes"), Ct);
        var cleared = await UpdateView(tenant).HandleAsync(new UpdateViewRequest(view.Id, CardCoverProperty: ""), Ct);

        var error = Assert.Single(Assert.IsType<ValidationError>(rejected.Error).Errors);
        Assert.Equal(("cardCoverProperty", "invalid_type"), (error.Field, error.Code));
        Assert.Contains("summary", error.Allowed!);
        Assert.Equal("cardCoverProperty", Assert.Single(Assert.IsType<ValidationError>(created.Error).Errors).Field);
        Assert.True(cleared.IsSuccess);
    }

    [Fact]
    public async Task UpdateSchema_SelectBecomesMultiSelect_BoardGroupingIsCleared()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var board = await NewViewAsync(seed, tenant, collection.Id, "Nach Prio", "board", "priority");

        var result = await UpdateSchemaAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("priority", Type: "multi_select")]));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var stored = (await ListAsync(tenant, collection.Id)).Single(v => v.Id == board.Id);
        Assert.True(stored.NeedsAttention);
    }

    [Fact]
    public async Task UpdateSchema_ViewsOfOtherCollections_AreNotTouched()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner, "Andere");
        var otherResult = await TryCreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Andere", [new("summary", "Summary", "text")], hooks: new DefaultViewsHook(Db, Clock));
        var other = otherResult.Value;
        Db.ChangeTracker.Clear();
        var otherTable = (await ListAsync(tenant, other.Id)).Single(v => v.Type == "table");
        Assert.True((await UpdateView(tenant).HandleAsync(new UpdateViewRequest(otherTable.Id, VisibleProperties: ["summary"]), Ct)).IsSuccess);

        await UpdateSchemaAsync(tenant, new UpdateSchemaRequest(collection.Id, Remove: ["summary"], ConfirmRemoval: true));

        Assert.Equal(["summary"], (await ListAsync(tenant, other.Id)).Single(v => v.Id == otherTable.Id).VisibleProperties);
    }

    private static readonly string[] HighLow = ["high", "low"];
    private static readonly string[] LowOnly = ["low"];

    [Fact]
    public async Task UpdateSchema_RemovedOption_IsCleanedFromFilterValues()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var filter = new ViewFilterNode
        {
            And =
            [
                Cond("priority", "in", HighLow),
                Cond("priority", "nin", LowOnly),
                Cond("priority", "eq", "low"),
                Cond("priority", "neq", "low"),
                Cond("priority", "eq", "high"),
                Cond("due", "is_empty"),
            ],
        };
        var view = await NewViewAsync(seed, tenant, collection.Id, "Gefiltert", "list", filter: filter);
        var untouched = await NewViewAsync(seed, tenant, collection.Id, "Ohne", "list", filter: Cond("summary"));

        var result = await UpdateSchemaAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("priority", Options: [new("high")])], ConfirmRemoval: true));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var views = await ListAsync(tenant, collection.Id);
        var children = views.Single(v => v.Id == view.Id).Filter!.And!;
        Assert.Equal([("in", "high"), ("eq", "high"), ("is_empty", null)], children.Select(c => (c.Op!, c.Value is { ValueKind: System.Text.Json.JsonValueKind.Array } list ? list[0].GetString() : c.Value?.GetString())));
        Assert.Equal("summary", views.Single(v => v.Id == untouched.Id).Filter!.Prop);
        var changed = await Db.ActivityEvents.AsNoTracking().SingleAsync(e => e.Type == ActivityTypes.CollectionSchemaChanged, Ct);
        Assert.Equal(1, changed.Diff["affectedViews"]!.GetValue<int>());
    }

    [Fact]
    public async Task UpdateSchema_AllInValuesRemoved_ConditionVanishes()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var view = await NewViewAsync(seed, tenant, collection.Id, "Nur low", "list", filter: new ViewFilterNode { And = [Cond("priority", "in", LowOnly)] });

        await UpdateSchemaAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("priority", Options: [new("high")])], ConfirmRemoval: true));

        Assert.Null((await ListAsync(tenant, collection.Id)).Single(v => v.Id == view.Id).Filter);
    }

    [Fact]
    public async Task UpdateSchema_SelectBecomesMultiSelect_FilterEqBecomesContains()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var view = await NewViewAsync(
            seed, tenant, collection.Id, "Prio", "list", filter: new ViewFilterNode { And = [Cond("priority", "eq", "high"), Cond("priority", "neq", "low"), Cond("priority", "in", HighLow), Cond("priority", "nin", LowOnly)] });

        await UpdateSchemaAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("priority", Type: "multi_select")]));

        var children = (await ListAsync(tenant, collection.Id)).Single(v => v.Id == view.Id).Filter!.And!;
        Assert.Equal(["contains", "in", "nin"], children.Select(c => c.Op));
        var changed = await Db.ActivityEvents.AsNoTracking().SingleAsync(e => e.Type == ActivityTypes.CollectionSchemaChanged, Ct);
        Assert.Equal(1, changed.Diff["affectedViews"]!.GetValue<int>());
    }
    [Fact]
    public async Task UpdateSchema_SelectBecomesMultiSelect_SortOnItIsRemoved()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var created = await CreateView(seed, tenant).HandleAsync(
            new CreateViewRequest(collection.Id, "Sortiert", "table", Sorts: [new("priority", "asc"), new("due", "desc")]), Ct);
        Assert.True(created.IsSuccess, created.Error?.ToString());

        await UpdateSchemaAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("priority", Type: "multi_select")]));

        var view = (await ListAsync(tenant, collection.Id)).Single(v => v.Id == created.Value.Id);
        Assert.Equal("due", Assert.Single(view.Sorts).Prop);
        var changed = await Db.ActivityEvents.AsNoTracking().SingleAsync(e => e.Type == ActivityTypes.CollectionSchemaChanged, Ct);
        Assert.Equal(1, changed.Diff["affectedViews"]!.GetValue<int>());
    }

    [Fact]
    public async Task UpdateSchema_RemovedOption_ContainsOnItIsRemovedLikeEq()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        await UpdateSchemaAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("priority", Type: "multi_select")]));
        var view = await NewViewAsync(
            seed, tenant, collection.Id, "Tags", "list", filter: new ViewFilterNode { And = [Cond("priority", "contains", "low"), Cond("priority", "contains", "high")] });

        var result = await UpdateSchemaAsync(tenant, new UpdateSchemaRequest(collection.Id, Update: [new("priority", Options: [new("high")])], ConfirmRemoval: true));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var remaining = (await ListAsync(tenant, collection.Id)).Single(v => v.Id == view.Id).Filter!.And!;
        Assert.Equal("high", Assert.Single(remaining).Value!.Value.GetString());
        var events = await Db.ActivityEvents.AsNoTracking().Where(e => e.Type == ActivityTypes.CollectionSchemaChanged).ToListAsync(Ct);
        // The view exists only for the second change (option removal); that one affected it.
        Assert.Equal(2, events.Count);
        Assert.Equal(1, Assert.Single(events, e => e.Diff["affectedViews"] is not null).Diff["affectedViews"]!.GetValue<int>());
    }
}
