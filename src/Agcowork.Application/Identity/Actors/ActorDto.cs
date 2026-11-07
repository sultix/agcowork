using Agcowork.Domain.Identity;

namespace Agcowork.Application.Identity.Actors;

/// <summary>Actor as seen by administrators and by the actor itself.</summary>
public sealed record ActorDto(
    Guid Id,
    ActorKind Kind,
    string Name,
    string? Role,
    string? Email,
    IReadOnlyList<string> InstanceScopes,
    bool IsActive,
    DateTimeOffset CreatedAt)
{
    public static ActorDto From(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new ActorDto(actor.Id, actor.Kind, actor.Name, actor.Role, actor.Email, [.. actor.InstanceScopes], actor.IsActive, actor.CreatedAt);
    }
}
