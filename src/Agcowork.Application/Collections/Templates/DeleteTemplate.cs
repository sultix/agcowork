using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Collections.Templates;

public sealed record DeleteTemplateRequest(Guid CollectionId, Guid TemplateId);

/// <summary>Deletes a record template. Records created from it are not affected. Requires <c>schema</c>.</summary>
public sealed class DeleteTemplate(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<DeleteTemplateRequest>
{
    public async Task<Result> HandleAsync(DeleteTemplateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireWritable() is { } archived)
        {
            return archived;
        }

        if (tenant.RequireScope(WorkspaceScope.Schema) is { } denied)
        {
            return denied;
        }

        var (collection, error) = await TemplateSupport.LoadWritableCollectionAsync(db, request.CollectionId, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var template = await db.Templates
            .SingleOrDefaultAsync(t => t.Id == request.TemplateId && t.CollectionId == collection!.Id, cancellationToken)
            .ConfigureAwait(false);
        if (template is null)
        {
            return new NotFoundError("template");
        }

        db.Templates.Remove(template);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
