using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Domain.Workspaces;

namespace Agcowork.Application.Workspaces;

/// <summary>Field rules of the workspace module. Mirrors the guards in <see cref="Workspace"/> and <see cref="Membership"/>.</summary>
internal static class WorkspaceInputValidation
{
    /// <summary>Wire names of <see cref="WorkspaceRole"/>.</summary>
    public static IReadOnlyList<string> Roles { get; } = ["owner", "admin", "editor", "viewer"];

    public static void Slug(FieldErrors errors, string? slug)
    {
        if (!errors.Required("slug", slug))
        {
            return;
        }

        if (slug.Length > Workspace.SlugMaxLength)
        {
            errors.Add("slug", ErrorCodes.TooLong, $"'slug' must not exceed {Workspace.SlugMaxLength} characters.");
        }
        else if (!Workspace.IsValidSlug(slug))
        {
            errors.Add("slug", ErrorCodes.InvalidFormat, "'slug' may contain lowercase letters, digits and single hyphens, and must not start or end with a hyphen, e.g. 'project-alpha'.");
        }
    }

    public static void Name(FieldErrors errors, string? name)
    {
        if (errors.Required("name", name) && errors.Storable("name", name))
        {
            errors.MaxLength("name", name, Workspace.NameMaxLength);
        }
    }

    public static void Description(FieldErrors errors, string? description)
    {
        if (errors.Storable("description", description))
        {
            errors.MaxLength("description", description, Workspace.DescriptionMaxLength);
        }
    }

    /// <summary>Parses a wire role ("owner", …). Adds <c>not_in_options</c> with the allowed values when unknown.</summary>
    public static WorkspaceRole? Role(FieldErrors errors, string? role, bool required)
    {
        if (role is null)
        {
            if (required)
            {
                errors.Required("role", role);
            }

            return null;
        }

        WorkspaceRole? parsed = role switch
        {
            "owner" => WorkspaceRole.Owner,
            "admin" => WorkspaceRole.Admin,
            "editor" => WorkspaceRole.Editor,
            "viewer" => WorkspaceRole.Viewer,
            _ => null,
        };
        if (parsed is null)
        {
            errors.OneOf("role", role, Roles);
        }

        return parsed;
    }

    /// <summary>Optional narrowing of a membership: a non-empty subset of the role's default scopes.</summary>
    public static void Scopes(FieldErrors errors, IReadOnlyList<string>? scopes, WorkspaceRole? role)
    {
        if (scopes is null)
        {
            return;
        }

        if (scopes.Count == 0)
        {
            errors.Add("scopes", ErrorCodes.Required, "'scopes' must not be empty; omit it to grant the role's default scopes.");
            return;
        }

        var allowed = role is { } r ? WorkspaceScope.ForRole(r) : WorkspaceScope.All;
        errors.AllOneOf("scopes", scopes, allowed);
    }
}
