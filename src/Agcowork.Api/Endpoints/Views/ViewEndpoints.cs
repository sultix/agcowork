using Agcowork.Api.Http;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Views;
using Agcowork.Domain.Views;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Agcowork.Api.Endpoints.Views;

/// <summary>
/// Views of collections: list and create below <c>/collections/{collectionId}/views</c>, the rest below <c>/views/{viewId}</c>.
/// Descriptions are written so an agent can use them as tool descriptions.
/// </summary>
internal static class ViewEndpoints
{
    public static RouteGroupBuilder MapViewEndpoints(this RouteGroupBuilder workspace)
    {
        var inCollection = workspace.MapGroup("/collections/{collectionId:guid}/views").WithTags("Views");

        inCollection.MapGet("/", ListAsync)
            .WithName("ListViews")
            .WithSummary("List the views of a collection")
            .WithDescription(
                "Views of the collection by sortOrder, then name. Every collection has the system views 'Tabelle' (table, default) and "
                + "'Rohdaten' (raw). A view says how records are presented: type, filter, sorts, groupBy and visibleProperties. "
                + "'needsAttention': true marks a board whose groupBy property was removed from the schema. Requires read.");

        inCollection.MapPost("/", CreateAsync)
            .WithName("CreateView")
            .WithSummary("Create a view")
            .WithDescription(
                "Creates a view. Required: 'name' (unique in the collection, max. 100 characters) and 'type' (table, board, gallery, list, "
                + "document, raw; timeline and calendar are not available yet). A board also needs 'groupBy': the key of a select or status "
                + "property. 'filter' uses the filter DSL: { \"and\": [ … ] }, { \"or\": [ … ] } and { \"not\": { … } } around conditions { \"prop\", \"op\", \"value\" }; "
                + "the operators depend on the property type and option values must exist (the rules of POST /collections/{id}/query, which lists the "
                + "operator matrix; 'in'/'nin' take a list, 'is_empty'/'is_not_empty' no value). Run a view with POST /views/{id}/query. "
                + "'sorts': [ { \"prop\", \"dir\": \"asc\"|\"desc\" } ]. 'visibleProperties': property keys shown (empty = all). 'cardCoverProperty': gallery cover. "
                + "All property keys must exist in the collection schema. 'isDefault': true makes it the default view. Invalid input: 400 with "
                + "'errors[]' and 'allowed' values. Requires write.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var views = workspace.MapGroup("/views/{viewId:guid}").WithTags("Views");

        views.MapGet("/", GetAsync)
            .WithName("GetView")
            .WithSummary("Get a view")
            .WithDescription("One view with filter, sorts, groupBy, visibleProperties, isDefault, isSystem and needsAttention. Requires read.");

        views.MapPatch("/", UpdateAsync)
            .WithName("UpdateView")
            .WithSummary("Update a view")
            .WithDescription(
                "Partial update; omitted fields stay unchanged. 'filter' and 'sorts' replace the old values ('filter': {} and 'sorts': [] remove them); "
                + "'groupBy': \"\" and 'cardCoverProperty': \"\" clear them; 'visibleProperties': [] shows all properties. A board always keeps a 'groupBy'. "
                + "System views (table, raw) can be renamed and configured but keep their type. Use POST …/default to change the default view. Requires write.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        views.MapDelete("/", DeleteAsync)
            .WithName("DeleteView")
            .WithSummary("Delete a view")
            .WithDescription(
                "Deletes the view; records are not affected. System views (table, raw) cannot be deleted: 409 'system_view'. "
                + "Deleting the default view makes the first remaining view the default. Requires write.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        views.MapPost("/default", SetDefaultAsync)
            .WithName("SetDefaultView")
            .WithSummary("Make a view the default")
            .WithDescription("Makes the view the default of its collection; the previous default stops being one, exactly one default remains. Idempotent. Requires write.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return workspace;
    }

    private static async Task<Results<Ok<IReadOnlyList<ViewDto>>, ProblemHttpResult>> ListAsync(
        Guid collectionId,
        ListViews useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ListViewsRequest(collectionId), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Created<ViewDto>, ProblemHttpResult>> CreateAsync(
        Guid collectionId,
        CreateViewBody body,
        CreateView useCase,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(
            new CreateViewRequest(
                collectionId, body.Name, body.Type, body.Filter, body.Sorts, body.GroupBy, body.VisibleProperties, body.CardCoverProperty, body.IsDefault ?? false, body.SortOrder),
            cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"{ApiV1.Prefix}/w/{tenant.Slug}/views/{result.Value.Id}", result.Value)
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<ViewDto>, ProblemHttpResult>> GetAsync(Guid viewId, GetView useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new GetViewRequest(viewId), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<ViewDto>, ProblemHttpResult>> UpdateAsync(
        Guid viewId,
        UpdateViewBody body,
        UpdateView useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(
            new UpdateViewRequest(viewId, body.Name, body.Type, body.Filter, body.Sorts, body.GroupBy, body.VisibleProperties, body.CardCoverProperty, body.SortOrder),
            cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid viewId, DeleteView useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new DeleteViewRequest(viewId), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<ViewDto>, ProblemHttpResult>> SetDefaultAsync(Guid viewId, SetDefaultView useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new SetDefaultViewRequest(viewId), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}

/// <summary>Body of <c>POST /collections/{collectionId}/views</c>.</summary>
/// <param name="Name">Display name, unique in the collection, max. 100 characters.</param>
/// <param name="Type">table, board, gallery, list, document or raw. A board needs groupBy.</param>
/// <param name="Filter">Filter DSL: { "and": [ { "prop", "op", "value" }, … ] }, "or" or "not". Omit for all records.</param>
/// <param name="Sorts">[ { "prop", "dir": "asc"|"desc" } ].</param>
/// <param name="GroupBy">Key of a select/status property; board columns. Required for boards.</param>
/// <param name="VisibleProperties">Keys of the properties shown; empty or omitted = all.</param>
/// <param name="CardCoverProperty">Gallery: key of the cover property.</param>
/// <param name="IsDefault">Make it the collection's default view.</param>
/// <param name="SortOrder">Position among the views; default: last.</param>
internal sealed record CreateViewBody(
    string? Name,
    string? Type,
    ViewFilterNode? Filter = null,
    IReadOnlyList<ViewSortInput>? Sorts = null,
    string? GroupBy = null,
    IReadOnlyList<string>? VisibleProperties = null,
    string? CardCoverProperty = null,
    bool? IsDefault = null,
    int? SortOrder = null);

/// <summary>Body of <c>PATCH /views/{viewId}</c>; null leaves a field unchanged.</summary>
/// <param name="Name">New display name.</param>
/// <param name="Type">New type (not for system views).</param>
/// <param name="Filter">New complete filter; {} removes it.</param>
/// <param name="Sorts">New complete sort list; [] removes the sorting.</param>
/// <param name="GroupBy">New grouping key; "" removes it (not for boards).</param>
/// <param name="VisibleProperties">New complete list of visible keys; [] shows all.</param>
/// <param name="CardCoverProperty">New cover key; "" removes it.</param>
/// <param name="SortOrder">New position among the views.</param>
internal sealed record UpdateViewBody(
    string? Name = null,
    string? Type = null,
    ViewFilterNode? Filter = null,
    IReadOnlyList<ViewSortInput>? Sorts = null,
    string? GroupBy = null,
    IReadOnlyList<string>? VisibleProperties = null,
    string? CardCoverProperty = null,
    int? SortOrder = null);
