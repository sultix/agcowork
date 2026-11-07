namespace Agcowork.Domain.Identity;

/// <summary>Whether an actor is a person (logs in with a password) or an agent (authenticates with API keys).</summary>
public enum ActorKind
{
    Human = 1,
    Agent = 2,
}
