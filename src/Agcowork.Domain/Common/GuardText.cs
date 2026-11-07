namespace Agcowork.Domain.Common;

/// <summary>Shared guards for display texts: trimmed, bounded, optional ones collapse to null.</summary>
public static class GuardText
{
    /// <summary>Trimmed <paramref name="value"/>; throws when it is null, whitespace or longer than <paramref name="maxLength"/>.</summary>
    public static string Required(string value, int maxLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"Must not exceed {maxLength} characters.", parameterName);
        }

        return trimmed;
    }

    /// <summary>Trimmed <paramref name="value"/> or null when it is null or whitespace; throws when longer than <paramref name="maxLength"/>.</summary>
    public static string? Optional(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"Must not exceed {maxLength} characters.", parameterName);
        }

        return trimmed;
    }
}
