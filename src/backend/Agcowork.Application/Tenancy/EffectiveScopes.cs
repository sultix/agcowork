namespace Agcowork.Application.Tenancy;

/// <summary>What a caller may do in a workspace: the membership's scopes, narrowed by the API key.</summary>
public static class EffectiveScopes
{
    /// <summary>
    /// Membership scopes ∩ key scopes. An empty <paramref name="keyScopes"/> means "not restricted"
    /// (sessions, unrestricted keys), so the membership scopes apply in full.
    /// </summary>
    public static IReadOnlySet<string> Compute(IEnumerable<string> membershipScopes, IReadOnlySet<string> keyScopes)
    {
        ArgumentNullException.ThrowIfNull(membershipScopes);
        ArgumentNullException.ThrowIfNull(keyScopes);

        var scopes = membershipScopes.ToHashSet(StringComparer.Ordinal);
        if (keyScopes.Count > 0)
        {
            scopes.IntersectWith(keyScopes);
        }

        return scopes;
    }

    /// <summary>Ordinal-sorted list form for DTOs.</summary>
    public static IReadOnlyList<string> Sorted(IReadOnlySet<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        return [.. scopes.Order(StringComparer.Ordinal)];
    }
}
