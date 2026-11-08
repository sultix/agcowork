using System.Text.Json;
using System.Text.RegularExpressions;
using Agcowork.Application.Collections;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Records;
using Agcowork.Application.Schema;
using Agcowork.Application.Spaces;
using Agcowork.Domain.Collections.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Tests.Records;

/// <summary>
/// Text PostgreSQL cannot store (NUL, unpaired UTF-16 surrogates) is a field error <c>invalid_value</c> in every writing use
/// case (AP-028), never a database exception: records (title, body, properties, append, reason), spaces, collections and
/// their schema.
/// </summary>
public sealed class UnstorableTextTests(PostgresFixture postgres) : RecordTestBase(postgres)
{
    /// <summary>
    /// Strings as a use case may get them (MCP and REST deliver NUL; lone surrogates only from other callers), escaped: the test
    /// framework replaces unpaired surrogates in serialized theory data, so each test unescapes with <see cref="Text"/>.
    /// </summary>
    public static TheoryData<string> BadStrings => ["a\\u0000b", "\\u0000", "a\\ud800b", "\\udfff", "x\\udc00\\ud800y"];

    /// <summary>JSON string literals (escaped) as they arrive in property values.</summary>
    private static readonly string[] BadJson = ["\"a\\u0000b\"", "\"\\ud800\"", "\"x\\udfffy\"", "\"\\udc00\\ud800\""];

    public static TheoryData<string> BadJsonStrings => [.. BadJson];

    /// <summary>Unescapes <c>\uXXXX</c> (also unpaired surrogates and NUL).</summary>
    private static string Text(string escaped) => Regex.Unescape(escaped);

    /// <summary>A JSON string element from the escaped literal (the JSON writer would replace unpaired surrogates).</summary>
    private static JsonElement JsonString(string escaped) => Json($"\"{escaped}\"");

