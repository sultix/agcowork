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

    // Field codes of password changes (AP-022).
    public const string InvalidCredentials = "invalid_credentials";
    public const string Unchanged = "unchanged";
    public const string NotHuman = "not_human";

    // Field codes of schemas and property values (AP-006). Schema rules live in the domain; the codes are shared.
    public const string InvalidType = "invalid_type";
    public const string UnknownProperty = "unknown_property";
    public const string ReadOnly = "read_only";
    public const string Duplicate = Domain.Collections.Schema.SchemaViolationCodes.Duplicate;
    public const string Reserved = Domain.Collections.Schema.SchemaViolationCodes.Reserved;
    public const string TypeChangeNotAllowed = Domain.Collections.Schema.SchemaViolationCodes.TypeChangeNotAllowed;
    public const string KeyImmutable = Domain.Collections.Schema.SchemaViolationCodes.KeyImmutable;
    public const string RemovalNotConfirmed = "removal_not_confirmed";

    /// <summary>A new relation targets an archived collection (or one in an archived space), AP-037.</summary>
    public const string TargetArchived = "target_archived";

    // Field codes of the filter DSL and queries (AP-009).
    public const string UnknownOperator = "unknown_operator";
    public const string OperatorNotApplicable = "operator_not_applicable";
    public const string InvalidValue = "invalid_value";
    public const string InvalidCursor = "invalid_cursor";
    public const string NotSortable = "not_sortable";

    // Field codes of the full-text search (AP-026).
    public const string EmptyQuery = "empty_query";

    // Comments and mentions (AP-029): field codes not_member, not_root; 403 code not_author.
    public const string NotMember = "not_member";
    public const string NotRoot = "not_root";
    public const string NotAuthor = "not_author";
}
