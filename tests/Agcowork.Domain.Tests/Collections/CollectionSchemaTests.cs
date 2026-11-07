using Agcowork.Domain.Collections;
using Agcowork.Domain.Collections.Schema;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Domain.Tests.Collections;

public sealed class CollectionSchemaTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    private static readonly Guid Workspace = Guid.CreateVersion7();
    private static readonly Guid Space = Guid.CreateVersion7();
    private static readonly Guid Creator = Guid.CreateVersion7();

    private static PropertyDefinition Property(string key, PropertyType type = PropertyType.Text) => new() { Key = key, Name = key, Type = type };

    private static Collection Create(params PropertyDefinition[] schema) =>
        Collection.Create(Workspace, Space, "Decisions", "Architecture decisions.", schema, Creator, Clock);

    [Fact]
    public void Validate_DuplicateKeys_DuplicateOnSecond()
    {
        var violation = Assert.Single(CollectionSchema.Validate([Property("a"), Property("b"), Property("a")]));

        Assert.Equal("[2].key", violation.Path);
        Assert.Equal(SchemaViolationCodes.Duplicate, violation.Code);
    }

    [Fact]
    public void Validate_PropertyViolations_PrefixedWithIndex()
    {
        var violation = Assert.Single(CollectionSchema.Validate([Property("a"), Property("Bad")]));

        Assert.Equal("[1].key", violation.Path);
    }

    [Fact]
    public void Validate_TooManyProperties_TooLong()
    {
        var schema = Enumerable.Range(0, CollectionSchema.MaxProperties + 1).Select(i => Property($"p{i}")).ToList();

        Assert.Contains(CollectionSchema.Validate(schema), v => v.Code == SchemaViolationCodes.TooLong);
    }

    [Theory]
    [InlineData(PropertyType.Text, PropertyType.Markdown)]
    [InlineData(PropertyType.Markdown, PropertyType.Text)]
    [InlineData(PropertyType.Select, PropertyType.MultiSelect)]
    [InlineData(PropertyType.Number, PropertyType.Number)]
    public void IsAllowedChange_AllowedPaths_True(PropertyType from, PropertyType to)
    {
        Assert.True(PropertyTypes.IsAllowedChange(from, to));
    }

    [Fact]
    public void IsAllowedChange_EveryOtherPair_False()
    {
        var allowed = new HashSet<(PropertyType, PropertyType)>
        {
            (PropertyType.Text, PropertyType.Markdown), (PropertyType.Markdown, PropertyType.Text), (PropertyType.Select, PropertyType.MultiSelect),
        };

        foreach (var from in PropertyTypes.All)
        {
            foreach (var to in PropertyTypes.All.Where(t => t != from))
            {
                Assert.Equal(allowed.Contains((from, to)), PropertyTypes.IsAllowedChange(from, to));
            }
        }
    }

    [Fact]
    public void ValidateChange_DisallowedType_TypeChangeNotAllowedWithAllowedTargets()
    {
        var violation = Assert.Single(CollectionSchema.ValidateChange(Property("x", PropertyType.Select), Property("x", PropertyType.Text)));

        Assert.Equal(SchemaViolationCodes.TypeChangeNotAllowed, violation.Code);
        Assert.Equal(["select", "multi_select"], violation.Allowed);
    }

    [Fact]
    public void ValidateChange_OtherKey_KeyImmutable()
    {
        Assert.Equal(SchemaViolationCodes.KeyImmutable, Assert.Single(CollectionSchema.ValidateChange(Property("a"), Property("b"))).Code);
    }

    [Fact]
    public void ValidateChange_RelationTargetOrNarrowing_NotAllowed()
    {
        var before = Property("affects", PropertyType.Relation) with { TargetCollectionId = Guid.CreateVersion7(), Multiple = true };

        var violations = CollectionSchema.ValidateChange(before, before with { TargetCollectionId = Guid.CreateVersion7(), Multiple = false });

        Assert.Contains(violations, v => v.Path == "targetCollectionId");
        Assert.Contains(violations, v => v.Path == "multiple");
        Assert.Empty(CollectionSchema.ValidateChange(before with { Multiple = false }, before));
    }

    [Fact]
    public void Create_WithoutTitle_AddsTitleFirstAndStartsAtVersion1()
    {
        var collection = Create(Property("status"));

        Assert.Equal(["title", "status"], collection.Schema.Select(p => p.Key));
        Assert.Equal("title", collection.TitlePropertyKey);
        Assert.Equal(1, collection.SchemaVersion);
        Assert.Equal(7, collection.Id.Version);
        Assert.Equal(Workspace, collection.WorkspaceId);
    }

    [Fact]
    public void Create_WithOwnTitle_KeepsPositionAndName()
    {
        var collection = Create(Property("status"), PropertyDefinition.Title("Headline"));

        Assert.Equal(["status", "title"], collection.Schema.Select(p => p.Key));
        Assert.Equal("Headline", collection.Schema[1].Name);
    }

    [Fact]
    public void Create_InvalidSchema_Throws()
    {
        Assert.Throws<ArgumentException>(() => Create(Property("a"), Property("a")));
    }

    [Fact]
    public void Create_WithoutDescription_Throws()
    {
        Assert.Throws<ArgumentException>(() => Collection.Create(Workspace, Space, "Decisions", " ", null, Creator, Clock));
    }

    [Fact]
    public void ReplaceSchema_Valid_IncrementsVersion()
    {
        var collection = Create();

        collection.ReplaceSchema([.. collection.Schema, Property("summary", PropertyType.Markdown)]);

        Assert.Equal(2, collection.SchemaVersion);
        Assert.Equal(["title", "summary"], collection.Schema.Select(p => p.Key));
    }

    [Fact]
    public void ReplaceSchema_DisallowedTypeChange_ThrowsAndKeepsSchema()
    {
        var collection = Create(Property("summary"));

        Assert.Throws<ArgumentException>(() => collection.ReplaceSchema([collection.Schema[0], Property("summary", PropertyType.Number)]));
        Assert.Equal(1, collection.SchemaVersion);
        Assert.Equal(PropertyType.Text, collection.Schema[1].Type);
    }

    [Fact]
    public void ReplaceSchema_WithoutTitle_Throws()
    {
        var collection = Create(Property("summary"));

        Assert.Throws<ArgumentException>(() => collection.ReplaceSchema([Property("summary")]));
    }

    [Fact]
    public void Archive_ThenChange_Throws()
    {
        var collection = Create();
        collection.Archive(Clock);

        Assert.True(collection.IsArchived);
        Assert.Throws<InvalidOperationException>(() => collection.Rename("Other"));
        Assert.Throws<InvalidOperationException>(() => collection.ReplaceSchema(collection.Schema));
        Assert.Throws<InvalidOperationException>(() => collection.Archive(Clock));
    }

    [Fact]
    public void Template_Create_CopiesPresetAndDefaultsBody()
    {
        var preset = new Dictionary<string, System.Text.Json.JsonElement> { ["status"] = System.Text.Json.JsonSerializer.SerializeToElement("open") };

        var template = Template.Create(Workspace, Guid.CreateVersion7(), "  ADR  ", null, preset, null, isDefault: true, Creator, Clock);
        preset.Clear();

        Assert.Equal("ADR", template.Name);
        Assert.Single(template.PropertiesPreset);
        Assert.Equal(string.Empty, template.BodyMarkdown);
        Assert.True(template.IsDefault);
    }
}
