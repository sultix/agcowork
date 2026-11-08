using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Collections.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Collections;

/// <summary>Field rules of the collection module. Mirrors the guards in <see cref="Collection"/>.</summary>
internal static class CollectionInputValidation
{
    public static void Name(FieldErrors errors, string? name)
    {
        if (errors.Required("name", name) && errors.Storable("name", name))
        {
            errors.MaxLength("name", name, Collection.NameMaxLength);
        }
    }

    public static void Description(FieldErrors errors, string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            errors.Add(
                "description",
                ErrorCodes.Required,
                "'description' is required: agents read it to decide what belongs in this collection and how to fill it. "
                + "Say what a record is, when to add one and what matters, e.g. 'Architecture decisions (ADR). Add one before changing a component.'");
            return;
        }

        if (errors.Storable("description", description))
        {
            errors.MaxLength("description", description, Collection.DescriptionMaxLength);
        }
    }

    /// <summary>
    /// Targets of new relations must be collections of the current workspace (the tenant filter hides all others) that are
    /// not archived: an archived collection, or one in an archived space, is read-only and cannot become a new target
    /// (<c>target_archived</c>, AP-037). Existing relations to a collection archived later stay valid; this check only runs
    /// for relations being created. <paramref name="relations"/>: field of the <c>targetCollectionId</c> and the definition.
    /// </summary>
    public static async Task CheckRelationTargetsAsync(
        IAgcoworkDbContext db,
        FieldErrors errors,
        IReadOnlyList<(string Field, PropertyDefinition Property)> relations,
        CancellationToken cancellationToken)
    {
        var targets = relations
            .Where(r => r.Property.Type == PropertyType.Relation && r.Property.TargetCollectionId is not null)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var ids = targets.Select(r => r.Property.TargetCollectionId!.Value).Distinct().ToList();
        var existing = await (
                from collection in db.Collections.AsNoTracking()
                join space in db.Spaces.AsNoTracking() on collection.SpaceId equals space.Id
                where ids.Contains(collection.Id)
                select new { collection.Id, Archived = collection.ArchivedAt != null || space.ArchivedAt != null })
            .ToDictionaryAsync(c => c.Id, c => c.Archived, cancellationToken)
            .ConfigureAwait(false);

        foreach (var (field, property) in targets)
        {
            var target = property.TargetCollectionId!.Value;
            if (!existing.TryGetValue(target, out var archived))
            {
                errors.Add(field, ErrorCodes.NotFound, $"No collection {target} exists in this workspace. Relations can only target collections of the same workspace.");
            }
            else if (archived)
            {
                errors.Add(
                    field,
                    ErrorCodes.TargetArchived,
                    $"Collection {target} is archived (or its space is). An archived collection is read-only and cannot become the target of a "
                    + "new relation; choose an active collection. Existing relations to it stay valid.");
            }
        }
    }
}

/// <summary>Conflicts of the collection module. Codes are part of the API contract.</summary>
public static class CollectionConflicts
{
    public static UniqueConflict Name { get; } = new(
        "ix_collections_workspace_id_space_id_name",
        new ConflictError("collection_name_taken", "The space already has a collection with this name. Choose a different one.", "name"));

    public static UniqueConflict TemplateName { get; } = new(
        "ix_templates_workspace_id_collection_id_name",
        new ConflictError("template_name_taken", "The collection already has a template with this name. Choose a different one.", "name"));

    public static ConflictError Archived { get; } =
        new("collection_archived", "The collection is archived and read-only.");

    /// <summary>The collection changed since the caller read it (expectedSchemaVersion or a concurrent change).</summary>
    public static ConflictError SchemaVersion(int current, string? field = "expectedSchemaVersion") =>
        new(
            "schema_version_conflict",
            $"The collection was changed concurrently; its schema is now at version {current}. Read it again (GET …/schema) and retry your change.",
            field);
}

/// <summary>Saving collections: <c>schema_version</c> is the concurrency token of every collection update.</summary>
internal static class CollectionSave
{
    /// <summary>
    /// Saves; returns the matching unique conflict, or 409 <c>schema_version_conflict</c> when another request changed the
    /// collection in the meantime, null on success.
    /// </summary>
    public static async Task<Error?> SaveAsync(
        IAgcoworkDbContext db,
        Guid collectionId,
        string? versionField,
        CancellationToken cancellationToken,
        params UniqueConflict[] conflicts)
    {
        try
        {
            return await db.SaveChangesOrConflictAsync(cancellationToken, conflicts).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            var version = await db.Collections.AsNoTracking()
                .Where(c => c.Id == collectionId)
                .Select(c => (int?)c.SchemaVersion)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return version is { } current ? CollectionConflicts.SchemaVersion(current, versionField) : new NotFoundError("collection");
        }
    }
}
