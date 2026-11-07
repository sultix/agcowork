using System.Security.Cryptography;
using System.Text;
using Agcowork.Domain.Identity;
using Agcowork.Infrastructure.Identity;

namespace Agcowork.Application.Tests.Identity;

public sealed class ApiKeyGeneratorTests
{
    private readonly ApiKeyGenerator _generator = new();

    [Fact]
    public void Generate_ProducesDocumentedFormat()
    {
        var generated = _generator.Generate();

        Assert.Matches("^agck_[0-9A-Za-z]{8}_[0-9A-Za-z]{32}\\z", generated.Token);
        Assert.True(ApiKeyToken.TryParse(generated.Token, out var prefix, out var secret));
        Assert.Equal(generated.Prefix, prefix);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), generated.SecretHash);
        Assert.DoesNotContain(secret, Convert.ToHexString(generated.SecretHash), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_ManyTimes_NoCollisions()
    {
        var tokens = Enumerable.Range(0, 1000).Select(_ => _generator.Generate().Token).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void HashSecret_SameInput_SameHash()
    {
        Assert.Equal(_generator.HashSecret("abc"), _generator.HashSecret("abc"));
        Assert.NotEqual(_generator.HashSecret("abc"), _generator.HashSecret("abd"));
    }
}
