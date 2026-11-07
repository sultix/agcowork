namespace Agcowork.Domain.Identity;

/// <summary>
/// Password credential of a human actor (1:1). Kept apart from <see cref="Actor"/> so agents
/// never carry a password field.
/// </summary>
public sealed class LocalLogin
{
    private LocalLogin(Guid actorId, string passwordHash, DateTimeOffset updatedAt)
    {
        ActorId = actorId;
        PasswordHash = passwordHash;
        UpdatedAt = updatedAt;
    }

    public Guid ActorId { get; private set; }

    /// <summary>Opaque hash produced by the application's password hasher. Never the password.</summary>
    public string PasswordHash { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static LocalLogin Create(Actor actor, string passwordHash, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (actor.Kind != ActorKind.Human)
        {
            throw new ArgumentException("Only human actors can have a local login.", nameof(actor));
        }

        return new LocalLogin(actor.Id, passwordHash, timeProvider.GetUtcNow());
    }

    public void ChangePasswordHash(string passwordHash, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        ArgumentNullException.ThrowIfNull(timeProvider);
        PasswordHash = passwordHash;
        UpdatedAt = timeProvider.GetUtcNow();
    }
}
