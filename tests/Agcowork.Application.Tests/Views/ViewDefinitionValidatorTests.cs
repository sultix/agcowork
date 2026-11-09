using System.Text.Json;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Views;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Views;

namespace Agcowork.Application.Tests.Views;

public sealed class ViewDefinitionValidatorTests
{
    private static readonly IReadOnlyList<PropertyDefinition> Schema =
    [
        PropertyDefinition.Title(),
        new() { Key = "status", Name = "Status", Type = PropertyType.Status, Options = [new("todo", "Todo", null, StatusGroup.Todo), new("done", "Done", null, StatusGroup.Done)] },
        new() { Key = "priority", Name = "Priority", Type = PropertyType.Select, Options = [new("high", "High"), new("low", "Low")] },
        new() { Key = "due", Name = "Due", Type = PropertyType.Date },
    ];

    private static readonly int[] One = [1];
    private static readonly string[] StatusOnly = ["todo"];
    private static readonly string[] TitleAndDue = ["title", "due"];
    private static readonly string[] TitleNopeTitle = ["title", "nope", "title"];

    private static ViewFilterNode Cond(string prop, string op, object? value = null) =>
        new() { Prop = prop, Op = op, Value = value is null ? null : JsonSerializer.SerializeToElement(value) };

    private static FieldErrors Run(ViewDefinitionInput input, out ViewType? type)
    {
        var errors = new FieldErrors();
        type = ViewDefinitionValidator.Validate(errors, Schema, input);
        return errors;
    }

    private static ViewDefinitionInput Filter(ViewFilterNode filter) => new("table", filter, null, null, null, null);

    [Fact]
    public void Validate_FullBoardDefinition_NoErrors()
    {
        var filter = new ViewFilterNode
        {
            And = [Cond("status", "in", StatusOnly), new ViewFilterNode { Or = [Cond("due", "lt", "2026-12-01"), Cond("priority", "eq", "high"), Cond("due", "is_empty")] }],
        };

        var errors = Run(new ViewDefinitionInput("board", filter, [new ViewSortInput("due", "asc")], "status", TitleAndDue, "due"), out var type);

        Assert.False(errors.Any, string.Join("; ", errors.Items.Select(e => $"{e.Field}:{e.Code}")));
        Assert.Equal(ViewType.Board, type);
    }

    [Theory]
    [InlineData("timeline")]
    [InlineData("calendar")]
    [InlineData("Table")]
    [InlineData("kanban")]
    public void Validate_UnsupportedType_AllowedListsTypes(string wire)
    {
        var errors = Run(new ViewDefinitionInput(wire, null, null, null, null, null), out var type);

        Assert.Null(type);
        var error = Assert.Single(errors.Items);
        Assert.Equal(("type", "not_in_options"), (error.Field, error.Code));
        Assert.Equal(["table", "board", "gallery", "list", "document", "raw"], error.Allowed);
    }

    [Fact]
    public void Validate_UnknownOperator_AllowedListsOperatorsOfTheType()
    {
        var errors = Run(Filter(new ViewFilterNode { And = [Cond("status", "like", "x")] }), out _);

        var error = Assert.Single(errors.Items);
        Assert.Equal(("filter.and[0].op", "unknown_operator"), (error.Field, error.Code));
        Assert.Equal(["eq", "neq", "in", "nin", "is_empty", "is_not_empty"], error.Allowed);
    }

    [Fact]
    public void Validate_UsesTheQueryParser_OperatorMatrixAndOptionValues()
    {
        var errors = Run(Filter(new ViewFilterNode
        {
            And = [Cond("due", "contains", "2026"), Cond("priority", "eq", "mittel"), new ViewFilterNode { Not = Cond("status", "eq", "done") }],
        }), out _);

        Assert.Equal([("filter.and[0].op", "operator_not_applicable"), ("filter.and[1].value", "not_in_options")], errors.Items.Select(e => (e.Field, e.Code)));
        Assert.Equal(["eq", "neq", "gt", "gte", "lt", "lte", "is_empty", "is_not_empty"], errors.Items[0].Allowed);
        Assert.Equal(["high", "low"], errors.Items[1].Allowed);
    }

