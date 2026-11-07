using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;

namespace Agcowork.Application.Identity;

/// <summary>Unique constraints of the identity tables and the conflicts they produce (see <see cref="UniqueConflict"/>).</summary>
public static class IdentityConflicts
{
    public static UniqueConflict ActorEmail { get; } = new(
        "ix_actors_email",
        new ConflictError("email_taken", "Another actor already uses this email.", "email"));

    /// <summary>Two keys drew the same random prefix (practically impossible, 62^8). Retrying the request is safe.</summary>
    public static UniqueConflict ApiKeyPrefix { get; } = new(
        "ix_api_keys_prefix",
        new ConflictError("key_prefix_collision", "Generated key prefix collided with an existing key. Retry the request."));
}
