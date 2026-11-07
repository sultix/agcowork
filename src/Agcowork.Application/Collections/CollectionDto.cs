using System.Text.Json.Nodes;
using Agcowork.Application.Collections.Templates;
using Agcowork.Application.Schema;
using Agcowork.Application.Spaces;
using Agcowork.Domain.Collections;

namespace Agcowork.Application.Collections;

/// <summary>A collection with its schema.</summary>
/// <param name="Id">Collection id.</param>
/// <param name="SpaceId">Space the collection belongs to.</param>
/// <param name="Name">Display name, unique within the space.</param>
/// <param name="Description">What the collection holds and when to add a record. Written for agents.</param>
/// <param name="TitlePropertyKey">Key of the title property (always <c>title</c>).</param>
/// <param name="SchemaVersion">Increments with every schema change; send it as <c>expectedSchemaVersion</c> to avoid lost updates.</param>
/// <param name="Schema">Ordered property definitions.</param>
/// <param name="CreatedBy">Actor that created the collection.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
/// <param name="ArchivedAt">Set when archived; archived collections are read-only.</param>
public sealed record CollectionDto(
    Guid Id,
    Guid SpaceId,
    string Name,
    string Description,
    string TitlePropertyKey,
    int SchemaVersion,
    IReadOnlyList<PropertyDefinitionDto> Schema,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt)
{
    public static CollectionDto From(Collection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        return new CollectionDto(
            collection.Id,
            collection.SpaceId,
            collection.Name,
            collection.Description,
            collection.TitlePropertyKey,
            collection.SchemaVersion,
            PropertyDefinitionDto.From(collection.Schema),
            collection.CreatedBy,
            collection.CreatedAt,
            collection.ArchivedAt);
    }
}

/// <summary>The schema of a collection, as definitions and as JSON Schema.</summary>
/// <param name="CollectionId">Collection id.</param>
/// <param name="SchemaVersion">Current version; send it as <c>expectedSchemaVersion</c> with the next change.</param>
/// <param name="TitlePropertyKey">Key of the title property.</param>
/// <param name="Properties">Ordered property definitions.</param>
/// <param name="JsonSchema">JSON Schema (draft 2020-12) of a record's <c>properties</c> object.</param>
public sealed record CollectionSchemaDto(
    Guid CollectionId,
    int SchemaVersion,
    string TitlePropertyKey,
    IReadOnlyList<PropertyDefinitionDto> Properties,
    JsonObject JsonSchema)
{
    public static CollectionSchemaDto From(Collection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        return new CollectionSchemaDto(
            collection.Id,
            collection.SchemaVersion,
            collection.TitlePropertyKey,
            PropertyDefinitionDto.From(collection.Schema),
            JsonSchemaBuilder.Build(collection));
    }
}

/// <summary>Everything an agent needs to work with a collection, in one response.</summary>
/// <param name="Collection">The collection with its schema.</param>
/// <param name="Space">The space it belongs to.</param>
/// <param name="JsonSchema">JSON Schema (draft 2020-12) of a record's <c>properties</c> object.</param>
/// <param name="Example">A valid example <c>properties</c> object: one value per writable property. Relation and actor properties are left out; they need real record and member ids.</param>
/// <param name="Templates">Record templates, default first.</param>
/// <param name="Statistics">Counts; filled once records exist.</param>
public sealed record CollectionDescriptionDto(
    CollectionDto Collection,
    SpaceDto Space,
    JsonObject JsonSchema,
    JsonObject Example,
    IReadOnlyList<TemplateDto> Templates,
    CollectionStatisticsDto Statistics);

/// <summary>Counts of a collection.</summary>
/// <param name="RecordCount">Number of records; null until records exist (AP-007).</param>
public sealed record CollectionStatisticsDto(long? RecordCount);
