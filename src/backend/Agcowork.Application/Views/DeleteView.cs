using Agcowork.Application.Activity;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Schema;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Views;

public sealed record DeleteViewRequest(Guid ViewId);

/// <summary>
/// Deletes a view. System views (table, raw) are not deletable (409 <c>system_view</c>). Deleting the default view makes the
/// remaining view with the smallest sort order the default. Requires <c>write</c>.
/// </summary>
public sealed class DeleteView(IAgcoworkDbContext db, ITenantContext tenant, ISchemaProvider schemas, IActivityRecorder activity)
    : IUseCase<DeleteViewRequest>
{
    public async Task<Result> HandleAsync(DeleteViewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireWritable() is { } archived)
        {
            return archived;
        }

        if (tenant.RequireScope(WorkspaceScope.Write) is { } denied)
        {
            return denied;
        }

        await using var transaction = await db.BeginOrJoinTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await ViewSupport.LockCollectionOfViewAsync(db, request.ViewId, cancellationToken).ConfigureAwait(false) is not { } collectionId)
        {
            return new NotFoundError("view");
        }

        var (schema, error) = await ViewSupport.LoadWritableSchemaAsync(schemas, collectionId, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var view = await db.Views.SingleOrDefaultAsync(v => v.Id == request.ViewId, cancellationToken).ConfigureAwait(false);
        if (view is null)
        {
            return new NotFoundError("view");
        }

        if (!view.IsDeletable)
        {
            return ViewConflicts.SystemView;
        }

        db.Views.Remove(view);
        activity.Record(
            ActivityTypes.ViewDeleted, ActivityTargets.View, view.Id, schema!.SpaceId, schema.CollectionId, new ActivityDiff().Set("name", view.Name).ToJson());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (view.IsDefault)
        {
            // Exactly one default per collection: the view with the smallest SortOrder (then name) takes over.
            var next = await db.Views
                .Where(v => v.CollectionId == view.CollectionId)
                .OrderBy(v => v.SortOrder).ThenBy(v => v.Name)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (next is not null)
            {
                next.MarkDefault(true);
                if (await db.SaveChangesOrConflictAsync(cancellationToken, ViewConflicts.Default).ConfigureAwait(false) is { } conflict)
                {
                    return conflict;
                }
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
