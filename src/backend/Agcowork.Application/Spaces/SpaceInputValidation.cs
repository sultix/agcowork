using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Domain.Spaces;

namespace Agcowork.Application.Spaces;

/// <summary>Field rules of the space module. Mirrors the guards in <see cref="Space"/>.</summary>
internal static class SpaceInputValidation
{
    public static void Name(FieldErrors errors, string? name)
    {
        if (errors.Required("name", name) && errors.Storable("name", name))
        {
            errors.MaxLength("name", name, Space.NameMaxLength);
        }
    }

    public static void Description(FieldErrors errors, string? description)
    {
        if (errors.Storable("description", description))
        {
            errors.MaxLength("description", description, Space.DescriptionMaxLength);
        }
    }

    public static void Icon(FieldErrors errors, string? icon)
    {
        if (errors.Storable("icon", icon))
        {
            errors.MaxLength("icon", icon, Space.IconMaxLength);
        }
    }
}

/// <summary>Conflicts of the space module. Codes are part of the API contract.</summary>
public static class SpaceConflicts
{
    /// <summary>Changing an archived space or adding collections to it.</summary>
    public static ConflictError Archived(string? field = null) =>
        new("space_archived", "The space is archived and read-only.", field);
}