    [Fact]
    public void Validate_UnknownProp_AllowedListsKeys()
    {
        var errors = Run(Filter(new ViewFilterNode { Or = [Cond("nope", "eq", "x")] }), out _);

        var error = Assert.Single(errors.Items);
        Assert.Equal(("filter.or[0].prop", "unknown_property"), (error.Field, error.Code));
        Assert.Equal(["title", "status", "priority", "due", "created_at", "updated_at", "created_by", "updated_by"], error.Allowed);
    }

    [Fact]
    public void Validate_ConditionValueRules()
    {
        var errors = Run(Filter(new ViewFilterNode
        {
            And =
            [
                Cond("status", "in", "todo"),
                Cond("status", "eq"),
                Cond("status", "eq", StatusOnly),
                Cond("status", "is_empty", "ignored"),
                Cond("status", "is_not_empty", "ignored"),
                Cond("status", "is_not_empty"),
            ],
        }), out _);

        Assert.Equal(
            [("filter.and[0].value", "invalid_value"), ("filter.and[1].value", "required"), ("filter.and[2].value", "invalid_value"), ("filter.and[3].value", "not_allowed"), ("filter.and[4].value", "not_allowed")],
            errors.Items.Select(e => (e.Field, e.Code)));
    }

    private static ViewFilterNode Unknown(string key, ViewFilterNode? inner = null) =>
        new() { Unknown = new Dictionary<string, JsonElement> { [key] = JsonSerializer.SerializeToElement(inner is null ? (object)One : inner) } };

    [Fact]
    public void Validate_UnknownFilterKeys_AreErrorsWithAllowedKeys()
    {
        var errors = Run(Filter(new ViewFilterNode
        {
            And = [Unknown("nicht"), new ViewFilterNode { Or = [Cond("status", "eq", "todo"), Unknown("all")] }],
        }), out _);

        Assert.Equal([("filter.and[0].nicht", "unknown_property"), ("filter.and[1].or[1].all", "unknown_property")], errors.Items.Select(e => (e.Field, e.Code)));
        Assert.All(errors.Items, e => Assert.Equal(["and", "or", "not", "prop", "op", "value"], e.Allowed));
    }

    [Fact]
    public void Validate_RootWithOnlyUnknownKey_IsNotAnEmptyFilter()
    {
        var errors = Run(Filter(Unknown("all")), out _);

        Assert.Equal("filter.all", Assert.Single(errors.Items).Field);
    }

    [Fact]
    public void Validate_BlankGroupBy_IsTreatedAsNoGroupBy() =>
        Assert.False(Run(new ViewDefinitionInput("table", null, null, "   ", null, "  "), out _).Any);

    [Fact]
    public void Validate_BadNodeShapes()
    {
        var errors = Run(Filter(new ViewFilterNode
        {
            And =
            [
                new ViewFilterNode(),
                new ViewFilterNode { And = [], },
                new ViewFilterNode { And = [Cond("status", "eq", "todo")], Prop = "status" },
                new ViewFilterNode { Prop = "status" },
            ],
        }), out _);

        Assert.Equal(
            [("filter.and[0]", "required"), ("filter.and[1].and", "required"), ("filter.and[2]", "invalid_format"), ("filter.and[3].op", "required")],
            errors.Items.Select(e => (e.Field, e.Code)));
    }

    [Fact]
    public void Validate_EmptyFilterObject_MeansNoFilter() =>
        Assert.False(Run(Filter(new ViewFilterNode()), out _).Any);

    [Fact]
    public void Validate_FilterNestedTooDeep_TooLong()
    {
        var node = Cond("status", "eq", "todo");
        for (var i = 0; i < ViewDefinitionValidator.MaxFilterDepth; i++)
        {
            node = new ViewFilterNode { And = [node] };
        }

        var errors = Run(Filter(node), out _);

        Assert.Contains(errors.Items, e => e.Code == "too_long");
    }

