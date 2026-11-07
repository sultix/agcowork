using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Query;
using Agcowork.Application.Records;
using Agcowork.Application.Schema;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Views;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Views;

/// <param name="ViewId">View to run.</param>
/// <param name="Filter">Additional filter, combined with the view's filter by <c>and</c>.</param>
/// <param name="Cursor"><c>nextCursor</c> of the previous page of this view.</param>
/// <param name="Limit">Page size, 1..500, default 50.</param>
/// <param name="IncludeArchived">Also return archived records.</param>
/// <param name="IncludeCount">Also count all matches (up to 10 000).</param>
/// <param name="UnknownMembers">Members of the request body that are not part of it; each is an error.</param>
public sealed record ExecuteViewRequest(
    Guid ViewId,
    ViewFilterNode? Filter = null,
    string? Cursor = null,
    int? Limit = null,
    bool IncludeArchived = false,
    bool IncludeCount = false,
    IReadOnlyList<string>? UnknownMembers = null)
{
    /// <summary>Members of the request body, the only ones it may have.</summary>
    public static IReadOnlyList<string> Keys { get; } = ["filter", "cursor", "limit", "includeArchived", "includeCount"];
}

/// <summary>
/// Runs a view as a query: its filter (and the optional additional filter), its sorts and its visible properties (empty = all).
/// Requires <c>read</c>. Unknown view: 404. A stored definition that no longer parses (e.g. an operator that does not fit the
/// property type) is reported as 400 on <c>view.filter…</c> / <c>view.sorts…</c>: fix the view with PATCH /views/{id}.
/// </summary>
public sealed class ExecuteView(IAgcoworkDbContext db, ITenantContext tenant, ISchemaProvider schemas, IRecordQueryTranslator translator)
    : IUseCase<ExecuteViewRequest, QueryResult<RecordDto>>
{
    public async Task<Result<QueryResult<RecordDto>>> HandleAsync(ExecuteViewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var view = await db.Views.AsNoTracking().SingleOrDefaultAsync(v => v.Id == request.ViewId, cancellationToken).ConfigureAwait(false);
        var schema = view is null ? null : await schemas.GetSchemaAsync(view.CollectionId, cancellationToken).ConfigureAwait(false);
        if (view is null || schema is null)
        {
            return new NotFoundError("view");
        }

        var errors = new FieldErrors();
        QueryParser.RejectUnknownMembers(errors, request.UnknownMembers, ExecuteViewRequest.Keys);
        var viewFilter = QueryParser.ParseFilter(errors, "view.filter", schema.Properties, view.Filter);
        var sorts = QueryParser.ParseSorts(errors, "view.sorts", schema.Properties, [.. view.Sorts.Select(s => new ViewSortInput(s.Prop, s.Dir))]);
        var fields = view.VisibleProperties.Count == 0
            ? null
            : QueryParser.ParseFields(errors, "view.visibleProperties", schema.Properties, view.VisibleProperties);
        var extra = QueryParser.ParseFilter(errors, "filter", schema.Properties, request.Filter);
        var limit = QueryParser.ParseLimit(errors, request.Limit);
        if (QueryParser.CountConditions(viewFilter) + QueryParser.CountConditions(extra) > QueryParser.MaxCombinedConditions)
        {
            errors.Add(
                "filter",
                ErrorCodes.TooLong,
                $"The view's filter and 'filter' together have at most {QueryParser.MaxCombinedConditions} conditions; the view has {QueryParser.CountConditions(viewFilter)}.");
        }

        if (errors.Any)
        {
            return errors.ToError();
        }

        var after = QueryParser.ParseCursor(errors, request.Cursor, sorts);
        if (errors.Any)
        {
            return errors.ToError();
        }

        var filter = (viewFilter, extra) switch
        {
            ({ } a, { } b) => new FilterGroup(IsAnd: true, [a, b]),
            ({ } a, null) => a,
            (null, var b) => b,
        };
        var query = new RecordQuery(filter, sorts, limit, after, fields, request.IncludeArchived, request.IncludeCount);
        return await RecordQueryRunner.RunAsync(db, translator, schema, query, cancellationToken).ConfigureAwait(false);
    }
}
