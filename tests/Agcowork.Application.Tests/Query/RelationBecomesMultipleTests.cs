using Agcowork.Application.Collections;
using Agcowork.Application.Collections.Templates;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Records;
using Agcowork.Application.Schema;
using Agcowork.Application.Views;
using Agcowork.Domain.Views;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Tests.Query;

/// <summary>
/// A relation switched from single to <c>multiple</c> (AP-035): views on it stay valid and match the same records, template
/// presets become arrays, all in the one schema change with every hook (records, templates, views).
/// </summary>
public sealed class RelationBecomesMultipleTests(PostgresFixture postgres) : QueryTestBase(postgres)
{
    private static ViewFilterNode Cond(string op, params Guid[] ids) => new()
    {
        Prop = "affects",
        Op = op,
        Value = op switch
        {
            "in" or "nin" => System.Text.Json.JsonSerializer.SerializeToElement(ids.Select(id => id.ToString())),
            "is_empty" or "is_not_empty" => null,
            _ => System.Text.Json.JsonSerializer.SerializeToElement(ids[0].ToString()),
        },
    };

    [Fact]
    public async Task UpdateSchema_RelationBecomesMultiple_ViewFiltersStayValid_TemplatePresetWrapped()
    {
        var a = await ArrangeAsync();
        var first = await CreateRecordAsync(a, "Ziel 1");
        var second = await CreateRecordAsync(a, "Ziel 2");
        var decisions = await CreateCollectionAsync(a.Tenant, a.Seed.AlphaOwner, a.SpaceId, "Entscheidungen",
            new PropertyDefinitionInput("affects", "Affects", "relation", TargetCollectionId: a.Collection.Id));

        async Task<Guid> CreateDecisionAsync(string title, string properties)
        {
            Db.ChangeTracker.Clear();
            var result = await CreateUseCase(a.Tenant, a.Seed.AlphaOwner).HandleAsync(new CreateRecordRequest(decisions.Id, Title: title, Properties: Props(properties)), Ct);
            Assert.True(result.IsSuccess, Describe(result.Error));
            return result.Value.Id;
        }

        var toFirst = await CreateDecisionAsync("Zu 1", $$"""{ "affects": "{{first.Id}}" }""");
        var toSecond = await CreateDecisionAsync("Zu 2", $$"""{ "affects": "{{second.Id}}" }""");
        var toNone = await CreateDecisionAsync("Ohne", "{}");

        Db.ChangeTracker.Clear();
        var template = await new CreateTemplate(Db, TestActor.Agent(a.Seed.AlphaOwner.Id), a.Tenant, Validator(), Clock)
            .HandleAsync(new CreateTemplateRequest(decisions.Id, "Standard", PropertiesPreset: Props($$"""{ "affects": "{{first.Id}}" }"""), IsDefault: true), Ct);
        Assert.True(template.IsSuccess, Describe(template.Error));

        var filters = new Dictionary<string, (ViewFilterNode Filter, Guid[] Expected)>
        {
            ["contains"] = (Cond("contains", first.Id), [toFirst]),
            ["in"] = (Cond("in", first.Id, second.Id), [toFirst, toSecond]),
            ["nin"] = (Cond("nin", second.Id), [toFirst, toNone]),
            ["is_empty"] = (Cond("is_empty"), [toNone]),
            ["is_not_empty"] = (Cond("is_not_empty"), [toFirst, toSecond]),
            ["not contains"] = (new ViewFilterNode { Not = Cond("contains", first.Id) }, [toSecond, toNone]),
        };
        var views = new Dictionary<string, Guid>();
        foreach (var (name, (filter, _)) in filters)
        {
            Db.ChangeTracker.Clear();
            var created = await new CreateView(Db, TestActor.Agent(a.Seed.AlphaOwner.Id), a.Tenant, Schemas(), Clock, Activity)
                .HandleAsync(new CreateViewRequest(decisions.Id, name, "table", filter), Ct);
            Assert.True(created.IsSuccess, Describe(created.Error));
            views[name] = created.Value.Id;
        }

        Db.ChangeTracker.Clear();
        var changed = await new UpdateSchema(Db, a.Tenant, [new TemplatePresetSchemaHook(Db), new RecordValuesSchemaHook(Db), new ViewSchemaHook(Db)], Activity)
            .HandleAsync(new UpdateSchemaRequest(decisions.Id, Update: [new("affects", Multiple: true)]), Ct);
        Assert.True(changed.IsSuccess, Describe(changed.Error));
        Db.ChangeTracker.Clear();

        // Views: filters unchanged, still valid against the new schema, and they match the same records (values now arrays).
        var schema = (await Db.Collections.AsNoTracking().SingleAsync(c => c.Id == decisions.Id, Ct)).Schema;
        foreach (var (name, (filter, expected)) in filters)
        {
            var view = await Db.Views.AsNoTracking().SingleAsync(v => v.Id == views[name], Ct);
            Assert.True(
                System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.JsonSerializer.SerializeToNode(filter), System.Text.Json.JsonSerializer.SerializeToNode(view.Filter)),
                name);
            var errors = new FieldErrors();
            ViewDefinitionValidator.Validate(errors, schema, new ViewDefinitionInput(null, view.Filter, null, null, null, null));
            Assert.False(errors.Any, name);

            var executed = await new ExecuteView(Db, a.Tenant, Schemas(), Translator).HandleAsync(new ExecuteViewRequest(view.Id, Limit: 50), Ct);
            Assert.True(executed.IsSuccess, Describe(executed.Error));
            Assert.Equal(expected.Order(), executed.Value.Items.Select(r => r.Id).Order());
        }

        // Template preset: the single id became an array with that id; a record from the template gets it.
        var preset = (await Db.Templates.AsNoTracking().SingleAsync(t => t.Id == template.Value.Id, Ct)).PropertiesPreset;
        Assert.Equal($"[\"{first.Id}\"]", preset["affects"].GetRawText());
        var fromTemplate = await CreateDecisionAsync("Aus Vorlage", "{}");
        var stored = await Db.Records.AsNoTracking().SingleAsync(r => r.Id == fromTemplate, Ct);
        Assert.Equal($"[\"{first.Id}\"]", stored.Properties["affects"].GetRawText());
    }
}
