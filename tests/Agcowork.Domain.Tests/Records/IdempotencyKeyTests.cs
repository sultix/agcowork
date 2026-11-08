using Agcowork.Domain.Records;

namespace Agcowork.Domain.Tests.Records;

public sealed class IdempotencyKeyTests
{
    private static readonly string Hash = new('a', IdempotencyKey.RequestHashLength);

    [Fact]
    public void Matches_SameHash_True_OtherHash_False()
    {
        var key = IdempotencyKey.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "k", Guid.CreateVersion7(), Hash, TimeProvider.System);

        Assert.Equal(Hash, key.RequestHash);
        Assert.True(key.Matches(Hash));
        Assert.False(key.Matches(new string('b', IdempotencyKey.RequestHashLength)));
    }

    [Fact]
    public void Create_HashMissingOrWrongLength_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => IdempotencyKey.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "k", Guid.CreateVersion7(), "", TimeProvider.System));
        Assert.ThrowsAny<ArgumentException>(() => IdempotencyKey.ForComment(Guid.CreateVersion7(), Guid.CreateVersion7(), "k", Guid.CreateVersion7(), Guid.CreateVersion7(), "abc", TimeProvider.System));
    }
}
