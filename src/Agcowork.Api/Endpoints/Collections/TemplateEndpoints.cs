using System.Text.Json;
using Agcowork.Api.Http;
using Agcowork.Application.Collections.Templates;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Agcowork.Api.Endpoints.Collections;

/// <summary>Record templates of a collection (<c>/collections/{collectionId}/templates</c>).</summary>
internal static class TemplateEndpoints
{
    public static RouteGroupBuilder MapTemplateEndpoints(this RouteGroupBuilder collections)
    {
        var templates = collections.MapGroup("/{collectionId:guid}/templates").WithTags("Templates");

        templates.MapGet("/", ListAsync)
            .WithName("ListTemplates")
            .WithSummary("List record templates")
            .WithDescription("Record templates of the collection (preset properties and Markdown body), default first. Requires read.");

        templates.MapPost("/", CreateAsync)
            .WithName("CreateTemplate")
            .WithSummary("Create a record template")
            .WithDescription(
                "Creates a template: 'name' (unique in the collection) is required; 'description' says when to use it; 'propertiesPreset' "
                + "holds property values by key and is validated against the schema like a partial record (400 with 'errors[]' on "
                + "'propertiesPreset.<key>', e.g. 'unknown_property' or 'not_in_options' with 'allowed'); 'bodyMarkdown' is the starting "
                + "body (e.g. an ADR outline); 'isDefault' makes it the default for new records. Requires schema.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        templates.MapPatch("/{templateId:guid}", UpdateAsync)
            .WithName("UpdateTemplate")
            .WithSummary("Update a record template")
            .WithDescription(
                "Partial update; omitted fields stay unchanged. 'propertiesPreset' and 'bodyMarkdown' replace the old values; "
                + "'isDefault': true moves the default flag to this template. Requires schema.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        templates.MapDelete("/{templateId:guid}", DeleteAsync)
            .WithName("DeleteTemplate")
            .WithSummary("Delete a record template")
            .WithDescription("Deletes the template; records created from it keep their content. Requires schema.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return collections;
    }

    private static async Task<Results<Ok<IReadOnlyList<TemplateDto>>, ProblemHttpResult>> ListAsync(
        Guid collectionId,
        ListTemplates useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ListTemplatesRequest(collectionId), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Created<TemplateDto>, ProblemHttpResult>> CreateAsync(
        Guid collectionId,
        CreateTemplateBody body,
        CreateTemplate useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(
            new CreateTemplateRequest(collectionId, body.Name, body.Description, body.PropertiesPreset, body.BodyMarkdown, body.IsDefault ?? false),
            cancellationToken);
        // There is no single-template GET; the template shows up in GET …/templates.
        return result.IsSuccess ? TypedResults.Created((string?)null, result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<TemplateDto>, ProblemHttpResult>> UpdateAsync(
        Guid collectionId,
        Guid templateId,
        UpdateTemplateBody body,
        UpdateTemplate useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(
            new UpdateTemplateRequest(collectionId, templateId, body.Name, body.Description, body.PropertiesPreset, body.BodyMarkdown, body.IsDefault),
            cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid collectionId,
        Guid templateId,
        DeleteTemplate useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new DeleteTemplateRequest(collectionId, templateId), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }
}

/// <summary>Body of <c>POST /collections/{collectionId}/templates</c>.</summary>
/// <param name="Name">Display name, unique in the collection.</param>
/// <param name="Description">When to use the template.</param>
/// <param name="PropertiesPreset">Property values by key, validated against the schema.</param>
/// <param name="BodyMarkdown">Starting Markdown body.</param>
/// <param name="IsDefault">Make it the collection's default template.</param>
internal sealed record CreateTemplateBody(
    string? Name,
    string? Description = null,
    IReadOnlyDictionary<string, JsonElement>? PropertiesPreset = null,
    string? BodyMarkdown = null,
    bool? IsDefault = null);

/// <summary>Body of <c>PATCH /collections/{collectionId}/templates/{templateId}</c>; null leaves a field unchanged.</summary>
/// <param name="Name">New display name.</param>
/// <param name="Description">New description; "" clears it.</param>
/// <param name="PropertiesPreset">New complete preset.</param>
/// <param name="BodyMarkdown">New body; "" clears it.</param>
/// <param name="IsDefault">true makes it the default; false removes the flag.</param>
internal sealed record UpdateTemplateBody(
    string? Name = null,
    string? Description = null,
    IReadOnlyDictionary<string, JsonElement>? PropertiesPreset = null,
    string? BodyMarkdown = null,
    bool? IsDefault = null);
