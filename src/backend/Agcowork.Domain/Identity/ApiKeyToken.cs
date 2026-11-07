namespace Agcowork.Domain.Identity;

/// <summary>
/// Plain-text format of an API key: <c>agck_&lt;prefix&gt;_&lt;secret&gt;</c>, both parts Base62.
/// The prefix is stored in clear and identifies the key; only a SHA-256 hash of the secret is stored.
/// </summary>
public static class ApiKeyToken
{
    public const string Marker = "agck";
    public const int PrefixLength = 8;
    public const int SecretLength = 32;
    public const string Base62Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>Total length of a well-formed token.</summary>
    public const int Length = 4 + 1 + PrefixLength + 1 + SecretLength;

    public static string Format(string prefix, string secret)
    {
        if (!IsBase62(prefix, PrefixLength))
        {
            throw new ArgumentException($"Prefix must be {PrefixLength} Base62 characters.", nameof(prefix));
        }

        if (!IsBase62(secret, SecretLength))
        {
            throw new ArgumentException($"Secret must be {SecretLength} Base62 characters.", nameof(secret));
        }

        return $"{Marker}_{prefix}_{secret}";
    }

    /// <summary>Splits a token into prefix and secret. Returns false for anything that is not exactly the documented format.</summary>
    public static bool TryParse(string? token, out string prefix, out string secret)
    {
        prefix = string.Empty;
        secret = string.Empty;
        if (token is null || token.Length != Length)
        {
            return false;
        }

        var parts = token.Split('_');
        if (parts.Length != 3
            || !string.Equals(parts[0], Marker, StringComparison.Ordinal)
            || !IsBase62(parts[1], PrefixLength)
            || !IsBase62(parts[2], SecretLength))
        {
            return false;
        }

        prefix = parts[1];
        secret = parts[2];
        return true;
    }

    public static bool IsBase62(string? value, int length) =>
        value is not null && value.Length == length && value.All(char.IsAsciiLetterOrDigit);
}
