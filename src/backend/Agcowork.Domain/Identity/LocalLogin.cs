namespace Agcowork.Domain.Identity;

/// <summary>
/// Password credential of a human actor (1:1). Kept apart from <see cref="Actor"/> so agents
/// never carry a password field.
/// </summary>
/// <remarks>
/// <see cref="UpdatedAt"/> is the time the password last changed and doubles as credential stamp: a session cookie
/// carries the value it was issued for and is valid only while it equals the current one, so every password change or
/// reset signs out all existing sessions of the actor. It is kept at microsecond precision, which PostgreSQL stores
/// exactly, so the stamp survives the database round trip unchanged whatever clock is injected.
/// </remarks>
public sealed class LocalLogin
{
    private LocalLogin(Guid actorId, string passwordHash, DateTimeOffset updatedAt, bool mustChangePassword)
    {
        ActorId = actorId;
        PasswordHash = passwordHash;
        UpdatedAt = updatedAt;
        MustChangePassword = mustChangePassword;
    }

    public Guid ActorId { get; private set; }

    /// <summary>Opaque hash produced by the application's password hasher. Never the password.</summary>
    public string PasswordHash { get; private set; }

    /// <summary>When the password last changed; sessions issued before are invalid.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// The human should replace the password (bootstrap password, admin reset). Informational for the UI,
    /// which enforces the change; the server does not block other endpoints.
    /// </summary>
    public bool MustChangePassword { get; private set; }

    public static LocalLogin Create(Actor actor, string passwordHash, TimeProvider timeProvider, bool mustChangePassword = false)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (actor.Kind != ActorKind.Human)
        {
            throw new ArgumentException("Only human actors can have a local login.", nameof(actor));
        }

        return new LocalLogin(actor.Id, passwordHash, Now(timeProvider), mustChangePassword);
    }

    /// <summary>
    /// Sets a new password: replaces the hash, moves <see cref="UpdatedAt"/> to now (which invalidates all sessions
    /// issued before) and sets <see cref="MustChangePassword"/>.
    /// </summary>
    /// <param name="passwordHash">Hash of the new password.</param>
    /// <param name="mustChangePassword">False when the human chose the password, true for an admin reset that the human should replace.</param>
    /// <param name="timeProvider">Clock of <see cref="UpdatedAt"/>.</param>
    public void ChangePassword(string passwordHash, bool mustChangePassword, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        ArgumentNullException.ThrowIfNull(timeProvider);
        PasswordHash = passwordHash;
        UpdatedAt = Now(timeProvider);
        MustChangePassword = mustChangePassword;
    }

    /// <summary>Flags the password for replacement without changing it (sessions stay valid).</summary>
    public void RequirePasswordChange() => MustChangePassword = true;

    private static DateTimeOffset Now(TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        return new DateTimeOffset(now.UtcTicks - (now.UtcTicks % TimeSpan.TicksPerMicrosecond), TimeSpan.Zero);
    }
}
