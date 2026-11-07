using Agcowork.Application.Collections;
using Agcowork.Application.Common.Persistence;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Views;

namespace Agcowork.Application.Views;

/// <summary>
/// Gives every new collection its standard views <c>Tabelle</c> (table, default) and <c>Rohdaten</c> (raw). Both are system
/// views: renamable and configurable, not deletable. Runs inside <see cref="CreateCollection"/>, stored with the collection.
/// </summary>
public sealed class DefaultViewsHook(IAgcoworkDbContext db, TimeProvider timeProvider) : ICollectionCreatedHook
{
    public const string TableName = "Tabelle";
    public const string RawName = "Rohdaten";

    public Task OnCollectionCreatedAsync(Collection collection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);

        db.Views.Add(View.Create(
            collection.WorkspaceId, collection.Id, TableName, ViewType.Table, null, null, null, null, null,
            isDefault: true, isSystem: true, sortOrder: 0, collection.CreatedBy, timeProvider));
        db.Views.Add(View.Create(
            collection.WorkspaceId, collection.Id, RawName, ViewType.Raw, null, null, null, null, null,
            isDefault: false, isSystem: true, sortOrder: 1, collection.CreatedBy, timeProvider));
        return Task.CompletedTask;
    }
}
