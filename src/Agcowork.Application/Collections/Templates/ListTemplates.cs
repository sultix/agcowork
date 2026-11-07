using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Collections.Templates;

public sealed record ListTemplatesRequest(Guid CollectionId);

/// <summary>Templates of a collection, default first, then by name. Requires <c>read</c>.</summary>
public sealed class ListTemplates(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<ListTemplatesRequest, IReadOnlyList<TemplateDto>>
{
    public async Task<Result<IReadOnlyList<TemplateDto>>> HandleAsync(ListTemplatesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        if (!await db.Collections.AnyAsync(c => c.Id == request.CollectionId, cancellationToken).ConfigureAwait(false))
        {
            return new NotFoundError("collection");
        }

        return Result.Success(await LoadAsync(db, request.CollectionId, cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<IReadOnlyList<TemplateDto>> LoadAsync(IAgcoworkDbContext db, Guid collectionId, CancellationToken cancellationToken)
    {
        var templates = await db.Templates.AsNoTracking()
            .Where(t => t.CollectionId == collectionId)
            .OrderByDescending(t => t.IsDefault).ThenBy(t => t.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. templates.Select(TemplateDto.From)];
    }
}
