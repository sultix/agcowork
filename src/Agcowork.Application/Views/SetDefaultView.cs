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

public sealed record SetDefaultViewRequest(Guid ViewId);

/// <summary>Makes a view the default of its collection; the previous default stops being one. Idempotent. Requires <c>write</c>.</summary>
public sealed class SetDefaultView(IAgcoworkDbContext db, ITenantContext tenant, ISchemaProvider schemas, IActivityRecorder activity)
    : IUseCase<SetDefaultViewRequest, ViewDto>
{
    public async Task<Result<ViewDto>> HandleAsync(SetDefaultViewRequest request, CancellationToken cancellationToken)
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

        if (!view.IsDefault)
        {
            await ViewSupport.ClearOtherDefaultsAsync(db, view.CollectionId, view.Id, cancellationToken).ConfigureAwait(false);
            view.MarkDefault(true);
            activity.Record(
                ActivityTypes.ViewUpdated,
                ActivityTargets.View,
                view.Id,
                schema!.SpaceId,
                schema.CollectionId,
                new ActivityDiff().Change("isDefault", false, true).ToJson());
            if (await db.SaveChangesOrConflictAsync(cancellationToken, ViewConflicts.Default).ConfigureAwait(false) is { } conflict)
            {
                return conflict;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ViewDto.From(view);
    }
}
