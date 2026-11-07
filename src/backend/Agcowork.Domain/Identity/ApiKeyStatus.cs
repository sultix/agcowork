namespace Agcowork.Domain.Identity;

/// <summary>Outcome of checking whether an API key may authenticate right now.</summary>
public enum ApiKeyStatus
{
    Active = 1,
    Revoked = 2,
    Expired = 3,
    ActorInactive = 4,
}
