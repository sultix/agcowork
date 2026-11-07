using Agcowork.Application.Common.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Schema;

/// <summary>
/// Looks up relation targets for <see cref="PropertyValueValidator"/>: which of the given record ids exist in the
/// target collection of the current workspace. Implementations must stay inside the tenant (query filter).
/// </summary>
public interface IRelationTargetChecker
{
    /// <returns>The subset of <paramref name="recordIds"/> that are records of <paramref name="targetCollectionId"/>.</returns>
    Task<IReadOnlySet<Guid>> FindExistingAsync(Guid targetCollectionId, IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken);
}

/// <summary>Looks up actor references for <see cref="PropertyValueValidator"/>: which actors are members of the current workspace.</summary>
public interface IActorMembershipChecker
{
    /// <returns>
    /// The members among <paramref name="actorIds"/> with whether the actor is active; ids that are no member are absent.
    /// Deactivated actors are rejected as values (<c>actor_inactive</c>).
    /// </returns>
    Task<IReadOnlyDictionary<Guid, bool>> FindMembersAsync(IReadOnlyCollection<Guid> actorIds, CancellationToken cancellationToken);
}

/// <summary>
/// Placeholder until records exist (AP-007 replaces the registration): there are no records yet, so no
/// reference can be valid. Rejecting is the safe default; accepting unchecked ids could point into other workspaces.
/// </summary>
internal sealed class NoRecordsRelationTargetChecker : IRelationTargetChecker
{
    public Task<IReadOnlySet<Guid>> FindExistingAsync(Guid targetCollectionId, IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
}

/// <summary>Members of the current workspace, via the tenant-filtered memberships.</summary>
internal sealed class MembershipActorChecker(IAgcoworkDbContext db) : IActorMembershipChecker
{
    public async Task<IReadOnlyDictionary<Guid, bool>> FindMembersAsync(IReadOnlyCollection<Guid> actorIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actorIds);
        if (actorIds.Count == 0)
        {
            return new Dictionary<Guid, bool>();
        }

        var ids = actorIds.Distinct().ToList();
        // Memberships are tenant-filtered; actors are global and joined by id.
        var members = await (
                from membership in db.Memberships.AsNoTracking()
                join actor in db.Actors.AsNoTracking() on membership.ActorId equals actor.Id
                where ids.Contains(membership.ActorId)
                select new { actor.Id, actor.IsActive })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return members.ToDictionary(m => m.Id, m => m.IsActive);
    }
}
