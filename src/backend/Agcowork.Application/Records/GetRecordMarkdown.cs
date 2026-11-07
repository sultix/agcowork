using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Records;

/// <param name="RecordId">Record to read.</param>
public sealed record GetRecordMarkdownRequest(Guid RecordId);

/// <summary>
/// One record as Markdown with YAML frontmatter (architecture §4.4), the form LLMs read best; also the content of the
/// MCP resource <c>agcowork://record/{id}</c> (AP-011). Requires <c>read</c>. Format: <see cref="RecordMarkdown"/>.
/// </summary>
public sealed class GetRecordMarkdown(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<GetRecordMarkdownRequest, string>
{
    public async Task<Result<string>> HandleAsync(GetRecordMarkdownRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var record = await db.Records.AsNoTracking().SingleOrDefaultAsync(r => r.Id == request.RecordId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return new NotFoundError("record");
        }

        var collection = record.CollectionId is { } collectionId
            ? await db.Collections.AsNoTracking().SingleAsync(c => c.Id == collectionId, cancellationToken).ConfigureAwait(false)
            : null;
        var spaceName = await db.Spaces.AsNoTracking().Where(s => s.Id == record.SpaceId).Select(s => s.Name).SingleAsync(cancellationToken).ConfigureAwait(false);

        var dto = RecordMapper.ToDto(record, collection?.Schema, includeBody: true);
        return RecordMarkdown.Render(dto, collection?.Name, spaceName);
    }
}
