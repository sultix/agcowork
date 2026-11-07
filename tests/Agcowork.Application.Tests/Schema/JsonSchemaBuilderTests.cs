using System.Text.Json;
using System.Text.Json.Nodes;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Schema;
using Agcowork.Domain.Collections.Schema;
using Json.Schema;
using JsonSchemaBuilder = Agcowork.Application.Schema.JsonSchemaBuilder;
using static Agcowork.Application.Tests.Schema.SchemaFixtures;

namespace Agcowork.Application.Tests.Schema;

public sealed class JsonSchemaBuilderTests
{
    private static readonly Guid CollectionId = Guid.Parse("0190f5e2-7a4c-7000-8000-000000000001");

    private static JsonObject Build(IReadOnlyList<PropertyDefinition>? schema = null) =>
        JsonSchemaBuilder.Build(CollectionId, "Tasks", "Work items. Add one per task.", schema ?? AllTypes);

    private static JsonSchema Compile(JsonObject schema) =>
        JsonSchema.FromText(schema.ToJsonString(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });

    private static bool IsValid(JsonObject schema, string instance) =>
        Compile(schema).Evaluate(JsonDocument.Parse(instance).RootElement).IsValid;

    [Fact]
    public void Build_SmallSchema_MatchesExpectedStructure()
    {
        var schema = Build(
        [
            PropertyDefinition.Title(),
            new() { Key = "priority", Name = "Priority", Type = PropertyType.Select, Description = "How urgent.", Options = [new("high", "High"), new("low", "Low")], Default = JsonSerializer.SerializeToElement("low") },
        ]);

        var expected = JsonNode.Parse("""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$id": "agcowork://collection/0190f5e2-7a4c-7000-8000-000000000001/schema",
              "title": "Tasks",
              "description": "Work items. Add one per task. Optional properties accept null, which clears the value; required properties must not be empty.",
              "type": "object",
              "properties": {
                "title": {
                  "title": "Title",
                  "description": "Title of the record: short, specific, unique enough to find it again.",
                  "type": "string",
                  "maxLength": 10000,
                  "minLength": 1,
                  "examples": ["Use PostgreSQL instead of MongoDB"],
                  "x-agcowork-type": "text"
                },
                "priority": {
                  "title": "Priority",
                  "description": "How urgent.",
                  "type": ["string", "null"],
                  "enum": ["high", "low", null],
                  "default": "low",
                  "examples": ["low"],
                  "x-agcowork-type": "select"
                }
              },
              "required": ["title"],
              "additionalProperties": false
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, schema), schema.ToJsonString());
    }

    [Fact]
    public void Build_AllTypes_IsValidDraft202012()
    {
        var schema = Build();

        var result = MetaSchemas.Draft202012.Evaluate(JsonDocument.Parse(schema.ToJsonString()).RootElement);

        Assert.True(result.IsValid, schema.ToJsonString());
    }

    [Fact]
    public void Build_AllTypes_EnumsRequiredTypesAndReadOnly()
    {
        var properties = Build()["properties"]!.AsObject();

        Assert.Equal(["high", "low", null], properties["priority"]!["enum"]!.AsArray().Select(n => n?.GetValue<string>()));
        Assert.Equal(["a", "b"], properties["tags"]!["items"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(1, properties["status"]!["minLength"]!.GetValue<int>());
        Assert.Equal("string", properties["status"]!["type"]!.GetValue<string>());
        Assert.Equal("in_progress", properties["status"]!["x-agcowork-groups"]!["doing"]!.GetValue<string>());
        Assert.Equal(["integer", "null"], properties["count"]!["type"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("h", properties["estimate"]!["x-agcowork-unit"]!.GetValue<string>());
        Assert.Equal(TargetCollection.ToString(), properties["related"]!["x-agcowork-target-collection"]!.GetValue<string>());
        Assert.Equal(["array", "null"], properties["related"]!["type"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.True(properties["created"]!["readOnly"]!.GetValue<bool>());
        Assert.Equal("datetime", properties["at"]!["x-agcowork-type"]!.GetValue<string>());
        Assert.Equal(["title", "status"], Build()["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(Build()["additionalProperties"]!.GetValue<bool>());
        Assert.All(AllTypes, p => Assert.Equal(PropertyTypes.ToWire(p.Type), properties[p.Key]!["x-agcowork-type"]!.GetValue<string>()));
    }

    [Fact]
    public void Example_IsValidAgainstSchemaAndAcceptedByValidator()
    {
        var example = JsonSchemaBuilder.Example(AllTypes);

        Assert.True(IsValid(Build(), example.ToJsonString()), example.ToJsonString());
        Assert.False(example.ContainsKey("created"));
        Assert.False(example.ContainsKey("affects"));
        Assert.False(example.ContainsKey("related"));
        Assert.False(example.ContainsKey("owner"));
        var errors = new FieldErrors();
        PropertyValueValidator.Validate(AllTypes, Props(example.ToJsonString()), PropertyValidationMode.Full, null, errors);
        Assert.Empty(errors.Items);
    }

    [Fact]
    public void Build_RejectsUnknownKeysWrongEnumAndMissingRequired()
    {
        var schema = Build();

        Assert.False(IsValid(schema, """{ "title": "x", "status": "open", "nope": 1 }"""));
        Assert.False(IsValid(schema, """{ "title": "x", "status": "unknown" }"""));
        Assert.False(IsValid(schema, """{ "title": "x" }"""));
        Assert.False(IsValid(schema, """{ "title": "", "status": "open" }"""));
        Assert.True(IsValid(schema, """{ "title": "x", "status": "open" }"""));
        Assert.True(IsValid(schema, """{ "title": "x", "status": "open", "priority": null, "tags": null, "affects": null, "payload": null }"""));
        Assert.False(IsValid(schema, """{ "title": "x", "status": null }"""));
    }

    [Fact]
    public void Build_RequiredArray_MinItems()
    {
        var schema = Build([PropertyDefinition.Title(), new() { Key = "tags", Name = "Tags", Type = PropertyType.MultiSelect, Required = true, Options = [new("a", "A")] }]);

        Assert.Equal(1, schema["properties"]!["tags"]!["minItems"]!.GetValue<int>());
        Assert.False(IsValid(schema, """{ "title": "x", "tags": [] }"""));
        Assert.True(IsValid(schema, """{ "title": "x", "tags": ["a"] }"""));
    }
}
