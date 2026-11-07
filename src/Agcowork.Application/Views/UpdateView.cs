using Agcowork.Application.Activity;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Schema;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Views;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Views;

/// <summary>Partial update: null leaves a field unchanged. Use <c>POST …/default</c> to change the default view.</summary>
/// <param name="ViewId">View to change.</param>
/// <param name="Name">New display name.</param>
/// <param name="Type">New type (not for system views).</param>
/// <param name="Filter">New complete filter (replaces the old one); <c>{}</c> removes the filter.</param>
/// <param name="Sorts">New complete sort list; an empty list removes the sorting.</param>
/// <param name="GroupBy">New grouping property key; an empty string removes it (not for boards).</param>
/// <param name="VisibleProperties">New complete list of visible property keys; an empty list shows all.</param>
/// <param name="CardCoverProperty">New cover property key; an empty string removes it.</param>
/// <param name="SortOrder">New position among the collection's views.</param>
public sealed record UpdateViewRequest(
    Guid ViewId,
    string? Name = null,
    string? Type = null,
    ViewFilterNode? Filter = null,
    IReadOnlyList<ViewSortInput>? Sorts = null,
    string? GroupBy = null,
    IReadOnlyList<string>? VisibleProperties = null,
    string? CardCoverProperty = null,
    int? SortOrder = null);

/// <summary>Changes a view (all settings except collection and system flag). Requires <c>write</c>.</summary>
public sealed class UpdateView(IAgcoworkDbContext db, ITenantContext tenant, ISchemaProvider schemas, IActivityRecorder activity)
    : IUseCase<UpdateViewRequest, ViewDto>
{
    public async Task<Result<ViewDto>> HandleAsync(UpdateViewRequest request, CancellationToken cancellationToken)
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

        var errors = new FieldErrors();
        if (request.Name is not null)
        {
            ViewSupport.Name(errors, request.Name);
        }

        if (errors.Any)
        {
            return errors.ToError();
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

        var before = ViewDto.From(view);
        var touchesDefinition = request.Type is not null || request.Filter is not null || request.Sorts is not null
            || request.GroupBy is not null || request.VisibleProperties is not null || request.CardCoverProperty is not null;
        var type = view.Type;
        if (touchesDefinition)
        {
            var parsed = ViewDefinitionValidator.Validate(
                errors, schema!.Properties, new ViewDefinitionInput(request.Type, request.Filter, request.Sorts, request.GroupBy, request.VisibleProperties, request.CardCoverProperty));
            if (parsed is { } newType)
            {
                if (view.IsSystem && newType != view.Type)
                {
                    errors.Add("type", ErrorCodes.NotAllowed, $"System views keep their type ({ViewTypes.ToWire(view.Type)}); create another view for a different type.");
                }

                type = newType;
            }

            // "" removes the grouping; null keeps it.
            var groupBy = request.GroupBy is null ? view.GroupByProperty : request.GroupBy.Trim().Length == 0 ? null : request.GroupBy.Trim();
            ViewDefinitionValidator.RequireGroupBy(errors, schema.Properties, type, groupBy);
        }

        if (errors.Any)
        {
            return errors.ToError();
        }

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (await db.Views.AnyAsync(v => v.CollectionId == view.CollectionId && v.Name == name && v.Id != view.Id, cancellationToken).ConfigureAwait(false))
            {
                return ViewConflicts.Name.Error;
            }

            view.Rename(name);
        }

        if (touchesDefinition)
        {
            view.Configure(
                type,
                request.Filter is null ? view.Filter : request.Filter.IsEmpty ? null : request.Filter,
                request.Sorts is null ? view.Sorts : request.Sorts.Select(s => new ViewSort(s.Prop!, s.Dir!)),
                request.GroupBy is null ? view.GroupByProperty : request.GroupBy,
                request.VisibleProperties ?? view.VisibleProperties,
                request.CardCoverProperty ?? view.CardCoverProperty);
        }

        if (request.SortOrder is { } sortOrder)
        {
            view.MoveTo(sortOrder);
        }

        var after = ViewDto.From(view);
        var diff = new ActivityDiff()
            .Change("name", ViewSupport.ToNode(before.Name), ViewSupport.ToNode(after.Name))
            .Change("type", ViewSupport.ToNode(before.Type), ViewSupport.ToNode(after.Type))
            .Change("filter", ViewSupport.ToNode(before.Filter), ViewSupport.ToNode(after.Filter))
            .Change("sorts", ViewSupport.ToNode(before.Sorts), ViewSupport.ToNode(after.Sorts))
            .Change("groupBy", ViewSupport.ToNode(before.GroupBy), ViewSupport.ToNode(after.GroupBy))
            .Change("visibleProperties", ViewSupport.ToNode(before.VisibleProperties), ViewSupport.ToNode(after.VisibleProperties))
            .Change("cardCoverProperty", ViewSupport.ToNode(before.CardCoverProperty), ViewSupport.ToNode(after.CardCoverProperty))
            .Change("sortOrder", ViewSupport.ToNode(before.SortOrder), ViewSupport.ToNode(after.SortOrder))
            .ToJson();
        if (diff.Count > 0)
        {
            activity.Record(ActivityTypes.ViewUpdated, ActivityTargets.View, view.Id, schema!.SpaceId, schema.CollectionId, diff);
        }

        if (await db.SaveChangesOrConflictAsync(cancellationToken, ViewConflicts.Name).ConfigureAwait(false) is { } conflict)
        {
            return conflict;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return after;
    }
}
