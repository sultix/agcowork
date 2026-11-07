namespace Agcowork.Domain.Collections.Schema;

/// <summary>
/// A broken schema rule, structured so the application layer can report it as a field error without
/// re-implementing the rule. <see cref="Path"/> is relative (<c>key</c>, <c>options[1].value</c>,
/// <c>[2].key</c>); <see cref="Prefixed"/> anchors it in the request (<c>schema[2].key</c>).
/// </summary>
/// <param name="Path">camelCase path relative to the checked object.</param>
/// <param name="Code">One of <see cref="SchemaViolationCodes"/>.</param>
/// <param name="Message">Explanation an agent can act on.</param>
/// <param name="Allowed">Accepted values, where there is a closed set.</param>
public sealed record SchemaViolation(string Path, string Code, string Message, IReadOnlyList<string>? Allowed = null)
{
    /// <summary>Same violation with <paramref name="prefix"/> in front of the path (<c>schema</c> + <c>[2].key</c> → <c>schema[2].key</c>).</summary>
    public SchemaViolation Prefixed(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (prefix.Length == 0)
        {
            return this;
        }

        if (Path.Length == 0)
        {
            return this with { Path = prefix };
        }

        return this with { Path = Path.StartsWith('[') ? prefix + Path : $"{prefix}.{Path}" };
    }
}

/// <summary>Codes of schema violations. Identical to the application's field error codes of the same name.</summary>
public static class SchemaViolationCodes
{
    public const string Required = "required";
    public const string InvalidFormat = "invalid_format";
    public const string TooLong = "too_long";
    public const string OutOfRange = "out_of_range";
    public const string NotAllowed = "not_allowed";
    public const string NotInOptions = "not_in_options";
    public const string Duplicate = "duplicate";
    public const string Reserved = "reserved";
    public const string TypeChangeNotAllowed = "type_change_not_allowed";
    public const string KeyImmutable = "key_immutable";
}
