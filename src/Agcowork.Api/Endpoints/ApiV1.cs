using Agcowork.Api.Auth;
using Agcowork.Api.Endpoints.Identity;

namespace Agcowork.Api.Endpoints;

/// <summary>
/// Root of the REST API. Everything under <c>/api/v1</c> requires an authenticated actor unless an
/// endpoint opts out explicitly with <c>AllowAnonymous()</c>.
/// </summary>
internal static class ApiV1
{
    public const string Prefix = "/api/v1";

    public static IEndpointRouteBuilder MapApiV1(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(Prefix)
            .RequireAuthorization(AuthPolicies.Authenticated)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        api.MapAuthEndpoints();
        api.MapMeEndpoints();
        api.MapActorEndpoints();
        api.MapApiKeyEndpoints();

        return app;
    }
}
