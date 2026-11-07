using Agcowork.Application.Common.Results;
using Agcowork.Application.Views;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Tests.Views;

public sealed class ViewTransactionTests(PostgresFixture postgres) : ViewTestBase(postgres)
{
    [Fact]
    public async Task CreateView_InsideOuterTransaction_JoinsAndDoesNotCommit()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        Guid viewId;
        await using (var outer = await Db.Database.BeginTransactionAsync(Ct))
        {
            var result = await CreateView(seed, tenant).HandleAsync(new CreateViewRequest(collection.Id, "Intern", "list"), Ct);

            Assert.True(result.IsSuccess, result.Error?.ToString());
            viewId = result.Value.Id;
            Assert.NotNull(Db.Database.CurrentTransaction);
            await outer.RollbackAsync(Ct);
        }

        Db.ChangeTracker.Clear();
        Assert.False(await Db.Views.AnyAsync(v => v.Id == viewId, Ct));
    }

    [Fact]
    public async Task Writers_InArchivedWorkspace_Conflict()
    {
        var (seed, tenant, collection) = await ArrangeAsync();
        var list = await NewViewAsync(seed, tenant, collection.Id, "Liste", "list");
        var archived = Agcowork.Application.Tests.Tenancy.TenancySeed.TenantFor(Db, seed.Alpha, seed.AlphaOwner, null, archived: true);
        Db.ChangeTracker.Clear();

        var errors = new Error?[]
        {
            (await CreateView(seed, archived).HandleAsync(new CreateViewRequest(collection.Id, "Neu", "list"), Ct)).Error,
            (await UpdateView(archived).HandleAsync(new UpdateViewRequest(list.Id, Name: "X"), Ct)).Error,
            (await DeleteView(archived).HandleAsync(new DeleteViewRequest(list.Id), Ct)).Error,
            (await SetDefaultView(archived).HandleAsync(new SetDefaultViewRequest(list.Id), Ct)).Error,
        };

        Assert.All(errors, e => Assert.Equal("workspace_archived", Assert.IsType<ConflictError>(e).Code));
    }
}
