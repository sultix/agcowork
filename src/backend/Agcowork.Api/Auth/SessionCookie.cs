using System.Globalization;
using Agcowork.Application.Identity.Authentication;
using Microsoft.AspNetCore.Authentication;

namespace Agcowork.Api.Auth;

/// <summary>
/// Issues the session cookie of a human, bound to the password it was issued for, and reads that binding back for
/// <see cref="AuthenticateSession"/>.
/// </summary>
/// <remarks>
/// The ticket carries the credential stamp (<see cref="AuthenticatedActor.PasswordStamp"/>, the <c>LocalLogin.UpdatedAt</c>
/// read with the verified hash) in round-trip format. The issue time would not do: a login that verified the old hash
/// while a password change committed gets a cookie issued after the change, and
/// <see cref="AuthenticationProperties.IssuedUtc"/> keeps only whole seconds (RFC 1123). The cookie handler encrypts and
/// signs the ticket, so the client cannot change the stamp. It is kept in the properties, not the claims, because the
/// principal is replaced on every request and sliding renewal reuses the properties.
/// </remarks>
internal static class SessionCookie
{
    public const string PasswordStampKey = "agcowork.password_at";

    /// <summary>Signs in <paramref name="actor"/> (from login or own password change) with a persistent session cookie.</summary>
    public static Task SignInAsync(HttpContext httpContext, AuthenticatedActor actor)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(actor);
        var stamp = actor.PasswordStamp ?? throw new InvalidOperationException("A session needs the password stamp of its login.");
        var properties = new AuthenticationProperties { IsPersistent = true };
        properties.Items[PasswordStampKey] = stamp.ToString("O", CultureInfo.InvariantCulture);
        return httpContext.SignInAsync(AuthSchemes.Cookie, ActorClaims.ToPrincipal(actor, AuthSchemes.Cookie), properties);
    }

    /// <summary>The credential stamp of the cookie; null for cookies issued before stamps existed.</summary>
    public static DateTimeOffset? PasswordStamp(AuthenticationProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return properties.Items.TryGetValue(PasswordStampKey, out var value)
            && DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stamp)
                ? stamp
                : null;
    }
}
