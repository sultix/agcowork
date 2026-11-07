using Agcowork.Domain.Collections.Schema;

namespace Agcowork.Domain.Tests.Collections;

public sealed class PropertyDefinitionTests
{
    private static PropertyDefinition Property(string key = "status", PropertyType type = PropertyType.Text) =>
        new() { Key = key, Name = "Name", Type = type };

    private static SchemaViolation Single(PropertyDefinition property, string path) =>
        Assert.Single(property.Validate(), v => v.Path == path);

    [Theory]
    [InlineData("a")]
    [InlineData("title")]
    [InlineData("due_date")]
    [InlineData("x1")]
    [InlineData("a_b_c_9")]
    public void IsValidKey_SnakeCase_True(string key)
    {
        Assert.True(PropertyDefinition.IsValidKey(key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Due")]
    [InlineData("due-date")]
    [InlineData("1due")]
    [InlineData("_due")]
    [InlineData("due date")]
    [InlineData("due\n")]
    [InlineData("dü")]
    public void IsValidKey_NotSnakeCase_False(string key)
    {
        Assert.False(PropertyDefinition.IsValidKey(key));
    }

    [Fact]
    public void IsValidKey_Length_UpTo63()
    {
        Assert.True(PropertyDefinition.IsValidKey("a" + new string('b', 62)));
        Assert.False(PropertyDefinition.IsValidKey("a" + new string('b', 63)));
    }

    [Fact]
    public void Validate_InvalidKey_InvalidFormat()
    {
        Assert.Equal(SchemaViolationCodes.InvalidFormat, Single(Property("Due-Date"), "key").Code);
    }

    [Fact]
    public void Validate_TooLongKey_TooLong()
    {
        Assert.Equal(SchemaViolationCodes.TooLong, Single(Property("a" + new string('b', 63)), "key").Code);
    }

    [Fact]
    public void Validate_EmptyKeyAndName_Required()
    {
        var violations = (Property(string.Empty) with { Name = " " }).Validate();

        Assert.Contains(violations, v => v.Path == "key" && v.Code == SchemaViolationCodes.Required);
        Assert.Contains(violations, v => v.Path == "name" && v.Code == SchemaViolationCodes.Required);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("collection")]
    [InlineData("space")]
    [InlineData("revision")]
    [InlineData("body")]
    public void Validate_ReservedKey_Reserved(string key)
    {
        Assert.Equal(SchemaViolationCodes.Reserved, Single(Property(key), "key").Code);
    }

    [Fact]
    public void Validate_TitleWithOtherType_ReservedWithAllowedText()
    {
        var violation = Single(Property("title", PropertyType.Number) with { Required = true }, "type");

        Assert.Equal(SchemaViolationCodes.Reserved, violation.Code);
        Assert.Equal(["text"], violation.Allowed);
    }

    [Fact]
    public void Validate_TitleNotRequired_NotAllowed()
    {
        Assert.Equal(SchemaViolationCodes.NotAllowed, Single(Property("title"), "required").Code);
    }

    [Fact]
    public void Title_Default_IsValidRequiredText()
    {
        var title = PropertyDefinition.Title();

        Assert.Equal("title", title.Key);
        Assert.Equal(PropertyType.Text, title.Type);
        Assert.True(title.Required);
        Assert.Empty(title.Validate());
    }

    [Fact]
    public void Validate_DuplicateOptionValues_Duplicate()
    {
        var property = Property("priority", PropertyType.Select) with
        {
            Options = [new("high", "High"), new("low", "Low"), new("high", "High again")],
        };

        Assert.Equal(SchemaViolationCodes.Duplicate, Single(property, "options[2].value").Code);
    }

    [Fact]
    public void Validate_OptionValuesDifferOnlyInCase_Allowed()
    {
        var property = Property("priority", PropertyType.Select) with { Options = [new("High", "High"), new("high", "high")] };

        Assert.Empty(property.Validate());
    }

    [Fact]
    public void Validate_OptionWithoutValueOrLabel_Required()
    {
        var violations = (Property("priority", PropertyType.Select) with { Options = [new(" ", "")] }).Validate();

        Assert.Contains(violations, v => v.Path == "options[0].value" && v.Code == SchemaViolationCodes.Required);
        Assert.Contains(violations, v => v.Path == "options[0].label" && v.Code == SchemaViolationCodes.Required);
    }

    [Fact]
    public void Validate_UnknownColor_NotInOptionsWithPalette()
    {
        var violation = Single(Property("priority", PropertyType.Select) with { Options = [new("high", "High", "magenta")] }, "options[0].color");

        Assert.Equal(SchemaViolationCodes.NotInOptions, violation.Code);
        Assert.Equal(PropertyOption.Colors, violation.Allowed);
    }

    [Fact]
    public void Validate_StatusOptionWithoutGroup_RequiredWithGroups()
    {
        var violation = Single(Property("status", PropertyType.Status) with { Options = [new("todo", "To do")] }, "options[0].group");

        Assert.Equal(SchemaViolationCodes.Required, violation.Code);
        Assert.Equal(["todo", "in_progress", "done"], violation.Allowed);
    }

    [Fact]
    public void Validate_StatusOptionsWithGroups_Valid()
    {
        var property = Property("status", PropertyType.Status) with
        {
            Options = [new("open", "Open", "gray", StatusGroup.Todo), new("doing", "Doing", "blue", StatusGroup.InProgress), new("done", "Done", "green", StatusGroup.Done)],
        };

        Assert.Empty(property.Validate());
    }

    [Fact]
    public void Validate_GroupOnSelectOption_NotAllowed()
    {
        var property = Property("priority", PropertyType.Select) with { Options = [new("high", "High", Group: StatusGroup.Done)] };

        Assert.Equal(SchemaViolationCodes.NotAllowed, Single(property, "options[0].group").Code);
    }

    [Theory]
    [InlineData(PropertyType.Text)]
    [InlineData(PropertyType.Number)]
    [InlineData(PropertyType.Relation)]
    public void Validate_OptionsOnTypeWithoutOptions_NotAllowed(PropertyType type)
    {
        var property = Property("x", type) with { Options = [new("a", "A")], TargetCollectionId = Guid.CreateVersion7() };

        Assert.Equal(SchemaViolationCodes.NotAllowed, Single(property, "options").Code);
    }

    [Fact]
    public void Validate_SelectWithoutOptions_Valid()
    {
        Assert.Empty(Property("priority", PropertyType.Select).Validate());
    }

    [Theory]
    [InlineData(PropertyType.Select)]
    [InlineData(PropertyType.MultiSelect)]
    [InlineData(PropertyType.Status)]
    public void Validate_RequiredWithoutOptions_Required(PropertyType type)
    {
        Assert.Equal(SchemaViolationCodes.Required, Single(Property("x", type) with { Required = true }, "options").Code);
    }

    [Fact]
    public void Validate_RelationWithoutTarget_Required()
    {
        Assert.Equal(SchemaViolationCodes.Required, Single(Property("affects", PropertyType.Relation), "targetCollectionId").Code);
    }

    [Fact]
    public void Validate_TargetOrMultipleOnNonRelation_NotAllowed()
    {
        var violations = (Property("x") with { TargetCollectionId = Guid.CreateVersion7(), Multiple = true }).Validate();

        Assert.Contains(violations, v => v.Path == "targetCollectionId" && v.Code == SchemaViolationCodes.NotAllowed);
        Assert.Contains(violations, v => v.Path == "multiple" && v.Code == SchemaViolationCodes.NotAllowed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Validate_PrecisionOutOfRange_OutOfRange(int precision)
    {
        Assert.Equal(SchemaViolationCodes.OutOfRange, Single(Property("estimate", PropertyType.Number) with { Precision = precision }, "precision").Code);
    }

    [Fact]
    public void Validate_UnitAndPrecisionOnText_NotAllowed()
    {
        var violations = (Property("x") with { Unit = "h", Precision = 2 }).Validate();

        Assert.Contains(violations, v => v.Path == "unit" && v.Code == SchemaViolationCodes.NotAllowed);
        Assert.Contains(violations, v => v.Path == "precision" && v.Code == SchemaViolationCodes.NotAllowed);
    }

    [Fact]
    public void Validate_RequiredComputed_NotAllowed()
    {
        Assert.Equal(SchemaViolationCodes.NotAllowed, Single(Property("created", PropertyType.CreatedAt) with { Required = true }, "required").Code);
    }

    [Fact]
    public void Validate_UndefinedType_NotInOptionsWithAllTypes()
    {
        var violation = Single(Property("x", (PropertyType)99), "type");

        Assert.Equal(PropertyTypes.AllWireNames, violation.Allowed);
    }

    [Fact]
    public void PropertyTypes_WireNames_RoundTripAndMatchArchitecture()
    {
        Assert.Equal(
            ["text", "markdown", "number", "checkbox", "select", "multi_select", "status", "date", "datetime", "url", "email", "relation", "actor", "json",
             "created_at", "updated_at", "created_by", "updated_by"],
            PropertyTypes.AllWireNames);
        foreach (var type in Enum.GetValues<PropertyType>())
        {
            Assert.True(PropertyTypes.TryParse(PropertyTypes.ToWire(type), out var parsed));
            Assert.Equal(type, parsed);
        }

        Assert.False(PropertyTypes.TryParse("Text", out _));
    }

    [Fact]
    public void Prefixed_AnchorsRelativePaths()
    {
        Assert.Equal("schema[2].key", new SchemaViolation("[2].key", "x", "m").Prefixed("schema").Path);
        Assert.Equal("add[0].options[1].value", new SchemaViolation("options[1].value", "x", "m").Prefixed("add[0]").Path);
        Assert.Equal("schema", new SchemaViolation(string.Empty, "x", "m").Prefixed("schema").Path);
    }
}
