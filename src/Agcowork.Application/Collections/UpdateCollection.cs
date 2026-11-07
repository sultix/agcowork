using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Collections;

/// <summary>Partial update: null leaves a field unchanged.</summary>
/// <param name="CollectionId">Collection to change.</param>
/// <param name="Name">New display name, unique within the space.</param>
/// <param name="Description">New description; cannot be cleared.</param>
public sealed record UpdateCollectionRequest(Guid CollectionId, string? Name = null, string? Description = null);

/// <summary>Renames or re-describes a collection. Requires <c>schema</c> (the collection's structure, like its properties).</summary>
public sealed class UpdateCollection(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<UpdateCollectionRequest, CollectionDto>
{
    public async Task<Result<CollectionDto>> HandleAsync(UpdateCollectionRequest request, CancellationToken cancellationToken)
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

        var errors = new FieldErrors();
        if (request.Name is not null)
        {
            CollectionInputValidation.Name(errors, request.Name);
        }

        if (request.Description is not null)
        {
            CollectionInputValidation.Description(errors, request.Description);
        }

        if (errors.Any)
        {
            return errors.ToError();
        }

        var collection = await db.Collections.SingleOrDefaultAsync(c => c.Id == request.CollectionId, cancellationToken).ConfigureAwait(false);
        if (collection is null)
        {
            return new NotFoundError("collection");
        }

        if (collection.IsArchived)
        {
            return CollectionConflicts.Archived;
        }

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (await db.Collections.AnyAsync(c => c.SpaceId == collection.SpaceId && c.Name == name && c.Id != collection.Id, cancellationToken).ConfigureAwait(false))
            {
                return CollectionConflicts.Name.Error;
            }

            collection.Rename(name);
        }

        if (request.Description is not null)
        {
            collection.Describe(request.Description);
        }

        if (await CollectionSave.SaveAsync(db, collection.Id, null, cancellationToken, CollectionConflicts.Name).ConfigureAwait(false) is { } conflict)
        {
            return conflict;
        }

        return CollectionDto.From(collection);
    }
}