    [Fact]
    public void Validate_Sorts_ChecksPropDirAndDuplicates()
    {
        var errors = Run(
            new ViewDefinitionInput("table", null, [new("due", "up"), new("nope", "asc"), new("due", "desc"), new(null, null)], null, null, null), out _);

        Assert.Equal(
            [("sorts[0].dir", "not_in_options"), ("sorts[1].prop", "unknown_property"), ("sorts[2].prop", "duplicate"), ("sorts[3].prop", "required"), ("sorts[3].dir", "required")],
            errors.Items.Select(e => (e.Field, e.Code)));
        Assert.Equal(["asc", "desc"], errors.Items[0].Allowed);
    }

    [Fact]
    public void Validate_GroupBy_MustBeSelectOrStatus()
    {
        var errors = Run(new ViewDefinitionInput("board", null, null, "due", null, null), out _);

        var error = Assert.Single(errors.Items);
        Assert.Equal(("groupBy", "invalid_type"), (error.Field, error.Code));
        Assert.Equal(["status", "priority"], error.Allowed);
    }

    [Fact]
    public void Validate_VisibleAndCover_MustExist()
    {
        var errors = Run(new ViewDefinitionInput("gallery", null, null, null, TitleNopeTitle, "ghost"), out _);

        Assert.Equal(
            [("visibleProperties[1]", "unknown_property"), ("visibleProperties[2]", "duplicate"), ("cardCoverProperty", "unknown_property")],
            errors.Items.Select(e => (e.Field, e.Code)));
    }

    [Theory]
    [InlineData(PropertyType.Json)]
    [InlineData(PropertyType.Markdown)]
    [InlineData(PropertyType.Relation)]
    [InlineData(PropertyType.Actor)]
    [InlineData(PropertyType.CreatedAt)]
    public void Validate_CoverOfTypeTheUiCannotShow_InvalidTypeWithAllowedKeys(PropertyType type)
    {
        var schema = new List<PropertyDefinition>(Schema) { new() { Key = "other", Name = "Other", Type = type } };
        var errors = new FieldErrors();

        ViewDefinitionValidator.Validate(errors, schema, new ViewDefinitionInput("gallery", null, null, null, null, "other"));

        var error = Assert.Single(errors.Items);
        Assert.Equal(("cardCoverProperty", "invalid_type"), (error.Field, error.Code));
        Assert.Equal(["status", "priority", "due"], error.Allowed);
    }

    [Fact]
    public void Validate_TitleAsCover_InvalidType_TitleNotInAllowed()
    {
        var errors = new FieldErrors();

        ViewDefinitionValidator.Validate(errors, Schema, new ViewDefinitionInput("gallery", null, null, null, null, "title"));

        var error = Assert.Single(errors.Items);
        Assert.Equal(("cardCoverProperty", "invalid_type"), (error.Field, error.Code));
        Assert.DoesNotContain("title", error.Allowed!);
    }

    [Theory]
    [InlineData(PropertyType.Select)]
    [InlineData(PropertyType.Status)]
    [InlineData(PropertyType.MultiSelect)]
    [InlineData(PropertyType.Checkbox)]
    [InlineData(PropertyType.Text)]
    [InlineData(PropertyType.Url)]
    [InlineData(PropertyType.Email)]
    [InlineData(PropertyType.Number)]
    [InlineData(PropertyType.Date)]
    public void Validate_CoverOfSupportedType_NoError(PropertyType type)
    {
        var schema = new List<PropertyDefinition> { new() { Key = "cover", Name = "Cover", Type = type } };
        var errors = new FieldErrors();

        ViewDefinitionValidator.Validate(errors, schema, new ViewDefinitionInput("gallery", null, null, null, null, "cover"));

        Assert.False(errors.Any);
    }

    [Fact]
    public void RequireGroupBy_BoardWithoutGroupBy_AllowedListsGroupableKeys()
    {
        var errors = new FieldErrors();

        ViewDefinitionValidator.RequireGroupBy(errors, Schema, ViewType.Board, null);
        ViewDefinitionValidator.RequireGroupBy(errors, Schema, ViewType.Table, null);

        var error = Assert.Single(errors.Items);
        Assert.Equal(("groupBy", "required"), (error.Field, error.Code));
        Assert.Equal(["status", "priority"], error.Allowed);
    }
}
