using Agcowork.Application.Common.Results;
using Agcowork.Application.Views;
using Agcowork.Domain.Activity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Tests.Views;

public sealed class DefaultViewsTests(PostgresFixture postgres) : ViewTestBase(postgres)
{
    private static readonly string[] StatusOnly = ["todo"];
    private static readonly string[] TitleAndDue = ["title", "due"];

    [Fact]
    public async Task CreateCollection_CreatesTableAndRawSystemViews()
    {
        var (_, tenant, collection) = await ArrangeAsync();

        var views = await ListAsync(tenant, collection.Id);

        Assert.Equal(["Tabelle", "Rohdaten"], views.Select(v => v.Name));
        Assert.Equal(["table", "raw"], views.Select(v => v.Type));
        Assert.All(views, v => Assert.True(v.IsSystem));
        Assert.Equal([true, false], views.Select(v => v.IsDefault));
    }

    [Fact]
    public async Task CreateCollection_WithoutHook_ListViewsDoesNotCreateViewsSilently()
    {
        var seed = await Agcowork.Application.Tests.Tenancy.TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);
        var collection = await CreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Alt");

        Assert.Empty(await ListAsync(tenant, collection.Id));
        Assert.Empty(await ListAsync(tenant, collection.Id));
    }

    [Fact]
    public async Task CreateView_BoardWithFilter_StoresAndRecordsActivity()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var filter = new Domain.Views.ViewFilterNode
        {
            And = [Cond("status", "in", StatusOnly), new Domain.Views.ViewFilterNode { Or = [Cond("priority"), Cond("due", "is_empty")] }],
        };

        var view = await NewViewAsync(seed, tenant, collection.Id, "Board", "board", "status", filter);

        Assert.Equal(("board", "status", false, false, 2), (view.Type, view.GroupBy, view.IsDefault, view.IsSystem, view.SortOrder));
        Db.ChangeTracker.Clear();
        var stored = await Db.Views.AsNoTracking().SingleAsync(v => v.Id == view.Id, Ct);
        Assert.Equal("todo", stored.Filter!.And![0].Value!.Value[0].GetString());
        Assert.Equal("due", stored.Filter.And[1].Or![1].Prop);
        var created = await Db.ActivityEvents.AsNoTracking().SingleAsync(e => e.Type == ActivityTypes.ViewCreated, Ct);
        Assert.Equal((ActivityTargets.View, view.Id, collection.Id), (created.TargetType, created.TargetId, created.CollectionId));
    }

    [Fact]
    public async Task CreateView_DuplicateName_Conflict()
    {
        var (seed, tenant, collection) = await ArrangeAsync();

        var result = await CreateView(seed, tenant).HandleAsync(new CreateViewRequest(collection.Id, "Tabelle", "list"), Ct);

        Assert.Equal("view_name_taken", Assert.IsType<ConflictError>(result.Error).Code);
    }

    [Fact]
    public async Task CreateView_OnArchivedCollection_Conflict()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        Assert.True((await new Agcowork.Application.Collections.ArchiveCollection(Db, tenant, Clock, Activity)
            .HandleAsync(new Agcowork.Application.Collections.ArchiveCollectionRequest(collection.Id), Ct)).IsSuccess);
        Db.ChangeTracker.Clear();

        var result = await CreateView(seed, tenant).HandleAsync(new CreateViewRequest(collection.Id, "Neu", "list"), Ct);

        Assert.Equal("collection_archived", Assert.IsType<ConflictError>(result.Error).Code);
    }

    [Fact]
    public async Task CreateView_ForeignCollection_NotFound()
    {
        var (seed, tenant, _) = await ArrangeAsync();

        var result = await CreateView(seed, tenant).HandleAsync(new CreateViewRequest(Guid.CreateVersion7(), "Neu", "list"), Ct);

        Assert.IsType<NotFoundError>(result.Error);
    }

    [Fact]
    public async Task SetDefault_SwitchesDefault_ExactlyOneRemains()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var board = await NewViewAsync(seed, tenant, collection.Id, "Board", "board", "status");

        var result = await SetDefaultView(tenant).HandleAsync(new SetDefaultViewRequest(board.Id), Ct);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsDefault);
        var views = await ListAsync(tenant, collection.Id);
        Assert.Equal(board.Id, Assert.Single(views, v => v.IsDefault).Id);

        // Idempotent.
        Assert.True((await SetDefaultView(tenant).HandleAsync(new SetDefaultViewRequest(board.Id), Ct)).IsSuccess);
        Assert.Single(await ListAsync(tenant, collection.Id), v => v.IsDefault);
    }

    [Fact]
    public async Task CreateView_WithIsDefault_MovesDefault()
    {
        var (seed, tenant, collection) = await ArrangeAsync();

        var list = await NewViewAsync(seed, tenant, collection.Id, "Liste", "list", isDefault: true);

        Assert.Equal(list.Id, Assert.Single(await ListAsync(tenant, collection.Id), v => v.IsDefault).Id);
    }

    [Fact]
    public async Task DeleteView_SystemView_Conflict()
    {
        var (_, tenant, collection) = await ArrangeAsync();
        var raw = (await ListAsync(tenant, collection.Id)).Single(v => v.Type == "raw");

        var result = await DeleteView(tenant).HandleAsync(new DeleteViewRequest(raw.Id), Ct);

        Assert.Equal("system_view", Assert.IsType<ConflictError>(result.Error).Code);
        Assert.Equal(2, await CountViewsAsync(collection.Id));
    }

    [Fact]
    public async Task DeleteView_DefaultView_FirstRemainingBecomesDefault()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var list = await NewViewAsync(seed, tenant, collection.Id, "Liste", "list", isDefault: true);

        var result = await DeleteView(tenant).HandleAsync(new DeleteViewRequest(list.Id), Ct);

        Assert.True(result.IsSuccess);
        var views = await ListAsync(tenant, collection.Id);
        Assert.Equal(["Tabelle", "Rohdaten"], views.Select(v => v.Name));
        Assert.Equal("Tabelle", Assert.Single(views, v => v.IsDefault).Name);
        Assert.Single(Db.ActivityEvents.AsNoTracking().Where(e => e.Type == ActivityTypes.ViewDeleted));
    }

    [Fact]
    public async Task UpdateView_SystemView_RenameAndConfigureButNoTypeChange()
    {
        var (_, tenant, collection) = await ArrangeAsync();
        var table = (await ListAsync(tenant, collection.Id)).Single(v => v.Type == "table");

        var renamed = await UpdateView(tenant).HandleAsync(
            new UpdateViewRequest(table.Id, Name: "Alle", Sorts: [new ViewSortInput("due", "desc")], VisibleProperties: TitleAndDue), Ct);
        Db.ChangeTracker.Clear();
        var retype = await UpdateView(tenant).HandleAsync(new UpdateViewRequest(table.Id, Type: "list"), Ct);

        Assert.True(renamed.IsSuccess, renamed.Error?.ToString());
        Assert.Equal(("Alle", true), (renamed.Value.Name, renamed.Value.IsSystem));
        Assert.Equal(["title", "due"], renamed.Value.VisibleProperties);
        Assert.Equal("not_allowed", Assert.Single(Assert.IsType<ValidationError>(retype.Error).Errors, e => e.Field == "type").Code);
    }

    [Fact]
    public async Task UpdateView_ClearsAndReplaces_PartialUpdateKeepsRest()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var board = await NewViewAsync(seed, tenant, collection.Id, "Board", "board", "status", Cond("priority"));

        var cleared = await UpdateView(tenant).HandleAsync(new UpdateViewRequest(board.Id, Filter: new Domain.Views.ViewFilterNode()), Ct);
        Db.ChangeTracker.Clear();
        var removeGroup = await UpdateView(tenant).HandleAsync(new UpdateViewRequest(board.Id, GroupBy: ""), Ct);
        Db.ChangeTracker.Clear();
        var toList = await UpdateView(tenant).HandleAsync(new UpdateViewRequest(board.Id, Type: "list", GroupBy: ""), Ct);

        Assert.Null(cleared.Value.Filter);
        Assert.Equal("status", cleared.Value.GroupBy);
        Assert.Equal("required", Assert.Single(Assert.IsType<ValidationError>(removeGroup.Error).Errors, e => e.Field == "groupBy").Code);
        Assert.True(toList.IsSuccess, toList.Error?.ToString());
        Assert.Equal(("list", null), (toList.Value.Type, toList.Value.GroupBy));
    }

    [Fact]
    public async Task UpdateView_RenameToExisting_Conflict()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var list = await NewViewAsync(seed, tenant, collection.Id, "Liste", "list");

        var result = await UpdateView(tenant).HandleAsync(new UpdateViewRequest(list.Id, Name: "Rohdaten"), Ct);

        Assert.Equal("view_name_taken", Assert.IsType<ConflictError>(result.Error).Code);
    }
}
