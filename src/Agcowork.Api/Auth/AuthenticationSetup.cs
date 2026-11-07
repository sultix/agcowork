using Agcowork.Application.Common.Security;
using Agcowork.Application.Identity.Authentication;
using Agcowork.Domain.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace Agcowork.Api.Auth;

/// <summary>
/// Auth pipeline (architecture §5): bearer API key → agent, cookie → human, both into the same
/// claims (<see cref="ActorClaims"/>) and the same policies.
/// </summary>
/// <remarks>
/// No anti-CSRF token: the cookie is SameSite=Lax (not sent on cross-site POST/PATCH/DELETE),
/// state-changing endpoints accept JSON only (a cross-origin JSON request needs a CORS preflight,
/// and no CORS is configured), and GET endpoints have no side effects. Revisit if CORS is opened
/// or if sibling subdomains become untrusted (SameSite treats them as same-site).
/// </remarks>
internal static class AuthenticationSetup
{
    public const string CookieName = "agcowork_session";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

    public static IServiceCollection AddAgcoworkAuthentication(this IServiceCollection services, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentActor, HttpCurrentActor>();

        services.AddAuthentication(AuthSchemes.Default)
            .AddPolicyScheme(AuthSchemes.Default, "Bearer API key or session cookie", options =>
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? AuthSchemes.ApiKey
                        : AuthSchemes.Cookie)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthSchemes.ApiKey, displayName: "API key", _ => { })
            .AddCookie(AuthSchemes.Cookie, options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.IsEssential = true;
                // Secure everywhere except Development (local http and the test server). Deliberately not
                // derived from the Host header, which the client controls.
                options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                options.ExpireTimeSpan = SessionLifetime;
                options.SlidingExpiration = true;
                options.Events = new CookieAuthenticationEvents
                {
                    // An API answers with status codes, never with redirects to a login page.
                    OnRedirectToLogin = context => SetStatus(context.Response, StatusCodes.Status401Unauthorized),
                    OnRedirectToAccessDenied = context => SetStatus(context.Response, StatusCodes.Status403Forbidden),
                    OnValidatePrincipal = ValidateSessionAsync,
                };
            });

        // InstanceScopeRequirement handles itself (ASP.NET's pass-through handler invokes it).
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemDetailsAuthorizationResultHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser().RequireClaim(ActorClaims.ActorId))
            // Only the API key scheme is evaluated, so a session cookie never authenticates here (401).
            .AddPolicy(AuthPolicies.Agent, policy => policy
                .AddAuthenticationSchemes(AuthSchemes.ApiKey)
                .RequireAuthenticatedUser().RequireClaim(ActorClaims.ActorId))
            .AddPolicy(AuthPolicies.InstanceAdmin, policy => policy
                .RequireAuthenticatedUser().RequireClaim(ActorClaims.ActorId)
                .AddRequirements(new InstanceScopeRequirement(InstanceScope.Admin)))
            .AddPolicy(AuthPolicies.WorkspacesCreate, policy => policy
                .RequireAuthenticatedUser().RequireClaim(ActorClaims.ActorId)
                .AddRequirements(new InstanceScopeRequirement(InstanceScope.WorkspacesCreate)));

        // Rate limiting per key/IP (architecture §7) follows in a later package.
        return services;
    }

    /// <summary>
    /// Re-checks the session actor on every request so deactivation and scope changes apply
    /// immediately instead of at cookie expiry.
    /// </summary>
    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        if (!Guid.TryParse(context.Principal?.FindFirst(ActorClaims.ActorId)?.Value, out var actorId))
        {
            await RejectAsync(context);
            return;
        }

        var useCase = context.HttpContext.RequestServices.GetRequiredService<AuthenticateSession>();
        var result = await useCase.HandleAsync(new AuthenticateSessionRequest(actorId), context.HttpContext.RequestAborted);
        if (result.IsFailure)
        {
            await RejectAsync(context);
            return;
        }

        context.ReplacePrincipal(ActorClaims.ToPrincipal(result.Value, AuthSchemes.Cookie));
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(AuthSchemes.Cookie);
    }

    private static Task SetStatus(HttpResponse response, int statusCode)
    {
        response.StatusCode = statusCode;
        return Task.CompletedTask;
    }
}
