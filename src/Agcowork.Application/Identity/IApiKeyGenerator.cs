namespace Agcowork.Application.Identity;

/// <summary>Creates API key tokens and hashes their secrets. Implemented in Infrastructure.</summary>
public interface IApiKeyGenerator
{
    /// <summary>New cryptographically random token in <c>Agcowork.Domain.Identity.ApiKeyToken</c> format.</summary>
    GeneratedApiKey Generate();

    /// <summary>SHA-256 of the secret part (32 bytes).</summary>
    byte[] HashSecret(string secret);
}

/// <param name="Token">Plain-text key. Return it to the caller once, never store or log it.</param>
/// <param name="Prefix">Visible prefix, stored in clear.</param>
/// <param name="SecretHash">Hash of the secret part, the only thing stored about the secret.</param>
public sealed record GeneratedApiKey(string Token, string Prefix, byte[] SecretHash);
