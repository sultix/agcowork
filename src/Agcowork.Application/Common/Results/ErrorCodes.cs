namespace Agcowork.Application.Common.Results;

/// <summary>Machine-readable codes. Part of the public API contract: add, never rename.</summary>
public static class ErrorCodes
{
    // Top-level codes (Error.Code).
    public const string ValidationFailed = "validation_failed";
    public const string NotFound = "not_found";
    public const string Forbidden = "forbidden";
    public const string Unauthorized = "unauthorized";

    // Field codes (FieldError.Code).
    public const string Required = "required";
    public const string InvalidFormat = "invalid_format";
    public const string NotInOptions = "not_in_options";
    public const string TooLong = "too_long";
    public const string TooShort = "too_short";
    public const string OutOfRange = "out_of_range";
    public const string NotAllowed = "not_allowed";
    public const string Conflict = "conflict";
    public const string ActorInactive = "actor_inactive";
}
