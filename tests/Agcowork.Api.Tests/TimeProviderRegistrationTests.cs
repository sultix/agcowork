using Agcowork.Application.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Agcowork.Api.Tests;

/// <summary>
/// ASP.NET Core registers its own <see cref="TimeProvider"/>; the application must still win so that every
/// timestamp is truncated to the microseconds PostgreSQL stores (otherwise a POST response and the GET read
/// back from the database differ on Linux).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class TimeProviderRegistrationTests(AgcoworkApiFactory factory)
{
    [Fact]
    public void Host_Always_ResolvesMicrosecondTimeProvider()
    {
        var provider = factory.Services.GetRequiredService<TimeProvider>();

        Assert.IsType<MicrosecondTimeProvider>(provider);
    }
}
