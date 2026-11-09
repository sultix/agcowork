using Agcowork.Application.Identity.Throttling;
using Microsoft.Extensions.Options;

namespace Agcowork.Infrastructure.Identity;

/// <summary>
/// Fails the host start with every configuration error of <see cref="CredentialThrottlingOptions"/>, and with
/// <c>Enabled=false</c> unless <paramref name="allowDisabled"/> (Development, i.e. tests).
/// </summary>
internal sealed class CredentialThrottlingValidation(bool allowDisabled) : IValidateOptions<CredentialThrottlingOptions>
{
    public ValidateOptionsResult Validate(string? name, CredentialThrottlingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = options.Validate().ToList();
        if (!options.Enabled && !allowDisabled)
        {
            errors.Add($"{CredentialThrottlingOptions.SectionName}:Enabled=false is only allowed in Development (tests); "
                + "throttling is the brake on password guessing.");
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
