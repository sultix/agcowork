using Agcowork.Application.Common.Persistence;
using Agcowork.Domain.Collections.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Schema;

/// <summary>The schema of a collection as needed to validate records.</summary>
/// <param name="CollectionId">Collection id.</param>
/// <param name="SpaceId">Space of the collection.</param>
/// <param name="Properties">Ordered property definitions.</param>
/// <param name="SchemaVersion">Version the definitions belong to.</param>
/// <param name="TitlePropertyKey">Key of the title property.</param>
/// <param name="IsArchived">Archived collections are read-only.</param>
public sealed record CollectionSchemaSnapshot(
    Guid CollectionId,
    Guid SpaceId,
    IReadOnlyList<PropertyDefinition> Properties,
    int SchemaVersion,
    string TitlePropertyKey,
    bool IsArchived);

/// <summary>
/// Schemas of collections in the current workspace, for use cases that validate many records (AP-007 onwards).
/// Scoped: one cache per request, so one tenant; collections of other workspaces are invisible (null).
/// </summary>
public interface ISchemaProvider
{
    /// <returns>The schema, or null when the collection does not exist in the current workspace.</returns>
    Task<CollectionSchemaSnapshot?> GetSchemaAsync(Guid collectionId, CancellationToken cancellationToken);

    /// <summary>Forgets a cached schema, e.g. after changing it within the same request.</summary>
    void Invalidate(Guid collectionId);
}

internal sealed class SchemaProvider(IAgcoworkDbContext db) : ISchemaProvider
{
    // Per request scope; the scope never switches tenants, so the collection id is a sufficient key.
    private readonly Dictionary<Guid, CollectionSchemaSnapshot?> _cache = [];

    public async Task<CollectionSchemaSnapshot?> GetSchemaAsync(Guid collectionId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(collectionId, out var cached))
        {
            return cached;
        }

        var snapshot = await db.Collections.AsNoTracking()
            .Where(c => c.Id == collectionId)
            .Select(c => new CollectionSchemaSnapshot(c.Id, c.SpaceId, c.Schema, c.SchemaVersion, c.TitlePropertyKey, c.ArchivedAt != null))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        _cache[collectionId] = snapshot;
        return snapshot;
    }

    public void Invalidate(Guid collectionId) => _cache.Remove(collectionId);
}
