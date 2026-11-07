using Agcowork.Domain.Identity;

namespace Agcowork.Application.Common.Security;

/// <summary>
/// The authenticated caller of the current request, as established by the auth pipeline.
/// The only source of "who is acting": never take an actor id from request input.
/// </summary>
/// <remarks>
/// Members other than <see cref="IsAuthenticated"/> throw when the caller is anonymous; use
/// <see cref="CurrentActorAuthorization.RequireAuthenticated"/> or
/// <see cref="CurrentActorAuthorization.RequireInstanceScope"/> first.
/// </remarks>
public interface ICurrentActor
{
    bool IsAuthenticated { get; }

    Guid Id { get; }

    ActorKind Kind { get; }

    IReadOnlySet<string> InstanceScopes { get; }

    /// <summary>Set when the caller authenticated with an API key.</summary>
    Guid? ApiKeyId { get; }

    /// <summary>Workspace the key is restricted to, if any. Evaluated by the tenancy layer.</summary>
    Guid? KeyWorkspaceRestriction { get; }

    /// <summary>Scope restriction of the key; empty when not restricted or not a key.</summary>
    IReadOnlySet<string> KeyScopes { get; }
}
