using System.Text.Json;
using System.Text.Json.Nodes;
using Agcowork.Application.Collections;
using Agcowork.Application.Collections.Templates;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Query;
using Agcowork.Application.Records;
using Agcowork.Application.Schema;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Tests.Collections;
using Agcowork.Application.Tests.Tenancy;
using Agcowork.Domain.Identity;
using Agcowork.Infrastructure.Query;

namespace Agcowork.Application.Tests.Records;

/// <summary>
/// Record use cases on the seeded workspaces: <see cref="ArrangeAsync"/> creates a space and a collection "Aufgaben"
/// (status required with default, tags, estimate with one decimal, owner actor, notes markdown) in Alpha.
/// </summary>
public abstract class RecordTestBase(PostgresFixture postgres) : SchemaTestBase(postgres)
{
    internal sealed record Arranged(TenancySeed Seed, TenantContext Tenant, Guid SpaceId, CollectionDto Collection);

    internal async Task<Arranged> ArrangeAsync()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);
        var collection = await CreateCollectionAsync(tenant, seed.AlphaOwner, space.Id, "Aufgaben",
            new PropertyDefinitionInput("status", "Status", "select", Required: true, Options: [new("todo"), new("doing"), new("done")], Default: Json("\"todo\"")),
            new PropertyDefinitionInput("tags", "Tags", "multi_select", Options: [new("db"), new("api")]),
            new PropertyDefinitionInput("estimate", "Estimate", "number", Precision: 1),
            new PropertyDefinitionInput("owner", "Owner", "actor"),
            new PropertyDefinitionInput("notes", "Notes", "markdown"));
        return new Arranged(seed, tenant, space.Id, collection);
    }

    /// <summary>The production query translator, for the record reads that need it (backlinks).</summary>
    internal static IRecordQueryTranslator RecordTranslator { get; } = new SqlQueryTranslator();

    internal static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    internal static Dictionary<string, JsonElement> Props(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    internal CreateRecord CreateUseCase(ITenantContext tenant, Actor actor) =>
        new(Db, TestActor.Agent(actor.Id), tenant, Validator(), Activity, Clock);

    internal UpdateRecord UpdateUseCase(ITenantContext tenant, Actor actor) =>
        new(Db, TestActor.Agent(actor.Id), tenant, Validator(), Activity, Clock);

    internal AppendToRecord AppendUseCase(ITenantContext tenant, Actor actor) =>
        new(Db, TestActor.Agent(actor.Id), tenant, Activity, Clock);

    internal async Task<Result<RecordDto>> TryCreateAsync(Arranged a, CreateRecordRequest request)
    {
        Db.ChangeTracker.Clear();
        return await CreateUseCase(a.Tenant, a.Seed.AlphaOwner).HandleAsync(request, Ct);
    }

    internal async Task<RecordDto> CreateRecordAsync(Arranged a, string title = "Schema migrieren", string properties = "{}", string? body = null)
    {
        var result = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: title, Properties: Props(properties), Body: body));
        Assert.True(result.IsSuccess, Describe(result.Error));
        Db.ChangeTracker.Clear();
        return result.Value;
    }

    internal async Task<Result<RecordDto>> UpdateAsync(Arranged a, UpdateRecordRequest request)
    {
        Db.ChangeTracker.Clear();
        return await UpdateUseCase(a.Tenant, a.Seed.AlphaOwner).HandleAsync(request, Ct);
    }

    internal async Task<TemplateDto> CreateTemplateAsync(Arranged a, string preset, string body = "## Kontext\n", bool isDefault = false, string name = "Standard")
    {
        Db.ChangeTracker.Clear();
        var result = await new CreateTemplate(Db, TestActor.Agent(a.Seed.AlphaOwner.Id), a.Tenant, Validator(), Clock)
            .HandleAsync(new CreateTemplateRequest(a.Collection.Id, name, PropertiesPreset: Props(preset), BodyMarkdown: body, IsDefault: isDefault), Ct);
        Assert.True(result.IsSuccess, Describe(result.Error));
        return result.Value;
    }

    /// <summary>A diff entry <c>{ old, new }</c> with the given JSON values (jsonb does not keep member order).</summary>
    internal static void AssertChange(JsonNode? change, string oldJson, string newJson)
    {
        Assert.NotNull(change);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(oldJson), change["old"]), $"old: {change["old"]?.ToJsonString()}");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(newJson), change["new"]), $"new: {change["new"]?.ToJsonString()}");
    }

    internal static string Describe(Error? error) =>
        error is ValidationError v ? string.Join("; ", v.Errors.Select(e => $"{e.Field}:{e.Code}")) : error?.ToString() ?? string.Empty;
}
