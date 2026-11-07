using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agcowork.Api.Tests.Identity;

[Collection(ApiTestGroup.Name)]
public sealed class CookiePolicyTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Login_SameAsRequestOverHttp_CookieHasNoSecureFlag()
    {
        using var client = factory.WithWebHostBuilder(b => b.UseSetting("Agcowork:Cookies:SecurePolicy", "SameAsRequest"))
            .CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var cookie = await LoginCookieAsync(client);

        Assert.DoesNotContain("; secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_AlwaysOverHttp_CookieHasSecureFlag()
    {
        using var client = factory.WithWebHostBuilder(b => b.UseSetting("Agcowork:Cookies:SecurePolicy", "Always"))
            .CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var cookie = await LoginCookieAsync(client);

        Assert.Contains("; secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_ProductionWithoutOption_CookieHasSecureFlag()
    {
        using var client = factory.WithWebHostBuilder(b => b.UseEnvironment("Production")
                .UseSetting("Agcowork:Bootstrap:AdminEmail", factory.AdminEmail)
                .UseSetting("Agcowork:Bootstrap:AdminPassword", factory.AdminPassword))
            .CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var cookie = await LoginCookieAsync(client);

        Assert.Contains("; secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("nonsense")]
    public void Startup_InvalidSecurePolicy_FailsToStart(string value)
    {
        using var invalid = factory.WithWebHostBuilder(b => b.UseSetting("Agcowork:Cookies:SecurePolicy", value));

        Assert.ThrowsAny<Exception>(() => invalid.CreateClient());
    }

    private async Task<string> LoginCookieAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = factory.AdminEmail, password = factory.AdminPassword }, Ct);
        Assert.True(response.IsSuccessStatusCode, $"Login failed: {response.StatusCode}");
        return Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("agcowork_session=", StringComparison.Ordinal));
    }
}
