using Agcowork.Application.Identity;
using Agcowork.Domain.Identity;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agcowork.Infrastructure.Identity;

/// <summary>
/// Creates the first human actor with <c>instance:admin</c> if no actor exists, flagged to change the configured
/// password. On later starts it flags the bootstrap admin again while its password still matches the configured one
/// (covers installations from before the flag existed). Runs once at startup after migrations. Never logs the password.
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
        var settings = options.Value;
        if (await db.Actors.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            await FlagUnchangedBootstrapPasswordAsync(settings, cancellationToken).ConfigureAwait(false);
            return;
        }

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
        db.LocalLogins.Add(LocalLogin.Create(admin, passwordHasher.Hash(settings.AdminPassword), timeProvider, mustChangePassword: true));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogCreated(logger, admin.Email!, admin.Id);
    }

    /// <summary>
    /// Sets <see cref="LocalLogin.MustChangePassword"/> on the configured admin while its hash still verifies against the
    /// configured password: a password that sits in configuration should not stay valid for good. Leaves sessions alone.
    /// </summary>
    private async Task FlagUnchangedBootstrapPasswordAsync(BootstrapOptions settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.AdminEmail) || string.IsNullOrEmpty(settings.AdminPassword))
        {
            return;
        }

        var email = Actor.NormalizeEmail(settings.AdminEmail);
        var login = await (
                from actor in db.Actors
                join candidate in db.LocalLogins on actor.Id equals candidate.ActorId
                where actor.Email == email && actor.Kind == ActorKind.Human
                select candidate)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (login is null || login.MustChangePassword)
        {
            return;
        }

        if (passwordHasher.Verify(login.PasswordHash, settings.AdminPassword) == PasswordVerification.Failed)
        {
            return;
        }

        login.RequirePasswordChange();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogStillBootstrapPassword(logger, login.ActorId);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No actors exist and no bootstrap admin is configured ({Section}:AdminEmail / AdminPassword). Nobody can log in until one is configured.")]
    private static partial void LogMissingConfiguration(ILogger logger, string section);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bootstrap admin password in {Section}:AdminPassword is shorter than {MinLength} characters; no admin created.")]
    private static partial void LogPasswordTooShort(ILogger logger, string section, int minLength);

    [LoggerMessage(Level = LogLevel.Error, Message = "Bootstrap admin email in {Section}:AdminEmail is not a valid address; no admin created.")]
    private static partial void LogInvalidEmail(ILogger logger, string section);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bootstrap admin {ActorId} still uses the configured bootstrap password; flagged to change it after the next login.")]
    private static partial void LogStillBootstrapPassword(ILogger logger, Guid actorId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created bootstrap admin {Email} ({ActorId}).")]
    private static partial void LogCreated(ILogger logger, string email, Guid actorId);
}
