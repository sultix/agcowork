using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;

namespace Agcowork.Application.Workspaces;

/// <summary>A member of the workspace.</summary>
/// <param name="ActorId">The member; use it in <c>/members/{actorId}</c>.</param>
/// <param name="Name">Display name of the actor.</param>
/// <param name="Kind"><c>human</c> or <c>agent</c>.</param>
/// <param name="Role">Role in this workspace.</param>
/// <param name="Scopes">Membership scopes (role defaults unless narrowed).</param>
/// <param name="CreatedBy">Actor that added the member.</param>
/// <param name="CreatedAt">When the member was added (UTC).</param>
public sealed record MemberDto(
    Guid ActorId,
    string Name,
    ActorKind Kind,
    WorkspaceRole Role,
    IReadOnlyList<string> Scopes,
    Guid CreatedBy,
    DateTimeOffset CreatedAt)
{
    public static MemberDto From(Membership membership, Actor actor)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(actor);
        return new MemberDto(actor.Id, actor.Name, actor.Kind, membership.Role, [.. membership.Scopes], membership.CreatedBy, membership.CreatedAt);
    }
}
