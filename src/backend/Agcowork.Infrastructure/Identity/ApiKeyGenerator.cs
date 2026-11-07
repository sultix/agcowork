using System.Security.Cryptography;
using System.Text;
using Agcowork.Application.Identity;
using Agcowork.Domain.Identity;

namespace Agcowork.Infrastructure.Identity;

/// <summary>
/// Generates <c>agck_&lt;prefix8&gt;_&lt;secret32&gt;</c> tokens from a CSPRNG (Base62, ~190 bits of secret)
/// and hashes secrets with SHA-256. A fast hash is appropriate: the secret is high-entropy, not a password.
/// </summary>
internal sealed class ApiKeyGenerator : IApiKeyGenerator
{
    public GeneratedApiKey Generate()
    {
        var prefix = RandomNumberGenerator.GetString(ApiKeyToken.Base62Alphabet, ApiKeyToken.PrefixLength);
        var secret = RandomNumberGenerator.GetString(ApiKeyToken.Base62Alphabet, ApiKeyToken.SecretLength);
        return new GeneratedApiKey(ApiKeyToken.Format(prefix, secret), prefix, HashSecret(secret));
    }

    public byte[] HashSecret(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }
}
