using Agcowork.Domain.Identity;

namespace Agcowork.Application.Identity.Actors;

/// <summary>Wire names of <see cref="ActorKind"/> (snake_case, as serialized by the API).</summary>
public static class ActorKinds
{
    public const string Human = "human";
    public const string Agent = "agent";

    public static IReadOnlyList<string> All { get; } = [Human, Agent];

    public static ActorKind? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Human => ActorKind.Human,
        Agent => ActorKind.Agent,
        _ => null,
    };
}
