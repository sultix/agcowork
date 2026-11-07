using System.ComponentModel;
using Agcowork.Application.Collections;
using Agcowork.Application.Schema;
using Agcowork.Application.Views;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agcowork.Api.Mcp.Tools;

/// <summary>Collections and their schemas (<c>/w/{workspaceSlug}/mcp</c>).</summary>
[McpServerToolType]
internal static class CollectionTools
{
    /// <summary>Placeholder for the property definition rules; the catalog fills it from the type and option registries.</summary>
    private const string PropertyDefinitionHelp = "{{propertyDefinition}}";

    [McpServerTool(Name = "collection_describe", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Everything needed to work with one collection, in one call: name and description (what a record is, when to add one), "
        + "the property schema with types, required keys and allowed option values, the JSON Schema (draft 2020-12) of a record's "
        + "'properties' object, a valid example 'properties' object, the record templates (default first; record_create applies the "
        + "default template unless you send 'templateId'), the views and the record count. Call it before record_create or "
        + "records_query in a collection you have not used yet. Requires read.")]
    public static async Task<CallToolResult> Describe(
        DescribeCollection describe,
        ListViews listViews,
        McpOutput output,
        [Description("Collection id from workspace_overview.")] Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        var description = await describe.HandleAsync(new DescribeCollectionRequest(collectionId), cancellationToken);
        if (description.IsFailure)
        {
            return output.Failure(description.Error);
        }

        var views = await listViews.HandleAsync(new ListViewsRequest(collectionId), cancellationToken);
        if (views.IsFailure)
        {
            return output.Failure(views.Error);
        }

        var value = new CollectionDescriptionWithViews(description.Value, views.Value);
        return output.Success(value, McpText.Describe(description.Value, views.Value, output.Json));
    }

    [McpServerTool(Name = "collection_create", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Creates a typed collection (like a database table) in a space. Check workspace_overview first; prefer an existing "
        + "collection. 'description' is required and tells other agents what a record is and when to add one. 'schema' lists the "
        + "properties in display order; a title property (key 'title', text, required) is added when missing. "
        + PropertyDefinitionHelp
        + " Standard views (table, raw) are created with it. Requires write. Invalid input comes back as errors[] with 'allowed' "
        + "values; a taken name as 'collection_name_taken'.")]
    public static async Task<CallToolResult> Create(
        CreateCollection useCase,
        McpOutput output,
        [Description("Space of the collection (spaceId from workspace_overview).")] Guid spaceId,
        [Description("Display name, unique in the space, max. 200 characters, e.g. 'Entscheidungen'.")] string name,
        [Description("Required, max. 4000 characters: what a record is, when to add one, what matters. Agents decide by it.")] string description,
        [Description("Property definitions in display order (see the tool description).")] IReadOnlyList<PropertyDefinitionInput>? schema = null,
        CancellationToken cancellationToken = default)
    {
        var result = await useCase.HandleAsync(new CreateCollectionRequest(spaceId, name, description, schema), cancellationToken);
        return output.From(result, collection => output.SummaryWithJson($"Created collection {collection.Name} (collectionId {collection.Id}, schemaVersion {collection.SchemaVersion}).", collection));
    }

    [McpServerTool(Name = "collection_update_schema", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Changes the properties of a collection: 'add' (new definitions, appended), 'update' (by 'key'; only the given fields "
        + "change; 'options' replaces the whole list; 'clearDefault' removes the default) and 'remove' (keys), applied together or "
        + "not at all. Prefer additive changes. Keys are immutable ('key_immutable': add a new property instead); types change only "
        + "text <-> markdown and select -> multi_select ('type_change_not_allowed' with 'allowed'). Changes that lose stored values "
        + "(removing properties or option values, lowering precision, markdown -> text) need confirmRemoval: true, otherwise "
        + "'removal_not_confirmed' names what would be lost. Send 'expectedSchemaVersion' (from collection_describe) to get "
        + "'schema_version_conflict' instead of overwriting a concurrent change. " + PropertyDefinitionHelp + " Requires schema.")]
    public static async Task<CallToolResult> UpdateSchema(
        UpdateSchema useCase,
        McpOutput output,
        [Description("Collection to change.")] Guid collectionId,
        [Description("New property definitions, appended in this order. Keys must not exist yet.")] IReadOnlyList<PropertyDefinitionInput>? add = null,
        [Description("Changes to existing properties: { 'key', and only the fields to change: 'name', 'type', 'description', 'required', 'options', 'default', 'clearDefault', 'unit', 'precision', 'multiple' }.")] IReadOnlyList<PropertyChangeInput>? update = null,
        [Description("Keys of properties to remove (not 'title'). Needs confirmRemoval: true.")] IReadOnlyList<string>? remove = null,
        [Description("Must be true for changes that lose stored values.")] bool? confirmRemoval = null,
        [Description("Apply only if the schema is still at this version (schemaVersion from collection_describe).")] int? expectedSchemaVersion = null,
        CancellationToken cancellationToken = default)
    {
        var result = await useCase.HandleAsync(
            new UpdateSchemaRequest(collectionId, add, update, remove, confirmRemoval ?? false, expectedSchemaVersion), cancellationToken);
        return output.From(result, schema => output.SummaryWithJson($"Schema of collection {schema.CollectionId} is now at version {schema.SchemaVersion}.", schema));
    }
}

/// <summary>Result of <c>collection_describe</c>: the REST description plus the views of the collection.</summary>
/// <param name="Collection">The collection with its schema.</param>
/// <param name="Space">Its space.</param>
/// <param name="JsonSchema">JSON Schema of a record's properties.</param>
/// <param name="Example">A valid example properties object.</param>
/// <param name="Templates">Record templates, default first.</param>
/// <param name="Statistics">Counts.</param>
/// <param name="Views">Views of the collection.</param>
internal sealed record CollectionDescriptionWithViews(
    CollectionDto Collection,
    Application.Spaces.SpaceDto Space,
    System.Text.Json.Nodes.JsonObject JsonSchema,
    System.Text.Json.Nodes.JsonObject Example,
    IReadOnlyList<Application.Collections.Templates.TemplateDto> Templates,
    CollectionStatisticsDto Statistics,
    IReadOnlyList<ViewDto> Views)
{
    public CollectionDescriptionWithViews(CollectionDescriptionDto description, IReadOnlyList<ViewDto> views)
        : this(
            (description ?? throw new ArgumentNullException(nameof(description))).Collection,
            description.Space,
            description.JsonSchema,
            description.Example,
            description.Templates,
            description.Statistics,
            views)
    {
    }
}
