using Agcowork.Application.Common;

namespace Agcowork.Application.Tests.Common;

public sealed class MicrosecondTimeProviderTests
{
    [Fact]
    public void GetUtcNow_Always_HasNoSubMicrosecondTicks()
    {
        var provider = new MicrosecondTimeProvider();

        for (var i = 0; i < 1000; i++)
        {
            var now = provider.GetUtcNow();
            Assert.Equal(0, now.UtcTicks % 10);
            Assert.Equal(TimeSpan.Zero, now.Offset);
        }
    }

    [Fact]
    public void GetUtcNow_Always_IsCloseToSystemClock()
    {
        var provider = new MicrosecondTimeProvider();

        var difference = TimeProvider.System.GetUtcNow() - provider.GetUtcNow();

        Assert.InRange(difference, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }
}
