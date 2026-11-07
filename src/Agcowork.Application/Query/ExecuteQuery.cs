using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Records;
using Agcowork.Application.Schema;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;

namespace Agcowork.Application.Query;

/// <param name="CollectionId">Collection whose records are queried.</param>
/// <param name="Query">Filter, sorts, page and fields.</param>
public sealed record ExecuteQueryRequest(Guid CollectionId, QueryDefinition Query);

/// <summary>
/// Queries the records of a collection with the filter DSL (architecture §3): filter, sorts, keyset pagination, field
/// selection. Archived records only on request; also works on archived collections (reading). Requires <c>read</c>.
/// Unknown collection: 404; invalid query: 400 with <c>errors[]</c> and <c>allowed</c>.
/// </summary>
public sealed class ExecuteQuery(IAgcoworkDbContext db, ITenantContext tenant, ISchemaProvider schemas, IRecordQueryTranslator translator)
    : IUseCase<ExecuteQueryRequest, QueryResult<RecordDto>>
{
    public async Task<Result<QueryResult<RecordDto>>> HandleAsync(ExecuteQueryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        // The schema is needed to validate, so an unknown collection is reported before invalid input.
        var schema = await schemas.GetSchemaAsync(request.CollectionId, cancellationToken).ConfigureAwait(false);
        if (schema is null)
        {
            return new NotFoundError("collection");
        }

        var errors = new FieldErrors();
        var query = QueryParser.Parse(errors, schema.Properties, request.Query ?? new QueryDefinition());
        if (query is null)
        {
            return errors.ToError();
        }

        return await RecordQueryRunner.RunAsync(db, translator, schema, query, cancellationToken).ConfigureAwait(false);
    }
}
