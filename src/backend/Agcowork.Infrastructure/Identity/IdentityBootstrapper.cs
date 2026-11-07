using Agcowork.Application.Identity;
using Agcowork.Domain.Identity;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agcowork.Infrastructure.Identity;

/// <summary>
/// Creates the first human actor with <c>instance:admin</c> if no actor exists. Runs once at startup
/// after migrations. Never logs the password.
/// </summary>
internal sealed partial class IdentityBootstrapper(
    AgcoworkDbContext db,
    IPasswordHasher passwordHasher,
    TimeProvider timeProvider,
    IOptions<BootstrapOptions> options,
    ILogger<IdentityBootstrapper> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (await db.Actors.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.AdminEmail) || string.IsNullOrEmpty(settings.AdminPassword))
        {
            LogMissingConfiguration(logger, BootstrapOptions.SectionName);
            return;
        }

        if (settings.AdminPassword.Length < PasswordPolicy.MinLength)
        {
            LogPasswordTooShort(logger, BootstrapOptions.SectionName, PasswordPolicy.MinLength);
            return;
        }

        if (!Actor.IsValidEmail(settings.AdminEmail))
        {
            LogInvalidEmail(logger, BootstrapOptions.SectionName);
            return;
        }

        var admin = Actor.CreateHuman(settings.AdminName, settings.AdminEmail, role: null, [InstanceScope.Admin], timeProvider);
        db.Actors.Add(admin);
        db.LocalLogins.Add(LocalLogin.Create(admin, passwordHasher.Hash(settings.AdminPassword), timeProvider));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogCreated(logger, admin.Email!, admin.Id);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No actors exist and no bootstrap admin is configured ({Section}:AdminEmail / AdminPassword). Nobody can log in until one is configured.")]
    private static partial void LogMissingConfiguration(ILogger logger, string section);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bootstrap admin password in {Section}:AdminPassword is shorter than {MinLength} characters; no admin created.")]
    private static partial void LogPasswordTooShort(ILogger logger, string section, int minLength);

    [LoggerMessage(Level = LogLevel.Error, Message = "Bootstrap admin email in {Section}:AdminEmail is not a valid address; no admin created.")]
    private static partial void LogInvalidEmail(ILogger logger, string section);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created bootstrap admin {Email} ({ActorId}).")]
    private static partial void LogCreated(ILogger logger, string email, Guid actorId);
}
