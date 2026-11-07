using System.Text.Json;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Query;
using Agcowork.Application.Views;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Views;

namespace Agcowork.Application.Tests.Query;

public sealed class QueryParserTests
{
    private static readonly Guid Target = Guid.CreateVersion7();

    private static readonly IReadOnlyList<PropertyDefinition> Schema =
    [
        PropertyDefinition.Title(),
        new() { Key = "notes", Name = "Notes", Type = PropertyType.Text },
        new() { Key = "md", Name = "Md", Type = PropertyType.Markdown },
        new() { Key = "site", Name = "Site", Type = PropertyType.Url },
        new() { Key = "mail", Name = "Mail", Type = PropertyType.Email },
        new() { Key = "estimate", Name = "Estimate", Type = PropertyType.Number },
        new() { Key = "done", Name = "Done", Type = PropertyType.Checkbox },
        new() { Key = "status", Name = "Status", Type = PropertyType.Status, Options = [new("todo", "Todo", null, StatusGroup.Todo), new("done", "Done", null, StatusGroup.Done)] },
        new() { Key = "priority", Name = "Priority", Type = PropertyType.Select, Options = [new("high", "High"), new("low", "Low")] },
        new() { Key = "tags", Name = "Tags", Type = PropertyType.MultiSelect, Options = [new("db", "DB"), new("api", "API")] },
        new() { Key = "due", Name = "Due", Type = PropertyType.Date },
        new() { Key = "at", Name = "At", Type = PropertyType.DateTime },
        new() { Key = "component", Name = "Component", Type = PropertyType.Relation, TargetCollectionId = Target },
        new() { Key = "owner", Name = "Owner", Type = PropertyType.Actor },
        new() { Key = "payload", Name = "Payload", Type = PropertyType.Json },
        new() { Key = "created", Name = "Created", Type = PropertyType.CreatedAt },
        new() { Key = "editor", Name = "Editor", Type = PropertyType.UpdatedBy },
    ];

    private static readonly string[] AllKeys =
        ["title", "notes", "md", "site", "mail", "estimate", "done", "status", "priority", "tags", "due", "at", "component", "owner", "payload", "created", "editor", "created_at", "updated_at", "created_by", "updated_by"];

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static ViewFilterNode Node(string json) => JsonSerializer.Deserialize<ViewFilterNode>(json, Web)!;

    private static (FilterNode? Filter, FieldErrors Errors) ParseFilter(string json, IReadOnlyList<PropertyDefinition>? schema = null)
    {
        var errors = new FieldErrors();
        return (QueryParser.ParseFilter(errors, "filter", schema ?? Schema, Node(json)), errors);
    }

    private static FieldError SingleError(string json)
    {
        var (filter, errors) = ParseFilter(json);
        Assert.Null(filter);
        return Assert.Single(errors.Items);
    }

    private static FilterCondition Condition(string json)
    {
        var (filter, errors) = ParseFilter(json);
        Assert.False(errors.Any, string.Join("; ", errors.Items.Select(e => $"{e.Field}:{e.Code}")));
        return Assert.IsType<FilterCondition>(filter);
    }

    [Fact]
    public void ParseFilter_NestedAndOrNot_TypedTree()
    {
        var (filter, errors) = ParseFilter("""
            { "and": [
              { "prop": "status", "op": "in", "value": ["todo", "done"] },
              { "not": { "or": [ { "prop": "estimate", "op": "gt", "value": 3 }, { "prop": "due", "op": "is_empty" } ] } }
            ] }
            """);

        Assert.False(errors.Any);
        var and = Assert.IsType<FilterGroup>(filter);
        Assert.True(and.IsAnd);
        var status = Assert.IsType<FilterCondition>(and.Children[0]);
        Assert.Equal((FilterOperator.In, FilterValueType.Option), (status.Operator, status.Field.ValueType));
        Assert.Equal(["todo", "done"], status.Values);
        var or = Assert.IsType<FilterGroup>(Assert.IsType<FilterNot>(and.Children[1]).Child);
        Assert.False(or.IsAnd);
        Assert.Equal(3m, Assert.IsType<FilterCondition>(or.Children[0]).Value);
        Assert.Empty(Assert.IsType<FilterCondition>(or.Children[1]).Values);
    }

    [Fact]
    public void ParseFilter_UnknownProperty_AllowedListsSchemaKeysAndPseudoFields()
    {
        var error = SingleError("""{ "prop": "nope", "op": "eq", "value": "x" }""");

        Assert.Equal(("filter.prop", ErrorCodes.UnknownProperty), (error.Field, error.Code));
        Assert.Equal(AllKeys, error.Allowed);
    }

