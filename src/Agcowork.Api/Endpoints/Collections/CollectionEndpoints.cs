using Agcowork.Api.Http;
using Agcowork.Application.Collections;
using Agcowork.Application.Schema;
using Agcowork.Application.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Agcowork.Api.Endpoints.Collections;

/// <summary>
/// Collections and their schema. Descriptions are written so an agent can use them as tool descriptions (AP-011).
/// Thin: bind, call the use case, map the result.
/// </summary>
internal static class CollectionEndpoints
{
    public static RouteGroupBuilder MapCollectionEndpoints(this RouteGroupBuilder workspace)
    {
        var collections = workspace.MapGroup("/collections").WithTags("Collections");

        collections.MapGet("/", ListAsync)
            .WithName("ListCollections")
            .WithSummary("List collections")
            .WithDescription(
                "Collections of the workspace, or of one space with ?spaceId=, ordered by name, each with description and schema. "
                + "Archived collections only with includeArchived=true. Requires read. Unknown spaceId: 404.");

        collections.MapPost("/", CreateAsync)
            .WithName("CreateCollection")
            .WithSummary("Create a collection")
            .WithDescription(
                "Creates a typed collection in a space. 'spaceId', 'name' (unique in the space) and 'description' are required; the "
                + "description tells agents what a record is and when to add one. 'schema' lists property definitions in display order: "
                + "'key' (snake_case, immutable), 'name', 'type' (text, markdown, number, checkbox, select, multi_select, status, date, "
                + "datetime, url, email, relation, actor, json; computed: created_at, updated_at, created_by, updated_by), optional "
                + "'description', 'required', 'options' (select/multi_select/status; status options need 'group' todo|in_progress|done), "
                + "'targetCollectionId' and 'multiple' (relation, same workspace), 'default', 'unit'/'precision' (number). A title property "
                + "(key 'title', text, required) is added when missing. Requires write. Invalid input: 400 with 'errors[]' and 'allowed' "
                + "values; name taken: 409 'collection_name_taken'.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        collections.MapGet("/{collectionId:guid}", GetAsync)
            .WithName("GetCollection")
            .WithSummary("Get a collection")
            .WithDescription("One collection with its schema, archived ones included. Requires read. Unknown id: 404.");

        collections.MapPatch("/{collectionId:guid}", UpdateAsync)
            .WithName("UpdateCollection")
            .WithSummary("Rename or re-describe a collection")
            .WithDescription(
                "Partial update of 'name' (unique in the space) and 'description' (cannot be cleared). Properties change through "
                + "PATCH …/schema. Requires schema. Archived: 409 'collection_archived'; name taken: 409 'collection_name_taken'.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        collections.MapGet("/{collectionId:guid}/schema", GetSchemaAsync)
            .WithName("GetCollectionSchema")
            .WithSummary("Get the schema of a collection")
            .WithDescription(
                "Property definitions, 'schemaVersion' and the JSON Schema (draft 2020-12) of a record's 'properties' object: enum values, "
                + "required keys, descriptions, additionalProperties false. Requires read.");

        collections.MapPatch("/{collectionId:guid}/schema", UpdateSchemaAsync)
            .WithName("UpdateCollectionSchema")
            .WithSummary("Change the schema of a collection")
            .WithDescription(
                "Applies 'add' (new property definitions, appended), 'update' (by 'key'; only the given fields change; 'options' replaces "
                + "the list; 'clearDefault' removes the default) and 'remove' (keys) together, or nothing. Prefer additive changes. Keys are "
                + "immutable (400 'key_immutable': add a new property instead); types change only text ↔ markdown and select → "
                + "multi_select (400 'type_change_not_allowed' with 'allowed'). Changes that lose stored values need 'confirmRemoval': "
                + "true, otherwise 400 'removal_not_confirmed' naming what would be lost: removing properties, removing option values, "
                + "lowering 'precision', markdown → text. Template presets are adjusted. Increments 'schemaVersion' (entries without an "
                + "actual change do not); send 'expectedSchemaVersion' to get 409 'schema_version_conflict' instead of overwriting a "
                + "concurrent change. Requires schema.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        collections.MapGet("/{collectionId:guid}/describe", DescribeAsync)
            .WithName("DescribeCollection")
            .WithSummary("Describe a collection for agents")
            .WithDescription(
                "Everything needed to work with a collection in one call: the collection with schema and description, its space, the JSON "
                + "Schema of record properties, a valid example 'properties' object, the record templates (default first) and statistics. "
                + "Read this before creating records. Requires read.");

        collections.MapPost("/{collectionId:guid}/archive", ArchiveAsync)
            .WithName("ArchiveCollection")
            .WithSummary("Archive a collection")
            .WithDescription(
                "Makes the collection read-only and hides it from the list (includeArchived=true shows it). Requires schema. "
                + "Already archived: 409 'collection_archived'.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        collections.MapTemplateEndpoints();
        return workspace;
    }

    private static async Task<Results<Ok<IReadOnlyList<CollectionDto>>, ProblemHttpResult>> ListAsync(
        ListCollections useCase,
        [FromQuery] Guid? spaceId,
        [FromQuery] bool? includeArchived,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ListCollectionsRequest(spaceId, includeArchived ?? false), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Created<CollectionDto>, ProblemHttpResult>> CreateAsync(
        CreateCollectionRequest request,
        CreateCollection useCase,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(request, cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"{ApiV1.Prefix}/w/{tenant.Slug}/collections/{result.Value.Id}", result.Value)
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<CollectionDto>, ProblemHttpResult>> GetAsync(Guid collectionId, GetCollection useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new GetCollectionRequest(collectionId), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<CollectionDto>, ProblemHttpResult>> UpdateAsync(
        Guid collectionId,
        UpdateCollectionBody body,
        UpdateCollection useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new UpdateCollectionRequest(collectionId, body.Name, body.Description), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<CollectionSchemaDto>, ProblemHttpResult>> GetSchemaAsync(
        Guid collectionId,
        GetCollectionSchema useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new GetCollectionSchemaRequest(collectionId), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<CollectionSchemaDto>, ProblemHttpResult>> UpdateSchemaAsync(
        Guid collectionId,
        UpdateSchemaBody body,
        UpdateSchema useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(
            new UpdateSchemaRequest(collectionId, body.Add, body.Update, body.Remove, body.ConfirmRemoval ?? false, body.ExpectedSchemaVersion),
            cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<CollectionDescriptionDto>, ProblemHttpResult>> DescribeAsync(
        Guid collectionId,
        DescribeCollection useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new DescribeCollectionRequest(collectionId), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<CollectionDto>, ProblemHttpResult>> ArchiveAsync(Guid collectionId, ArchiveCollection useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ArchiveCollectionRequest(collectionId), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}

/// <summary>Body of <c>PATCH /collections/{collectionId}</c>; null leaves a field unchanged.</summary>
/// <param name="Name">New display name, unique in the space.</param>
/// <param name="Description">New description; cannot be cleared.</param>
internal sealed record UpdateCollectionBody(string? Name = null, string? Description = null);

/// <summary>Body of <c>PATCH /collections/{collectionId}/schema</c>. At least one of add, update, remove.</summary>
/// <param name="Add">New properties, appended in this order.</param>
/// <param name="Update">Changes to existing properties, by key.</param>
/// <param name="Remove">Keys to remove; needs confirmRemoval.</param>
/// <param name="ConfirmRemoval">true confirms changes that lose values: removed properties or options, lower precision, markdown → text.</param>
/// <param name="ExpectedSchemaVersion">Apply only if the schema is still at this version.</param>
internal sealed record UpdateSchemaBody(
    IReadOnlyList<PropertyDefinitionInput>? Add = null,
    IReadOnlyList<PropertyChangeInput>? Update = null,
    IReadOnlyList<string>? Remove = null,
    bool? ConfirmRemoval = null,
    int? ExpectedSchemaVersion = null);
