using Agcowork.Domain.Collections;

namespace Agcowork.Application.Collections;

/// <summary>
/// Extension point "a collection was created", e.g. for the standard views of AP-008. Every registered hook runs
/// inside <see cref="CreateCollection"/> after the collection is added to the context and before it is saved:
/// whatever a hook adds is stored in the same SaveChanges (same transaction), or not at all.
/// </summary>
/// <remarks>Hooks run in the caller's tenant; they must not save themselves.</remarks>
public interface ICollectionCreatedHook
{
    Task OnCollectionCreatedAsync(Collection collection, CancellationToken cancellationToken);
}
