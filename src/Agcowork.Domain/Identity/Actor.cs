using System.Net.Mail;

namespace Agcowork.Domain.Identity;

/// <summary>
/// A principal of the installation: a human or an agent. Actors are global (not workspace-bound);
/// workspace rights come from memberships, instance rights from <see cref="InstanceScope"/>.
/// </summary>
/// <remarks>
/// Guards throw <see cref="ArgumentException"/>: they protect invariants against programming
/// errors. User input is validated beforehand in the application layer, which reports
/// structured field errors.
/// </remarks>
public sealed class Actor
{
    public const int NameMaxLength = 200;
    public const int RoleMaxLength = 100;
    public const int EmailMaxLength = 320;

    private Actor(Guid id, ActorKind kind, string name, string? role, string? email, IReadOnlyList<string> instanceScopes, DateTimeOffset createdAt)
    {
        Id = id;
        Kind = kind;
        Name = name;
        Role = role;
        Email = email;
        InstanceScopes = instanceScopes;
        IsActive = true;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public ActorKind Kind { get; private set; }

    public string Name { get; private set; }

    /// <summary>Free-text role description, e.g. "Tech-Lead". Not a permission.</summary>
    public string? Role { get; private set; }

    /// <summary>Normalized (trimmed, lower case). Required for humans, optional for agents, unique when set.</summary>
    public string? Email { get; private set; }

    /// <summary>Distinct, ordinal-sorted subset of <see cref="InstanceScope.All"/>.</summary>
    public IReadOnlyList<string> InstanceScopes { get; private set; }

    /// <summary>Inactive actors cannot log in and their API keys are rejected.</summary>
    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Actor CreateHuman(string name, string email, string? role, IEnumerable<string> instanceScopes, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return Create(ActorKind.Human, name, email, role, instanceScopes, timeProvider);
    }

    public static Actor CreateAgent(string name, string? role, string? email, IEnumerable<string> instanceScopes, TimeProvider timeProvider) =>
        Create(ActorKind.Agent, name, email, role, instanceScopes, timeProvider);

    public void Rename(string name) => Name = GuardName(name);

    public void ChangeRole(string? role) => Role = GuardRole(role);

    public void SetInstanceScopes(IEnumerable<string> instanceScopes) => InstanceScopes = GuardInstanceScopes(instanceScopes);

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;

    public bool HasInstanceScope(string scope) => InstanceScopes.Contains(scope, StringComparer.Ordinal);

    /// <summary>Canonical form used for storage and lookup: trimmed and lower-cased (invariant).</summary>
    public static string NormalizeEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return email.Trim().ToLowerInvariant();
    }

    /// <summary>Plain address check (<c>local@domain</c>, no display name, no whitespace).</summary>
    public static bool IsValidEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var trimmed = email.Trim();
        if (trimmed.Length == 0 || trimmed.Length > EmailMaxLength || trimmed.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return MailAddress.TryCreate(trimmed, out var address)
            && string.Equals(address.Address, trimmed, StringComparison.Ordinal)
            && address.Host.Contains('.', StringComparison.Ordinal);
    }

    private static Actor Create(ActorKind kind, string name, string? email, string? role, IEnumerable<string> instanceScopes, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var createdAt = timeProvider.GetUtcNow();
        return new Actor(
            Guid.CreateVersion7(createdAt),
            kind,
            GuardName(name),
            GuardRole(role),
            GuardEmail(email),
            GuardInstanceScopes(instanceScopes),
            createdAt);
    }

    private static string GuardName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        if (trimmed.Length > NameMaxLength)
        {
            throw new ArgumentException($"Name must not exceed {NameMaxLength} characters.", nameof(name));
        }

        return trimmed;
    }

    private static string? GuardRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return null;
        }

        var trimmed = role.Trim();
        if (trimmed.Length > RoleMaxLength)
        {
            throw new ArgumentException($"Role must not exceed {RoleMaxLength} characters.", nameof(role));
        }

        return trimmed;
    }

    private static string? GuardEmail(string? email)
    {
        if (email is null)
        {
            return null;
        }

        if (!IsValidEmail(email))
        {
            throw new ArgumentException("Email is not a valid address.", nameof(email));
        }

        return NormalizeEmail(email);
    }

    private static string[] GuardInstanceScopes(IEnumerable<string> instanceScopes)
    {
        ArgumentNullException.ThrowIfNull(instanceScopes);
        var scopes = instanceScopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var unknown = scopes.FirstOrDefault(scope => !InstanceScope.IsKnown(scope));
        if (unknown is not null)
        {
            throw new ArgumentException($"Unknown instance scope '{unknown}'.", nameof(instanceScopes));
        }

        return scopes;
    }
}
