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

    // Field codes of schemas and property values (AP-006). Schema rules live in the domain; the codes are shared.
    public const string InvalidType = "invalid_type";
    public const string UnknownProperty = "unknown_property";
    public const string ReadOnly = "read_only";
    public const string Duplicate = Domain.Collections.Schema.SchemaViolationCodes.Duplicate;
    public const string Reserved = Domain.Collections.Schema.SchemaViolationCodes.Reserved;
    public const string TypeChangeNotAllowed = Domain.Collections.Schema.SchemaViolationCodes.TypeChangeNotAllowed;
    public const string KeyImmutable = Domain.Collections.Schema.SchemaViolationCodes.KeyImmutable;
    public const string RemovalNotConfirmed = "removal_not_confirmed";

    // Field codes of the filter DSL and queries (AP-009).
    public const string UnknownOperator = "unknown_operator";
    public const string OperatorNotApplicable = "operator_not_applicable";
    public const string InvalidValue = "invalid_value";
    public const string InvalidCursor = "invalid_cursor";
    public const string NotSortable = "not_sortable";
}
