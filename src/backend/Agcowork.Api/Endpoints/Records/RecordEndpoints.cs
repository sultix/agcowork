using System.Text;
using System.Text.Json;
using Agcowork.Api.Http;
using Agcowork.Application.Common.Paging;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Agcowork.Application.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

namespace Agcowork.Api.Endpoints.Records;

/// <summary>
/// Records: create, read (JSON or Markdown with frontmatter), patch, append, archive, restore, history. Descriptions are
/// written so an agent can use them as tool descriptions (AP-011). Thin: bind, call the use case, map the result.
/// </summary>
internal static class RecordEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string MarkdownContentType = "text/markdown";

    private static readonly string[] Formats = ["json", "markdown"];

    public static RouteGroupBuilder MapRecordEndpoints(this RouteGroupBuilder workspace)
    {
        var records = workspace.MapGroup("/records").WithTags("Records");

        records.MapGet("/", ListAsync)
            .WithName("ListRecords")
            .WithSummary("List records")
            .WithDescription(
                "Records of a collection (?collectionId=) or the free pages of a space (?spaceId=), exactly one of both, oldest first, "
                + "without bodies. Archived records only with includeArchived=true. Paginated: 'limit' (1..200, default 50) and 'cursor' "
                + "(the 'nextCursor' of the previous page; null on the last page). Filtering comes with POST /collections/{id}/query. "
                + "Requires read. Unknown collection or space: 404.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        records.MapPost("/", CreateAsync)
            .WithName("CreateRecord")
            .WithSummary("Create a record")
            .WithDescription(
                "Creates a record in a collection ('collectionId') or a free page in a space ('spaceId'). 'title' is required (max. 500 "
                + "characters). 'properties' holds values by key as described by GET /collections/{id}/describe (JSON Schema); omitted "
                + "properties get their default; relation values must be ids of records of the target collection, actor values ids of "
                + "members. 'body' is Markdown (max. 2 MiB). The collection's default template (or 'templateId') provides preset "
                + "values and the body when none is sent; sent values win. 'parentRecordId' nests the record below another one. "
                + "Send an 'Idempotency-Key' header (e.g. a UUID) to make retries safe: the same key by the same actor within 24 h "
                + "returns the first record instead of creating another. The response has no body; read it with GET ?includeBody=true. "
                + "Requires write. Invalid values: 400 with 'errors[]' on 'properties.<key>' (e.g. 'unknown_property' or "
                + "'not_in_options' with 'allowed'); archived collection or space: 409.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        records.MapGet("/{recordId:guid}", GetAsync)
            .WithName("GetRecord")
            .WithSummary("Get a record")
            .WithDescription(
                "One record, archived ones included. format=json (default) returns the record; includeBody=true adds the Markdown "
                + "body, includeHistory=n (0..100) the latest n revisions, includeComments=true the discussion ('comments': threads with replies, "
                + "as GET …/comments returns them; with format=markdown a '# Comments' section after the body). format=markdown returns text/markdown: a YAML frontmatter "
                + "block (id, collection, space, title, all properties, revision, timestamps, actors) followed by the body, the "
                + "easiest form to read for language models. Requires read.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                // 200 is JSON (inferred) or, with format=markdown, text/markdown.
                if (operation.Responses?.TryGetValue("200", out var ok) == true && ok.Content is { } content)
                {
                    content[MarkdownContentType] = new OpenApiMediaType { Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
                }

                return Task.CompletedTask;
            })
            .ProducesProblem(StatusCodes.Status400BadRequest);

        records.MapPatch("/{recordId:guid}", UpdateAsync)
            .WithName("UpdateRecord")
            .WithSummary("Change a record")
            .WithDescription(
                "Partial update. 'properties' is a patch: only sent keys change, null removes a value (not for required properties). "
                + "'title' and 'body' replace the old values when sent; to add text without sending the whole body use POST …/append. "
                + "Send 'expectedRevision' (the 'revision' you read) so concurrent changes are not overwritten: if the record moved on, "
                + "409 'revision_mismatch' carries 'currentRevision' and 'changedKeys' (title, property keys, body) changed since then; "
                + "if your change does not touch them, retry with the current revision. Without 'expectedRevision' the last writer wins. "
                + "'reason' is stored with the revision. A change that changes nothing creates no revision. Requires write. "
                + "Archived record: 409 'record_archived'.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        records.MapPost("/{recordId:guid}/append", AppendAsync)
            .WithName("AppendToRecord")
            .WithSummary("Append Markdown to a record")
            .WithDescription(
                "Appends 'markdown' to the body without knowing it, e.g. log entries or findings. With 'section' (a heading text such as "
                + "'Log', case-insensitive, leading '#' optional) the text goes to the end of that section; a missing section is created "
                + "as '## <section>' at the end. Without 'section' it goes to the end of the body. Concurrent appends never overwrite "
                + "each other. Creates a revision 'appended'. The body may not exceed 2 MiB: 400 'too_long' on 'markdown'. Requires write.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        records.MapPost("/{recordId:guid}/archive", ArchiveAsync)
            .WithName("ArchiveRecord")
            .WithSummary("Archive a record")
            .WithDescription(
                "Soft delete: the record stays readable but is hidden from lists and read-only until restored. Records are never "
                + "deleted. Optional 'reason'. Requires write. Already archived: 409 'record_archived'.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        records.MapPost("/{recordId:guid}/restore", RestoreAsync)
            .WithName("RestoreRecord")
            .WithSummary("Restore an archived record")
            .WithDescription("Makes an archived record writable and listed again. Optional 'reason'. Requires write. Not archived: 409 'record_not_archived'.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        records.MapGet("/{recordId:guid}/history", HistoryAsync)
            .WithName("GetRecordHistory")
            .WithSummary("List the revisions of a record")
            .WithDescription(
                "Revisions newest first, each with 'no', 'kind' (created, updated, appended, archived, restored), actor, time and "
                + "reason. The content of a revision: GET …/revisions/{no}. Paginated with 'limit' and 'cursor'. Requires read.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        records.MapGet("/{recordId:guid}/revisions/{no:int}", RevisionAsync)
            .WithName("GetRevision")
            .WithSummary("Get one revision of a record")
            .WithDescription("Title, properties and body of the record as they were after revision 'no'. Requires read.");

        return workspace;
    }

    private static async Task<Results<Ok<Page<RecordDto>>, ProblemHttpResult>> ListAsync(
        ListRecords useCase,
        [FromQuery] Guid? collectionId,
        [FromQuery] Guid? spaceId,
        [FromQuery] bool? includeArchived,
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ListRecordsRequest(collectionId, spaceId, includeArchived ?? false, limit, cursor), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Created<RecordDto>, ProblemHttpResult>> CreateAsync(
        CreateRecordBody body,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        CreateRecord useCase,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(
            new CreateRecordRequest(body.CollectionId, body.SpaceId, body.Title, body.Properties, body.Body, body.TemplateId, body.ParentRecordId, idempotencyKey),
            cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"{ApiV1.Prefix}/w/{tenant.Slug}/records/{result.Value.Id}", result.Value)
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<RecordDto>, ContentHttpResult, ProblemHttpResult>> GetAsync(
        Guid recordId,
        GetRecord getRecord,
        GetRecordMarkdown getMarkdown,
        [FromQuery] string? format,
        [FromQuery] bool? includeBody,
        [FromQuery] int? includeHistory,
        [FromQuery] bool? includeComments,
        CancellationToken cancellationToken)
    {
        format ??= "json";
        if (!Formats.Contains(format, StringComparer.Ordinal))
        {
            return new FieldError("format", ErrorCodes.NotInOptions, $"'{format}' is not a valid format.", Formats).ToValidationError().ToProblem();
        }

        if (format == "markdown")
        {
            var markdown = await getMarkdown.HandleAsync(new GetRecordMarkdownRequest(recordId, includeComments ?? false), cancellationToken);
            return markdown.IsSuccess ? TypedResults.Text(markdown.Value, MarkdownContentType, Encoding.UTF8) : markdown.Error.ToProblem();
        }

        var result = await getRecord.HandleAsync(new GetRecordRequest(recordId, includeBody ?? false, includeHistory ?? 0, includeComments ?? false), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<RecordDto>, ProblemHttpResult>> UpdateAsync(
        Guid recordId,
        UpdateRecordBody body,
        UpdateRecord useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(
            new UpdateRecordRequest(recordId, body.Title, body.Properties, body.Body, body.ExpectedRevision, body.Reason),
            cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<RecordDto>, ProblemHttpResult>> AppendAsync(
        Guid recordId,
        AppendToRecordBody body,
        AppendToRecord useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new AppendToRecordRequest(recordId, body.Markdown, body.Section, body.Reason), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<RecordDto>, ProblemHttpResult>> ArchiveAsync(
        Guid recordId,
        [FromBody] ReasonBody? body,
        ArchiveRecord useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ArchiveRecordRequest(recordId, body?.Reason), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<RecordDto>, ProblemHttpResult>> RestoreAsync(
        Guid recordId,
        [FromBody] ReasonBody? body,
        RestoreRecord useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new RestoreRecordRequest(recordId, body?.Reason), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<Page<RevisionSummaryDto>>, ProblemHttpResult>> HistoryAsync(
        Guid recordId,
        GetRecordHistory useCase,
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new GetRecordHistoryRequest(recordId, limit, cursor), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<RevisionDto>, ProblemHttpResult>> RevisionAsync(
        Guid recordId,
        int no,
        GetRevision useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new GetRevisionRequest(recordId, no), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}

/// <summary>Body of <c>POST /records</c>; the idempotency key comes as header.</summary>
/// <param name="CollectionId">Collection of the record; or <paramref name="SpaceId"/> for a free page.</param>
/// <param name="SpaceId">Space of a free page; optional with a collection.</param>
/// <param name="Title">Title, max. 500 characters.</param>
/// <param name="Properties">Property values by key.</param>
/// <param name="Body">Markdown body, max. 2 MiB.</param>
/// <param name="TemplateId">Template of the collection; default: the collection's default template.</param>
/// <param name="ParentRecordId">Parent record for nesting.</param>
internal sealed record CreateRecordBody(
    Guid? CollectionId = null,
    Guid? SpaceId = null,
    string? Title = null,
    IReadOnlyDictionary<string, JsonElement>? Properties = null,
    string? Body = null,
    Guid? TemplateId = null,
    Guid? ParentRecordId = null);

/// <summary>Body of <c>PATCH /records/{recordId}</c>; null leaves a field unchanged.</summary>
/// <param name="Title">New title.</param>
/// <param name="Properties">Patch of property values; null removes a value.</param>
/// <param name="Body">New Markdown body (replaces).</param>
/// <param name="ExpectedRevision">Apply only if the record is still at this revision.</param>
/// <param name="Reason">Why; stored with the revision.</param>
internal sealed record UpdateRecordBody(
    string? Title = null,
    IReadOnlyDictionary<string, JsonElement>? Properties = null,
    string? Body = null,
    int? ExpectedRevision = null,
    string? Reason = null);

/// <summary>Body of <c>POST /records/{recordId}/append</c>.</summary>
/// <param name="Markdown">Markdown to append.</param>
/// <param name="Section">Heading to append under; created when missing.</param>
/// <param name="Reason">Why; stored with the revision.</param>
internal sealed record AppendToRecordBody(string? Markdown, string? Section = null, string? Reason = null);

/// <summary>Optional body of archive and restore.</summary>
/// <param name="Reason">Why; stored with the revision.</param>
internal sealed record ReasonBody(string? Reason = null);
