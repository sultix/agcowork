using Agcowork.Application.Collections;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Schema;
using Agcowork.Application.Spaces;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Tests.Tenancy;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Application.Tests.Collections;

/// <summary>Helpers for space/collection/template use-case tests on the seeded workspaces alpha and beta.</summary>
public abstract class SchemaTestBase(PostgresFixture postgres) : DatabaseTest(postgres)
{
    protected FakeTimeProvider Clock { get; } = new(TenancySeed.Now);

    internal TenantContext Enter(Workspace workspace, Actor actor, params string[] keyScopes)
    {
        var tenant = TenancySeed.TenantFor(Db, workspace, actor, keyScopes.Length == 0 ? null : keyScopes.ToHashSet(StringComparer.Ordinal));
        Tenant.Current = tenant;
        return tenant;
    }

    /// <summary>The validator as registered by AddApplication (membership checker on <see cref="DatabaseTest.Db"/>, placeholder relation checker).</summary>
    protected PropertyValueValidator Validator()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgcoworkDbContext>(Db);
        services.AddApplication();
        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<PropertyValueValidator>();
    }

    internal async Task<SpaceDto> CreateSpaceAsync(TenantContext tenant, Actor actor, string name = "Produkt")
    {
        var result = await new CreateSpace(Db, TestActor.Agent(actor.Id), tenant, Clock).HandleAsync(new CreateSpaceRequest(name), Ct);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value;
    }

    internal Task<Result<CollectionDto>> TryCreateCollectionAsync(
        TenantContext tenant,
        Actor actor,
        Guid spaceId,
        string name,
        IReadOnlyList<PropertyDefinitionInput>? schema = null,
        string? description = "Work items of the team. Add one per task.",
        params ICollectionCreatedHook[] hooks) =>
        new CreateCollection(Db, TestActor.Agent(actor.Id), tenant, Clock, hooks).HandleAsync(new CreateCollectionRequest(spaceId, name, description, schema), Ct);

    internal async Task<CollectionDto> CreateCollectionAsync(TenantContext tenant, Actor actor, Guid spaceId, string name, params PropertyDefinitionInput[] schema)
    {
        var result = await TryCreateCollectionAsync(tenant, actor, spaceId, name, schema);
        Assert.True(result.IsSuccess, result.Error is ValidationError v ? string.Join("; ", v.Errors.Select(e => $"{e.Field}:{e.Code}")) : result.Error?.ToString());
        Db.ChangeTracker.Clear();
        return result.Value;
    }

    protected static ValidationError Invalid<T>(Result<T> result) => Assert.IsType<ValidationError>(result.Error);

    protected static FieldError Field(ValidationError error, string field) => Assert.Single(error.Errors, e => e.Field == field);

    protected static PropertyDefinitionInput Select(string key, params string[] options) =>
        new(key, key, "select", Options: [.. options.Select(o => new PropertyOptionInput(o))]);
}