    [Fact]
    public void ParseFilter_UnknownOperator_AllowedListsOperatorsOfTheType()
    {
        var known = SingleError("""{ "prop": "estimate", "op": "like", "value": 1 }""");
        var (_, both) = ParseFilter("""{ "prop": "nope", "op": "like", "value": 1 }""");

        Assert.Equal(("filter.op", ErrorCodes.UnknownOperator), (known.Field, known.Code));
        Assert.Equal(["eq", "neq", "gt", "gte", "lt", "lte", "in", "nin", "is_empty", "is_not_empty"], known.Allowed);
        Assert.Equal([ErrorCodes.UnknownProperty, ErrorCodes.UnknownOperator], both.Items.Select(e => e.Code));
        Assert.Equal(ViewFilterOperators.All, both.Items[1].Allowed);
    }

    public static TheoryData<string, string[]> Matrix => new()
    {
        { "notes", ["eq", "neq", "contains", "starts_with", "in", "nin", "is_empty", "is_not_empty"] },
        { "md", ["eq", "neq", "contains", "starts_with", "in", "nin", "is_empty", "is_not_empty"] },
        { "site", ["eq", "neq", "contains", "starts_with", "in", "nin", "is_empty", "is_not_empty"] },
        { "mail", ["eq", "neq", "contains", "starts_with", "in", "nin", "is_empty", "is_not_empty"] },
        { "title", ["eq", "neq", "contains", "starts_with", "in", "nin", "is_empty", "is_not_empty"] },
        { "estimate", ["eq", "neq", "gt", "gte", "lt", "lte", "in", "nin", "is_empty", "is_not_empty"] },
        { "done", ["eq", "neq", "is_empty"] },
        { "status", ["eq", "neq", "in", "nin", "is_empty", "is_not_empty"] },
        { "priority", ["eq", "neq", "in", "nin", "is_empty", "is_not_empty"] },
        { "tags", ["contains", "in", "nin", "is_empty", "is_not_empty"] },
        { "due", ["eq", "neq", "gt", "gte", "lt", "lte", "is_empty", "is_not_empty"] },
        { "at", ["eq", "neq", "gt", "gte", "lt", "lte", "is_empty", "is_not_empty"] },
        { "created", ["eq", "neq", "gt", "gte", "lt", "lte", "is_empty", "is_not_empty"] },
        { "created_at", ["eq", "neq", "gt", "gte", "lt", "lte", "is_empty", "is_not_empty"] },
        { "updated_at", ["eq", "neq", "gt", "gte", "lt", "lte", "is_empty", "is_not_empty"] },
        { "component", ["contains", "in", "nin", "is_empty", "is_not_empty"] },
        { "owner", ["eq", "neq", "in", "nin", "is_empty", "is_not_empty"] },
        { "editor", ["eq", "neq", "in", "nin", "is_empty", "is_not_empty"] },
        { "created_by", ["eq", "neq", "in", "nin", "is_empty", "is_not_empty"] },
        { "updated_by", ["eq", "neq", "in", "nin", "is_empty", "is_not_empty"] },
        { "payload", [] },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void ParseFilter_OperatorMatrix_NotApplicableListsAllowedOfTheType(string prop, string[] allowed)
    {
        foreach (var op in ViewFilterOperators.All.Except(allowed))
        {
            var value = OperatorMatrix.TryParse(op, out var parsed) && OperatorMatrix.TakesNoValue(parsed) ? string.Empty : ", \"value\": [\"x\"]";
            var single = value.Length == 0 || OperatorMatrix.TakesList(parsed) ? value : ", \"value\": \"x\"";
            var error = SingleError($$"""{ "prop": "{{prop}}", "op": "{{op}}"{{single}} }""");

            Assert.Equal(("filter.op", ErrorCodes.OperatorNotApplicable), (error.Field, error.Code));
            Assert.Equal(allowed, error.Allowed);
        }
    }

    public static TheoryData<string> ValidConditions => new()
    {
        """{ "prop": "notes", "op": "contains", "value": "x" }""",
        """{ "prop": "md", "op": "eq", "value": " **a** " }""",
        """{ "prop": "estimate", "op": "in", "value": [1, 2.5] }""",
        """{ "prop": "done", "op": "eq", "value": false }""",
        """{ "prop": "status", "op": "nin", "value": ["todo"] }""",
        """{ "prop": "tags", "op": "contains", "value": "db" }""",
        """{ "prop": "due", "op": "lte", "value": "2026-12-01" }""",
        """{ "prop": "at", "op": "gt", "value": "2026-12-01T10:00:00+02:00" }""",
        """{ "prop": "at", "op": "gt", "value": "2026-12-01" }""",
        """{ "prop": "component", "op": "contains", "value": "0190f5e2-7a4c-7000-8000-000000000001" }""",
        """{ "prop": "owner", "op": "eq", "value": "actor:0190f5e2-7a4c-7000-8000-000000000001" }""",
        """{ "prop": "created_by", "op": "in", "value": ["0190f5e2-7a4c-7000-8000-000000000001"] }""",
        """{ "prop": "title", "op": "starts_with", "value": "ADR" }""",
    };

    [Theory]
    [MemberData(nameof(ValidConditions))]
    public void ParseFilter_ValidConditions(string json) => Condition(json);

    [Fact]
    public void ParseFilter_TypedValues()
    {
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero), Condition("""{ "prop": "at", "op": "gt", "value": "2026-12-01T10:00:00+02:00" }""").Value);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), Condition("""{ "prop": "created_at", "op": "gte", "value": "2026-12-01" }""").Value);
        Assert.Equal(new DateOnly(2026, 12, 1), Condition("""{ "prop": "due", "op": "eq", "value": "2026-12-01" }""").Value);
        Assert.Equal(Guid.Parse(PropertyTypeRegistryExampleId), Condition($$"""{ "prop": "owner", "op": "eq", "value": "actor:{{PropertyTypeRegistryExampleId}}" }""").Value);
        Assert.Equal("Notiz", Condition("""{ "prop": "notes", "op": "eq", "value": " Notiz " }""").Value);
        Assert.Equal(" Notiz ", Condition("""{ "prop": "notes", "op": "contains", "value": " Notiz " }""").Value);
        Assert.Equal(" **a** ", Condition("""{ "prop": "md", "op": "eq", "value": " **a** " }""").Value);
    }

    private const string PropertyTypeRegistryExampleId = "0190f5e2-7a4c-7000-8000-000000000001";

    public static TheoryData<string, string> InvalidValues => new()
    {
        { """{ "prop": "estimate", "op": "gt", "value": "3" }""", "a JSON number" },
        { """{ "prop": "estimate", "op": "gt", "value": 1e40 }""", "too large" },
        { """{ "prop": "done", "op": "eq", "value": "true" }""", "true or false" },
        { """{ "prop": "due", "op": "lt", "value": "01.12.2026" }""", "YYYY-MM-DD" },
        { """{ "prop": "at", "op": "lt", "value": "2026-12-01T10:00:00" }""", "with offset" },
        { """{ "prop": "component", "op": "contains", "value": "rec_1" }""", "record id" },
        { """{ "prop": "owner", "op": "eq", "value": "actor:tech-lead" }""", "actor id" },
        { """{ "prop": "notes", "op": "eq", "value": 3 }""", "a string" },
        { """{ "prop": "status", "op": "in", "value": "todo" }""", "list of values" },
        { """{ "prop": "status", "op": "eq", "value": ["todo"] }""", "single value" },
    };

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void ParseFilter_InvalidValue_MessageNamesExpectedType(string json, string expected)
    {
        var error = SingleError(json);

        Assert.Equal(ErrorCodes.InvalidValue, error.Code);
        Assert.StartsWith("filter.value", error.Field, StringComparison.Ordinal);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> UnstorableValues => new()
    {
        """{ "prop": "notes", "op": "eq", "value": "a\u0000b" }""",
        """{ "prop": "notes", "op": "contains", "value": "\u0000" }""",
        """{ "prop": "notes", "op": "starts_with", "value": "\ud800" }""",
        """{ "prop": "title", "op": "eq", "value": "x\ud800" }""",
        """{ "prop": "title", "op": "contains", "value": "\udc00x" }""",
        """{ "prop": "status", "op": "eq", "value": "\u0000" }""",
        """{ "prop": "owner", "op": "eq", "value": "\ud800" }""",
        """{ "prop": "notes", "op": "in", "value": ["ok", "\u0000"] }""",
    };

    [Theory]
    [MemberData(nameof(UnstorableValues))]
    public void ParseFilter_NulOrUnpairedSurrogate_InvalidValue(string json)
    {
        var error = SingleError(json);

        Assert.Equal(ErrorCodes.InvalidValue, error.Code);
        Assert.Contains("NUL", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseFilter_PairedSurrogates_AreFine() =>
        Assert.Equal("🙂", Condition("""{ "prop": "notes", "op": "contains", "value": "🙂" }""").Value);

    [Theory]
    [InlineData("eq")]
    [InlineData("neq")]
    public void ParseFilter_DateTimeEqWithPlainDate_InvalidValueWithHint(string op)
    {
        var error = SingleError($$"""{ "prop": "at", "op": "{{op}}", "value": "2026-12-01" }""");

        Assert.Equal(ErrorCodes.InvalidValue, error.Code);
        Assert.Contains("gt, gte, lt, lte", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownTopLevelMembers_UnknownPropertyWithAllowed()
    {
        var definition = JsonSerializer.Deserialize<QueryDefinition>("""{ "filters": {}, "sort": [], "limit": 5 }""", Web)!;
        var errors = new FieldErrors();

        Assert.Null(QueryParser.Parse(errors, Schema, definition));
        Assert.Equal([("filters", ErrorCodes.UnknownProperty), ("sort", ErrorCodes.UnknownProperty)], errors.Items.Select(e => (e.Field, e.Code)));
        Assert.Equal(["filter", "sorts", "limit", "cursor", "fields", "includeArchived", "includeCount"], errors.Items[0].Allowed);
    }

    [Fact]
    public void ParseFilter_ListItemErrors_NameTheIndex()
    {
        var (_, errors) = ParseFilter("""{ "prop": "estimate", "op": "in", "value": [1, "zwei", 3, true] }""");

        Assert.Equal(["filter.value[1]", "filter.value[3]"], errors.Items.Select(e => e.Field));
    }

    [Theory]
    [InlineData("""{ "prop": "status", "op": "eq", "value": "verworfen" }""", "filter.value", new[] { "todo", "done" })]
    [InlineData("""{ "prop": "priority", "op": "in", "value": ["high", "mittel"] }""", "filter.value[1]", new[] { "high", "low" })]
    [InlineData("""{ "prop": "tags", "op": "contains", "value": "ui" }""", "filter.value", new[] { "db", "api" })]
    public void ParseFilter_ValueNotInOptions_AllowedListsOptions(string json, string field, string[] allowed)
    {
        var error = SingleError(json);

        Assert.Equal((field, ErrorCodes.NotInOptions), (error.Field, error.Code));
        Assert.Equal(allowed, error.Allowed);
    }

    [Fact]
    public void ParseFilter_ValueRules()
    {
        var (_, errors) = ParseFilter("""
            { "and": [
              { "prop": "status", "op": "eq" },
              { "prop": "status", "op": "is_empty", "value": "x" },
              { "prop": "status", "op": "in", "value": [] },
              { "prop": "status", "op": "eq", "value": null }
            ] }
            """);

        Assert.Equal(
            [("filter.and[0].value", ErrorCodes.Required), ("filter.and[1].value", ErrorCodes.NotAllowed), ("filter.and[2].value", ErrorCodes.Required), ("filter.and[3].value", ErrorCodes.Required)],
            errors.Items.Select(e => (e.Field, e.Code)));
    }

    [Fact]
    public void ParseFilter_Shapes()
    {
        var (_, errors) = ParseFilter("""
            { "and": [
              { },
              { "not": { "prop": "done", "op": "is_empty" }, "prop": "done" },
              { "not": { "all": 1 } },
              { "or": [] }
            ] }
            """);

        Assert.Equal(
            [("filter.and[0]", ErrorCodes.Required), ("filter.and[1]", ErrorCodes.InvalidFormat), ("filter.and[2].not.all", ErrorCodes.UnknownProperty), ("filter.and[3].or", ErrorCodes.Required)],
            errors.Items.Select(e => (e.Field, e.Code)));
        Assert.Equal(["and", "or", "not", "prop", "op", "value"], errors.Items[2].Allowed);
    }

    [Fact]
    public void ParseFilter_EmptyOrNull_IsNoFilter()
    {
        Assert.Equal((null, false), (ParseFilter("{}").Filter, ParseFilter("{}").Errors.Any));
        Assert.Null(QueryParser.ParseFilter(new FieldErrors(), "filter", Schema, null));
    }

    [Fact]
    public void ParseFilter_DepthAndConditionLimits()
    {
        var deep = """{ "prop": "done", "op": "is_empty" }""";
        for (var i = 0; i < QueryParser.MaxFilterDepth; i++)
        {
            deep = $$"""{ "not": {{deep}} }""";
        }

        var many = $$"""{ "or": [ {{string.Join(", ", Enumerable.Repeat("""{ "prop": "done", "op": "is_empty" }""", QueryParser.MaxConditions + 1))}} ] }""";

        Assert.Contains(ParseFilter(deep).Errors.Items, e => e.Code == ErrorCodes.TooLong);
        Assert.Equal(ErrorCodes.TooLong, Assert.Single(ParseFilter(many).Errors.Items).Code);
    }

    [Fact]
    public void ResolveField_PseudoAndComputedMapToColumns_SchemaKeyWins()
    {
        IReadOnlyList<PropertyDefinition> shadowing = [PropertyDefinition.Title(), new() { Key = "updated_at", Name = "Eigenes Feld", Type = PropertyType.Text }];

        Assert.Equal(QueryFieldSource.Title, QueryParser.ResolveField(Schema, "title")!.Source);
        Assert.Equal((QueryFieldSource.CreatedAt, FilterValueType.DateTime), Pick(QueryParser.ResolveField(Schema, "created")!));
        Assert.Equal((QueryFieldSource.UpdatedBy, FilterValueType.Actor), Pick(QueryParser.ResolveField(Schema, "editor")!));
        Assert.Equal((QueryFieldSource.CreatedBy, FilterValueType.Actor), Pick(QueryParser.ResolveField(Schema, "created_by")!));
        Assert.Equal((QueryFieldSource.Property, FilterValueType.Text), Pick(QueryParser.ResolveField(shadowing, "updated_at")!));
        Assert.Equal(["title", "updated_at", "created_at", "created_by", "updated_by"], QueryParser.FieldKeys(shadowing));

        static (QueryFieldSource, FilterValueType) Pick(QueryField f) => (f.Source, f.ValueType);
    }

    [Fact]
    public void ParseSorts_Rules()
    {
        var errors = new FieldErrors();

        var sorts = QueryParser.ParseSorts(errors, "sorts", Schema,
        [
            new("estimate", "desc"), new("created_at", "asc"), new("tags", "asc"), new("payload", "asc"),
            new("component", "asc"), new("estimate", "asc"), new("due", "up"), new(null, null),
        ]);

        Assert.Equal(["estimate:desc", "created_at:asc"], sorts.Select(s => s.Signature));
        Assert.Equal(
            [
                ("sorts[2].prop", ErrorCodes.NotSortable), ("sorts[3].prop", ErrorCodes.NotSortable), ("sorts[4].prop", ErrorCodes.NotSortable),
                ("sorts[5].prop", ErrorCodes.Duplicate), ("sorts[6].dir", ErrorCodes.NotInOptions), ("sorts[7].prop", ErrorCodes.Required), ("sorts[7].dir", ErrorCodes.Required),
            ],
            errors.Items.Select(e => (e.Field, e.Code)));
        Assert.DoesNotContain("tags", errors.Items[0].Allowed!);
        Assert.Contains("created_at", errors.Items[0].Allowed!);
    }

    [Fact]
    public void ParseFields_KeysAndBody()
    {
        var errors = new FieldErrors();

        var fields = QueryParser.ParseFields(errors, "fields", Schema, ["owner", "body", "owner", "nope", ""]);

        Assert.Equal(["owner", "body"], fields);
        Assert.Equal([("fields[3]", ErrorCodes.UnknownProperty), ("fields[4]", ErrorCodes.Required)], errors.Items.Select(e => (e.Field, e.Code)));
        Assert.Contains("body", errors.Items[0].Allowed!);
    }

    [Fact]
    public void Parse_CollectsAllErrors_AndSkipsCursorWhenInvalid()
    {
        var errors = new FieldErrors();

        var query = QueryParser.Parse(errors, Schema, new QueryDefinition(
            Node("""{ "prop": "nope", "op": "eq", "value": 1 }"""), [new ViewSortInput("tags", "asc")], Limit: 0, Cursor: "garbage", Fields: ["x"]));

        Assert.Null(query);
        Assert.Equal(["filter.prop", "sorts[0].prop", "fields[0]", "limit"], errors.Items.Select(e => e.Field));
    }

    [Fact]
    public void Parse_Defaults()
    {
        var query = QueryParser.Parse(new FieldErrors(), Schema, new QueryDefinition())!;

        Assert.Equal((null, 0, QueryParser.DefaultLimit, null, null, false, false), (query.Filter, query.Sorts.Count, query.Limit, query.After, query.Fields, query.IncludeArchived, query.IncludeCount));
    }
}
