namespace Agcowork.Infrastructure.Identity;

/// <summary>
/// Initial administrator, created on startup when the installation has no actors yet
/// (configuration section <c>Agcowork:Bootstrap</c>). Supply the password via environment or
/// user secrets outside development.
/// </summary>
public sealed class BootstrapOptions
{
    public const string SectionName = "Agcowork:Bootstrap";

    public string? AdminEmail { get; set; }

    public string? AdminPassword { get; set; }

    public string AdminName { get; set; } = "Administrator";
}
