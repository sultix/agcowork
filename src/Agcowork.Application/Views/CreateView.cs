using Agcowork.Application.Activity;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Schema;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Views;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Views;

/// <param name="CollectionId">Collection the view belongs to.</param>
/// <param name="Name">Display name, unique within the collection, max. 100 characters, e.g. <c>Board nach Status</c>.</param>
/// <param name="Type">table, board, gallery, list, document or raw. A board needs <paramref name="GroupBy"/>.</param>
/// <param name="Filter">Filter DSL: { "and": [ { "prop", "op", "value" }, … ] } / "or" / "not"; operators per property type (<see cref="Query.OperatorMatrix"/>). Omit for all records.</param>
/// <param name="Sorts">[ { "prop", "dir": "asc"|"desc" } ] in order of priority.</param>
/// <param name="GroupBy">Key of a select/status property; board columns. Required for boards.</param>
/// <param name="VisibleProperties">Keys of the properties shown; omit or empty for all.</param>
/// <param name="CardCoverProperty">Gallery: key of the property used as cover.</param>
/// <param name="IsDefault">Make this the collection's default view (the previous default stops being one).</param>
/// <param name="SortOrder">Position among the views; default: last.</param>
public sealed record CreateViewRequest(
    Guid CollectionId,
    string? Name,
    string? Type,
    ViewFilterNode? Filter = null,
    IReadOnlyList<ViewSortInput>? Sorts = null,
    string? GroupBy = null,
    IReadOnlyList<string>? VisibleProperties = null,
    string? CardCoverProperty = null,
    bool IsDefault = false,
    int? SortOrder = null);

/// <summary>Creates a view of a collection. Requires <c>write</c> (views are presentation, not schema).</summary>
public sealed class CreateView(
    IAgcoworkDbContext db,
    ICurrentActor currentActor,
    ITenantContext tenant,
    ISchemaProvider schemas,
    TimeProvider timeProvider,
    IActivityRecorder activity)
    : IUseCase<CreateViewRequest, ViewDto>
{
    public async Task<Result<ViewDto>> HandleAsync(CreateViewRequest request, CancellationToken cancellationToken)
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
        ViewSupport.Name(errors, request.Name);
        if (string.IsNullOrWhiteSpace(request.Type))
        {
            errors.Add("type", ErrorCodes.Required, "'type' is required.", ViewTypes.AllWireNames);
        }

        if (errors.Any)
        {
            return errors.ToError();
        }

        // Shared collection lock first (collection → anything): a schema change waits for us and then cleans up this view too.
        await using var transaction = await db.BeginOrJoinTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.LockCollectionSharedAsync(request.CollectionId, cancellationToken).ConfigureAwait(false);
        var (schema, error) = await ViewSupport.LoadWritableSchemaAsync(schemas, request.CollectionId, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var type = ViewDefinitionValidator.Validate(
            errors, schema!.Properties, new ViewDefinitionInput(request.Type, request.Filter, request.Sorts, request.GroupBy, request.VisibleProperties, request.CardCoverProperty));
        if (type is { } parsed)
        {
            ViewDefinitionValidator.RequireGroupBy(errors, schema.Properties, parsed, request.GroupBy);
        }

        if (errors.Any)
        {
            return errors.ToError();
        }

        var name = request.Name!.Trim();
        if (await db.Views.AnyAsync(v => v.CollectionId == schema.CollectionId && v.Name == name, cancellationToken).ConfigureAwait(false))
        {
            return ViewConflicts.Name.Error;
        }

        var sortOrder = request.SortOrder
            ?? (await db.Views.Where(v => v.CollectionId == schema.CollectionId).MaxAsync(v => (int?)v.SortOrder, cancellationToken).ConfigureAwait(false) ?? -1) + 1;
        var view = View.Create(
            tenant.WorkspaceId,
            schema.CollectionId,
            name,
            type!.Value,
            request.Filter,
            request.Sorts?.Select(s => new ViewSort(s.Prop!, s.Dir!)),
            request.GroupBy,
            request.VisibleProperties,
            request.CardCoverProperty,
            request.IsDefault,
            isSystem: false,
            sortOrder,
            currentActor.Id,
            timeProvider);
        db.Views.Add(view);
        if (view.IsDefault)
        {
            await ViewSupport.ClearOtherDefaultsAsync(db, schema.CollectionId, view.Id, cancellationToken).ConfigureAwait(false);
        }

        activity.Record(
            ActivityTypes.ViewCreated,
            ActivityTargets.View,
            view.Id,
            schema.SpaceId,
            schema.CollectionId,
            new ActivityDiff().Set("name", view.Name).Set("type", ViewTypes.ToWire(view.Type)).ToJson());

        if (await db.SaveChangesOrConflictAsync(cancellationToken, ViewConflicts.Name, ViewConflicts.Default).ConfigureAwait(false) is { } conflict)
        {
            return conflict;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ViewDto.From(view);
    }
}
