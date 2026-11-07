using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Records;

/// <param name="RecordId">Record.</param>
/// <param name="No">Revision number, from 1.</param>
public sealed record GetRevisionRequest(Guid RecordId, int No);

/// <summary>One revision with the full content of the record at that point. Requires <c>read</c>.</summary>
public sealed class GetRevision(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<GetRevisionRequest, RevisionDto>
{
    public async Task<Result<RevisionDto>> HandleAsync(GetRevisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var revision = await db.Revisions.AsNoTracking()
            .SingleOrDefaultAsync(r => r.RecordId == request.RecordId && r.No == request.No, cancellationToken)
            .ConfigureAwait(false);
        if (revision is null)
        {
            return new NotFoundError("revision");
        }

        var collectionId = await db.Records.AsNoTracking().Where(r => r.Id == revision.RecordId).Select(r => r.CollectionId).SingleAsync(cancellationToken).ConfigureAwait(false);
        var schema = await RecordReads.SchemaAsync(db, collectionId, cancellationToken).ConfigureAwait(false);
        return RecordMapper.ToDto(revision, schema);
    }
}
