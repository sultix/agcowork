using Agcowork.Application.Comments;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Query;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Records;

/// <param name="RecordId">Record to read.</param>
/// <param name="IncludeComments">Append the discussion as a <c># Comments</c> section after a separator (<see cref="CommentText.AppendTo"/>).</param>
/// <param name="ExpandRelations">Write the title of every referenced record as a YAML comment next to its id.</param>
/// <param name="IncludeBacklinks">Append a <c># Backlinks</c> section (before the comments): the records that reference this one.</param>
public sealed record GetRecordMarkdownRequest(Guid RecordId, bool IncludeComments = false, bool ExpandRelations = false, bool IncludeBacklinks = false);

/// <summary>The Markdown of a record with the structured data it was rendered from (for adapters that answer both).</summary>
/// <param name="Markdown">Frontmatter, body and the requested sections.</param>
/// <param name="Relations">Resolved relation values; only with <c>expandRelations</c>.</param>
/// <param name="Backlinks">Backlinks; only with <c>includeBacklinks</c>.</param>
public sealed record RecordMarkdownView(
    string Markdown,
    IReadOnlyDictionary<string, IReadOnlyList<RelationTargetDto>>? Relations,
    RecordBacklinksDto? Backlinks);

/// <summary>
/// One record as Markdown with YAML frontmatter (architecture §4.4), the form LLMs read best; also the content of the
/// MCP resource <c>agcowork://record/{id}</c> (AP-011). Requires <c>read</c>. Format: <see cref="RecordMarkdown"/>.
/// </summary>
public sealed class GetRecordMarkdown(IAgcoworkDbContext db, ITenantContext tenant, IRecordQueryTranslator translator) : IUseCase<GetRecordMarkdownRequest, string>
{
    public async Task<Result<string>> HandleAsync(GetRecordMarkdownRequest request, CancellationToken cancellationToken)
    {
        var view = await RenderAsync(request, cancellationToken).ConfigureAwait(false);
        return view.IsSuccess ? view.Value.Markdown : view.Error;
    }

    /// <summary>Like <see cref="HandleAsync"/>, plus the resolved relations and backlinks that went into the text.</summary>
    public async Task<Result<RecordMarkdownView>> RenderAsync(GetRecordMarkdownRequest request, CancellationToken cancellationToken)
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
        var relations = request.ExpandRelations
            ? await RecordTitles.ExpandAsync(db, collection?.Schema, record.Properties, cancellationToken).ConfigureAwait(false)
            : null;
        var markdown = RecordMarkdown.Render(dto, collection?.Name, spaceName, relations);
        RecordBacklinksDto? backlinks = null;
        if (request.IncludeBacklinks)
        {
            // Without a cursor there is always an answer (null only for a cursor whose group is gone).
            backlinks = (await Backlinks.ReadAsync(db, translator, record.Id, record.CollectionId, Backlinks.PerGroupInRecord, cursor: null, includeArchived: false, cancellationToken).ConfigureAwait(false))!;
            markdown = RecordMarkdown.AppendBacklinks(markdown, backlinks);
        }

        if (request.IncludeComments)
        {
            markdown = CommentText.AppendTo(markdown, await CommentReads.ThreadsAsync(db, record.Id, includeResolved: true, cancellationToken).ConfigureAwait(false));
        }

        return new RecordMarkdownView(markdown, relations, backlinks);
    }
}
