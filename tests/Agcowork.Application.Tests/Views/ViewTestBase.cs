using System.Text.Json;
using Agcowork.Application.Collections;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Schema;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Tests.Collections;
using Agcowork.Application.Tests.Tenancy;
using Agcowork.Application.Views;
using Agcowork.Domain.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agcowork.Application.Tests.Views;

/// <summary>Helpers for view use-case tests: a collection with the standard views in workspace alpha.</summary>
public abstract class ViewTestBase(PostgresFixture postgres) : SchemaTestBase(postgres)
{
    protected static readonly string[] OwnerKey = ["owner"];

    internal async Task<(TenancySeed Seed, TenantContext Tenant, CollectionDto Collection)> ArrangeAsync()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);
        var result = await TryCreateCollectionAsync(
            tenant,
            seed.AlphaOwner,
            space.Id,
            "Aufgaben",
            [
                Select("priority", "high", "low"),
                new PropertyDefinitionInput("status", "Status", "status", Options: [new PropertyOptionInput("todo", Group: "todo"), new PropertyOptionInput("done", Group: "done")]),
                new PropertyDefinitionInput("summary", "Summary", "text"),
                new PropertyDefinitionInput("due", "Due", "date"),
            ],
            hooks: new DefaultViewsHook(Db, Clock));
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Db.ChangeTracker.Clear();
        return (seed, tenant, result.Value);
    }

    internal ISchemaProvider Schemas()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgcoworkDbContext>(Db);
        services.AddApplication();
        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<ISchemaProvider>();
    }

    internal CreateView CreateView(TenancySeed seed, TenantContext tenant) =>
        new(Db, Agcowork.Application.Tests.TestActor.Agent(seed.AlphaOwner.Id), tenant, Schemas(), Clock, Activity);

    internal UpdateView UpdateView(TenantContext tenant) => new(Db, tenant, Schemas(), Activity);

    internal DeleteView DeleteView(TenantContext tenant) => new(Db, tenant, Schemas(), Activity);

    internal SetDefaultView SetDefaultView(TenantContext tenant) => new(Db, tenant, Schemas(), Activity);

    internal async Task<ViewDto> NewViewAsync(TenancySeed seed, TenantContext tenant, Guid collectionId, string name, string type = "table", string? groupBy = null, ViewFilterNode? filter = null, bool isDefault = false)
    {
        Db.ChangeTracker.Clear();
        var result = await CreateView(seed, tenant).HandleAsync(new CreateViewRequest(collectionId, name, type, filter, GroupBy: groupBy, IsDefault: isDefault), Ct);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value;
    }

    protected async Task<List<ViewDto>> ListAsync(TenantContext tenant, Guid collectionId)
    {
        Db.ChangeTracker.Clear();
        var result = await new ListViews(Db, tenant).HandleAsync(new ListViewsRequest(collectionId), Ct);
        Assert.True(result.IsSuccess);
        return [.. result.Value];
    }

    protected static ViewFilterNode Cond(string prop, string op = "eq", object? value = null) =>
        new() { Prop = prop, Op = op, Value = op.StartsWith("is_", StringComparison.Ordinal) ? null : JsonSerializer.SerializeToElement(value ?? "x") };

    protected async Task<int> CountViewsAsync(Guid collectionId)
    {
        Db.ChangeTracker.Clear();
        return await Db.Views.CountAsync(v => v.CollectionId == collectionId, Ct);
    }
}
