using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Agcowork.Application.Identity.Throttling;

/// <summary>What a credential limit counts (each kind has its own <see cref="AttemptLimit"/>).</summary>
public enum AttemptKind
{
    /// <summary>Failed logins for one email address, known or not.</summary>
    LoginAccount = 0,

    /// <summary>Failed logins from one client address (IPv6: per /64).</summary>
    LoginAddress = 1,

    /// <summary>Wrong <c>currentPassword</c> of one actor when changing their password.</summary>
    PasswordChange = 2,
}

/// <summary>
/// One counter of the <see cref="ICredentialAttemptLimiter"/>. <see cref="Subject"/> never holds an email address: emails
/// are pseudonymized (truncated SHA-256), so the subject has a fixed size and can be logged.
/// </summary>
public readonly record struct AttemptKey(AttemptKind Kind, string Subject)
{
    /// <summary>Subject of requests without a known client address (in-process hosts, tests).</summary>
    public const string UnknownAddress = "unknown";

    /// <summary>Key of an email address, normalized by the caller (<c>Actor.NormalizeEmail</c>), whether an account exists or not.</summary>
    public static AttemptKey LoginAccount(string normalizedEmail)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);
        return new AttemptKey(AttemptKind.LoginAccount, Pseudonym(normalizedEmail));
    }

    /// <summary>
    /// Key of a client address. IPv4-mapped IPv6 counts as IPv4; IPv6 counts per /64 prefix, because one client usually
    /// holds a whole /64 and could otherwise switch addresses at will.
    /// </summary>
    public static AttemptKey LoginAddress(IPAddress? address) => new(AttemptKind.LoginAddress, AddressBucket(address));

    public static AttemptKey PasswordChange(Guid actorId) =>
        new(AttemptKind.PasswordChange, actorId.ToString("D", CultureInfo.InvariantCulture));

    /// <summary>Truncated SHA-256 of the normalized email (Base64Url, 22 characters): fixed size, loggable, not the address itself.</summary>
    public static string Pseudonym(string normalizedEmail)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail)).AsSpan(0, 16));
    }

    private static string AddressBucket(IPAddress? address)
    {
        if (address is null)
        {
            return UnknownAddress;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    public override string ToString() => $"{Kind}:{Subject}";
}