    private static void AssertInvalidValue<T>(Result<T> result, string field)
    {
        var error = Assert.IsType<ValidationError>(result.Error);
        var fieldError = Assert.Single(error.Errors, e => e.Field == field);
        Assert.Equal(ErrorCodes.InvalidValue, fieldError.Code);
        Assert.Contains(StorableText.Hint, fieldError.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BadStrings))]
    public async Task CreateRecord_UnstorableTitleBodyOrKey_InvalidValueOnTheField(string escaped)
    {
        var bad = Text(escaped);
        var a = await ArrangeAsync();
        var before = await Db.Records.CountAsync(Ct);

        AssertInvalidValue(await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: bad)), "title");
        AssertInvalidValue(await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Ok", Body: bad)), "body");
        AssertInvalidValue(await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Ok", IdempotencyKey: bad)), "Idempotency-Key");
        AssertInvalidValue(await TryCreateAsync(a, new CreateRecordRequest(SpaceId: a.SpaceId, Title: bad)), "title");

        Assert.Equal(before, await Db.Records.CountAsync(Ct));
    }

    [Theory]
    [MemberData(nameof(BadJsonStrings))]
    public async Task CreateRecord_UnstorableProperty_InvalidValueOnTheProperty(string bad)
    {
        var a = await ArrangeAsync();

        AssertInvalidValue(await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Properties: Props($$"""{ "title": {{bad}} }"""))), "properties.title");
        AssertInvalidValue(await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Ok", Properties: Props($$"""{ "notes": {{bad}} }"""))), "properties.notes");
        AssertInvalidValue(await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Ok", Properties: Props($$"""{ "status": {{bad}} }"""))), "properties.status");
        AssertInvalidValue(await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Ok", Properties: Props($$"""{ "tags": ["db", {{bad}}] }"""))), "properties.tags[1]");
        AssertInvalidValue(await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Ok", Properties: Props($$"""{ "owner": {{bad}} }"""))), "properties.owner");
    }

    [Theory]
    [MemberData(nameof(BadStrings))]
    public async Task UpdateAndAppend_UnstorableText_InvalidValueOnTheField(string escaped)
    {
        var bad = Text(escaped);
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, "Vorher", body: "Text");

        AssertInvalidValue(await UpdateAsync(a, new UpdateRecordRequest(record.Id, Title: bad)), "title");
        AssertInvalidValue(await UpdateAsync(a, new UpdateRecordRequest(record.Id, Body: bad)), "body");
        AssertInvalidValue(await UpdateAsync(a, new UpdateRecordRequest(record.Id, Title: "Neu", Reason: bad)), "reason");

        var append = AppendUseCase(a.Tenant, a.Seed.AlphaOwner);
        Db.ChangeTracker.Clear();
        AssertInvalidValue(await append.HandleAsync(new AppendToRecordRequest(record.Id, bad), Ct), "markdown");
        AssertInvalidValue(await append.HandleAsync(new AppendToRecordRequest(record.Id, "- Eintrag", Section: bad), Ct), "section");
        AssertInvalidValue(await append.HandleAsync(new AppendToRecordRequest(record.Id, "- Eintrag", Reason: bad), Ct), "reason");

        Db.ChangeTracker.Clear();
        var unchanged = await Db.Records.AsNoTracking().SingleAsync(r => r.Id == record.Id, Ct);
        Assert.Equal(("Vorher", "Text", 1), (unchanged.Title, unchanged.Body, unchanged.RevisionNo));
    }

    [Theory]
    [MemberData(nameof(BadJsonStrings))]
    public async Task UpdateRecord_UnstorableProperty_InvalidValueOnTheProperty(string bad)
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a);

        AssertInvalidValue(await UpdateAsync(a, new UpdateRecordRequest(record.Id, Properties: Props($$"""{ "notes": {{bad}} }"""))), "properties.notes");
        AssertInvalidValue(await UpdateAsync(a, new UpdateRecordRequest(record.Id, Properties: Props($$"""{ "title": {{bad}} }"""))), "properties.title");
    }

    [Fact]
    public async Task Records_PairedSurrogatesAndOtherControlCharacters_AreStored()
    {
        var a = await ArrangeAsync();

        var record = await CreateRecordAsync(a, "Rakete 🚀", """{ "notes": "Tab\tund 😀" }""", "Zeile\u0001 mit 𝄞");

        Assert.Equal("Rakete 🚀", record.Title);
        Assert.Equal("Zeile\u0001 mit 𝄞", (await Db.Records.AsNoTracking().SingleAsync(r => r.Id == record.Id, Ct)).Body);
    }

    [Theory]
    [MemberData(nameof(BadStrings))]
    public async Task Spaces_UnstorableNameDescriptionIcon_InvalidValue(string escaped)
    {
        var bad = Text(escaped);
        var a = await ArrangeAsync();
        var create = new CreateSpace(Db, TestActor.Agent(a.Seed.AlphaOwner.Id), a.Tenant, Clock, Activity);
        var update = new UpdateSpace(Db, a.Tenant, Activity);

        AssertInvalidValue(await create.HandleAsync(new CreateSpaceRequest(bad), Ct), "name");
        AssertInvalidValue(await create.HandleAsync(new CreateSpaceRequest("Ok", Description: bad), Ct), "description");
        AssertInvalidValue(await create.HandleAsync(new CreateSpaceRequest("Ok", Icon: bad), Ct), "icon");
        AssertInvalidValue(await update.HandleAsync(new UpdateSpaceRequest(a.SpaceId, Name: bad), Ct), "name");
        AssertInvalidValue(await update.HandleAsync(new UpdateSpaceRequest(a.SpaceId, Description: bad), Ct), "description");
    }

    [Theory]
    [MemberData(nameof(BadStrings))]
    public async Task Collections_UnstorableNameDescriptionOrSchemaText_InvalidValue(string escaped)
    {
        var bad = Text(escaped);
        var a = await ArrangeAsync();
        var owner = a.Seed.AlphaOwner;

        AssertInvalidValue(await TryCreateCollectionAsync(a.Tenant, owner, a.SpaceId, bad), "name");
        AssertInvalidValue(await TryCreateCollectionAsync(a.Tenant, owner, a.SpaceId, "Ok", description: bad), "description");
        AssertInvalidValue(await TryCreateCollectionAsync(a.Tenant, owner, a.SpaceId, "Ok", [new("x", bad, "text")]), "schema[0].name");
        AssertInvalidValue(await TryCreateCollectionAsync(a.Tenant, owner, a.SpaceId, "Ok", [new("x", "X", "text", Description: bad)]), "schema[0].description");
        AssertInvalidValue(await TryCreateCollectionAsync(a.Tenant, owner, a.SpaceId, "Ok", [new("x", "X", "number", Unit: bad)]), "schema[0].unit");
        AssertInvalidValue(await TryCreateCollectionAsync(a.Tenant, owner, a.SpaceId, "Ok", [new("x", "X", "select", Options: [new("a"), new(bad)])]), "schema[0].options[1].value");
        AssertInvalidValue(await TryCreateCollectionAsync(a.Tenant, owner, a.SpaceId, "Ok", [new("x", "X", "select", Options: [new("a", Label: bad)])]), "schema[0].options[0].label");
        AssertInvalidValue(await TryCreateCollectionAsync(a.Tenant, owner, a.SpaceId, "Ok", [new("x", "X", "text", Default: JsonString(escaped))]), "schema[0].default");

        AssertInvalidValue(await new UpdateCollection(Db, a.Tenant, Activity).HandleAsync(new UpdateCollectionRequest(a.Collection.Id, Name: bad), Ct), "name");
        AssertInvalidValue(await new UpdateCollection(Db, a.Tenant, Activity).HandleAsync(new UpdateCollectionRequest(a.Collection.Id, Description: bad), Ct), "description");
    }

    [Theory]
    [MemberData(nameof(BadJsonStrings))]
    public async Task UpdateSchema_UnstorableNamesOptionsOrDefault_InvalidValue(string bad)
    {
        var a = await ArrangeAsync();
        var update = new UpdateSchema(Db, a.Tenant, [], Activity);

        foreach (var text in new[] { "a\0b", "\ud800" })
        {
            AssertInvalidValue(
                await update.HandleAsync(new UpdateSchemaRequest(a.Collection.Id, Update: [new PropertyChangeInput("tags", Name: text)]), Ct),
                "update[0].name");
            AssertInvalidValue(
                await update.HandleAsync(new UpdateSchemaRequest(a.Collection.Id, Update: [new PropertyChangeInput("tags", Options: [new("db"), new("api", Label: text)])]), Ct),
                "update[0].options[1].label");
            AssertInvalidValue(
                await update.HandleAsync(new UpdateSchemaRequest(a.Collection.Id, Add: [new PropertyDefinitionInput("extra", text, "text")]), Ct),
                "add[0].name");
        }

        AssertInvalidValue(
            await update.HandleAsync(new UpdateSchemaRequest(a.Collection.Id, Update: [new PropertyChangeInput("status", Default: Json(bad))]), Ct),
            "update[0].default");
        AssertInvalidValue(
            await update.HandleAsync(new UpdateSchemaRequest(a.Collection.Id, Add: [new PropertyDefinitionInput("extra", "Extra", "json", Default: Json($$"""{ "k": [{{bad}}] }"""))]), Ct),
            "add[0].default");
    }

    public static TheoryData<string, string> TypedValues => new()
    {
        { "text", "{0}" },
        { "markdown", "{0}" },
        { "select", "{0}" },
        { "status", "{0}" },
        { "multi_select", "[{0}]" },
        { "date", "{0}" },
        { "datetime", "{0}" },
        { "url", "{0}" },
        { "email", "{0}" },
        { "relation", "{0}" },
        { "actor", "{0}" },
        { "json", "{{ \"k\": {0} }}" },
        { "json", "[1, {{ \"deep\": [{0}] }}]" },
    };

    [Theory]
    [MemberData(nameof(TypedValues))]
    public void PropertyTypes_UnstorableText_InvalidValue_NeverThrows(string type, string template)
    {
        foreach (var bad in BadJson)
        {
            var property = Definition(type);
            var value = Json(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, bad));
            var errors = new FieldErrors();

            var normalized = PropertyTypeRegistry.Get(property.Type).Normalize(property, value);
            PropertyTypeRegistry.CheckValue(property, normalized, "properties.x", errors);
            PropertyValueValidator.Validate([property], new Dictionary<string, JsonElement> { ["x"] = value }, PropertyValidationMode.Partial, "p", errors);

            Assert.Equal(value.GetRawText(), normalized.GetRawText());
            Assert.Contains(errors.Items, e => e.Code == ErrorCodes.InvalidValue && e.Field.StartsWith("properties.x", StringComparison.Ordinal));
            Assert.Contains(errors.Items, e => e.Code == ErrorCodes.InvalidValue && e.Field.StartsWith("p.x", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Json_UnstorableMemberName_InvalidValue()
    {
        var errors = new FieldErrors();

        PropertyTypeRegistry.CheckValue(Definition("json"), Json("""{ "ok": 1, "a\u0000": 2 }"""), "data", errors);
        PropertyTypeRegistry.CheckValue(Definition("json"), Json("""{ "nested": { "\ud800": 2 } }"""), "data", errors);

        Assert.Equal(2, errors.Items.Count(e => e is { Field: "data", Code: ErrorCodes.InvalidValue }));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("Normal text\\twith\\ncontrol\\u0001characters", true)]
    [InlineData("\\ud83d\\ude00 \\ud834\\udd1e", true)]
    [InlineData("a\\u0000", false)]
    [InlineData("\\ud83d", false)]
    [InlineData("\\ude00", false)]
    [InlineData("\\ude00\\ud83d", false)]
    [InlineData("ok\\ud83d\\ude00\\ud83d", false)]
    public void StorableText_StringsAndJsonElements(string unescaped, bool storable)
    {
        var text = Text(unescaped);

        // Every character escaped (\uXXXX), as a client may send it: the JSON writer would replace unpaired surrogates.
        var escaped = string.Concat(text.Select(c => $"\\u{(int)c:x4}"));
        var element = JsonDocument.Parse($$"""[{ "k": "{{escaped}}" }]""").RootElement;
        var asName = JsonDocument.Parse($$"""{ "{{escaped}}": 1 }""").RootElement;

        Assert.Equal(storable, StorableText.IsStorable(text));
        Assert.Equal(storable, StorableText.IsStorable(element));
        Assert.Equal(storable, StorableText.IsStorable(asName));
        Assert.Equal(storable, StorableText.TryRead(element[0].GetProperty("k"), out _));
    }

    private static PropertyDefinition Definition(string type)
    {
        Assert.True(PropertyTypes.TryParse(type, out var parsed));
        return new PropertyDefinition
        {
            Key = "x",
            Name = "X",
            Type = parsed,
            Options = parsed is PropertyType.Select or PropertyType.MultiSelect or PropertyType.Status
                ? [new PropertyOption("a", "a", null, parsed == PropertyType.Status ? StatusGroup.Todo : null)]
                : null,
            TargetCollectionId = parsed == PropertyType.Relation ? Guid.CreateVersion7() : null,
        };
    }
}
