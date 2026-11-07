using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Schema;
using Agcowork.Application.Spaces;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Collections;

/// <param name="SpaceId">Space the collection belongs to (see GET /spaces).</param>
/// <param name="Name">Display name, unique within the space, max. 200 characters, e.g. <c>Entscheidungen</c>.</param>
/// <param name="Description">Required, max. 4000 characters: what a record is, when to add one, what matters. Agents decide by it.</param>
/// <param name="Schema">Property definitions in display order. Optional; the title property (key <c>title</c>, text) is added in front when missing.</param>
public sealed record CreateCollectionRequest(Guid? SpaceId, string? Name, string? Description, IReadOnlyList<PropertyDefinitionInput>? Schema = null);

/// <summary>
/// Creates a collection in a space of the current workspace. Requires <c>write</c>. Every registered
/// <see cref="ICollectionCreatedHook"/> runs before the save (standard views, AP-008).
/// </summary>
public sealed class CreateCollection(
    IAgcoworkDbContext db,
    ICurrentActor currentActor,
    ITenantContext tenant,
    TimeProvider timeProvider,
    IEnumerable<ICollectionCreatedHook> hooks)
    : IUseCase<CreateCollectionRequest, CollectionDto>
{
    public async Task<Result<CollectionDto>> HandleAsync(CreateCollectionRequest request, CancellationToken cancellationToken)
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
        if (request.SpaceId is null || request.SpaceId == Guid.Empty)
        {
            errors.Add("spaceId", ErrorCodes.Required, "'spaceId' is required: the space the collection belongs to (see GET /spaces).");
        }

        CollectionInputValidation.Name(errors, request.Name);
        CollectionInputValidation.Description(errors, request.Description);
        var parsed = SchemaInput.ParseList(errors, "schema", request.Schema ?? []);
        if (errors.Any)
        {
            return errors.ToError();
        }

        var definitions = parsed.Select(p => p!).ToList();
        var spaceId = request.SpaceId!.Value;
        var space = await db.Spaces.AsNoTracking().SingleOrDefaultAsync(s => s.Id == spaceId, cancellationToken).ConfigureAwait(false);
        if (space is null)
        {
            errors.Add("spaceId", ErrorCodes.NotFound, "No space with this id exists in this workspace.");
        }
        else if (space.IsArchived)
        {
            return SpaceConflicts.Archived("spaceId");
        }

        await CollectionInputValidation.CheckRelationTargetsAsync(
            db, errors, [.. definitions.Select((d, i) => ($"schema[{i}].targetCollectionId", d))], cancellationToken).ConfigureAwait(false);
        if (errors.Any)
        {
            return errors.ToError();
        }

        if (await db.Collections.AnyAsync(c => c.SpaceId == spaceId && c.Name == request.Name!.Trim(), cancellationToken).ConfigureAwait(false))
        {
            return CollectionConflicts.Name.Error;
        }

        var collection = Collection.Create(tenant.WorkspaceId, spaceId, request.Name!, request.Description!, definitions, currentActor.Id, timeProvider);
        db.Collections.Add(collection);
        foreach (var hook in hooks)
        {
            await hook.OnCollectionCreatedAsync(collection, cancellationToken).ConfigureAwait(false);
        }

        if (await db.SaveChangesOrConflictAsync(cancellationToken, CollectionConflicts.Name).ConfigureAwait(false) is { } conflict)
        {
            return conflict;
        }

        return CollectionDto.From(collection);
    }
}
