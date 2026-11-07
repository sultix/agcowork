using System.Text.Encodings.Web;
using Agcowork.Application.Identity.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Agcowork.Api.Auth;

/// <summary>
/// Authenticates <c>Authorization: Bearer agck_…</c> through <see cref="AuthenticateApiKey"/>.
/// The token is never logged; failure reasons are logged at debug level.
/// </summary>
internal sealed partial class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (header is null || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var useCase = Context.RequestServices.GetRequiredService<AuthenticateApiKey>();
        var result = await useCase.HandleAsync(new AuthenticateApiKeyRequest(header[BearerPrefix.Length..].Trim()), Context.RequestAborted);
        if (result.IsFailure)
        {
            LogRejected(Logger, result.Error.Message);
            return AuthenticateResult.Fail("Invalid API key.");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(ActorClaims.ToPrincipal(result.Value, Scheme.Name), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "API key rejected: {Reason}")]
    private static partial void LogRejected(ILogger logger, string reason);
}
