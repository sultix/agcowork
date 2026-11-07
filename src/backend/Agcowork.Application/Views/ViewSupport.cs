using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agcowork.Application.Collections;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Schema;
using Agcowork.Domain.Views;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Views;

public static class ViewConflicts
{
    public static UniqueConflict Name { get; } = new(
        "ix_views_workspace_id_collection_id_name",
        new ConflictError("view_name_taken", "The collection already has a view with this name. Choose a different one.", "name"));

    public static UniqueConflict Default { get; } = new(
        "ix_views_default",
        new ConflictError("view_default_conflict", "Another view became the default concurrently. Read the views again and retry.", "isDefault"));

    public static ConflictError SystemView { get; } =
        new("system_view", "System views (table, raw) cannot be deleted. Rename or reconfigure them instead.");
}

/// <summary>Shared rules of the view use cases.</summary>
internal static class ViewSupport
{
    private static readonly JsonSerializerOptions DiffOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static void Name(FieldErrors errors, string? name)
    {
        if (errors.Required("name", name))
        {
            errors.MaxLength("name", name, View.NameMaxLength);
        }
    }

    /// <summary>The schema of a collection for a view change: 404 when missing, 409 when archived.</summary>
    public static async Task<(CollectionSchemaSnapshot? Schema, Error? Error)> LoadWritableSchemaAsync(
        ISchemaProvider schemas, Guid collectionId, CancellationToken cancellationToken)
    {
        var schema = await schemas.GetSchemaAsync(collectionId, cancellationToken).ConfigureAwait(false);
        if (schema is null)
        {
            return (null, new NotFoundError("collection"));
        }

        return schema.IsArchived ? (null, CollectionConflicts.Archived) : (schema, null);
    }

    /// <summary>
    /// Takes the shared collection lock for the collection of a view (lock order: collection before anything else; a
    /// schema change holds it exclusively while it cleans up views). Returns the collection id, null when the view does not exist.
    /// </summary>
    public static async Task<Guid?> LockCollectionOfViewAsync(IAgcoworkDbContext db, Guid viewId, CancellationToken cancellationToken)
    {
        var collectionId = await db.Views.AsNoTracking()
            .Where(v => v.Id == viewId)
            .Select(v => (Guid?)v.CollectionId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (collectionId is { } id)
        {
            await db.LockCollectionSharedAsync(id, cancellationToken).ConfigureAwait(false);
        }

        return collectionId;
    }

    /// <summary>Clears the default flag of the collection's other views. Run inside the transaction that sets the new default.</summary>
    public static Task ClearOtherDefaultsAsync(IAgcoworkDbContext db, Guid collectionId, Guid viewId, CancellationToken cancellationToken) =>
        // Tenant filter applies; no IgnoreQueryFilters and no WorkspaceId change (rules of IAgcoworkDbContext).
        db.Views
            .Where(v => v.CollectionId == collectionId && v.IsDefault && v.Id != viewId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(v => v.IsDefault, false), cancellationToken);

    public static JsonNode? ToNode<T>(T? value) => value is null ? null : JsonSerializer.SerializeToNode(value, DiffOptions);
}
