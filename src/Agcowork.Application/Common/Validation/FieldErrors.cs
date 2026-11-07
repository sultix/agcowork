using System.Diagnostics.CodeAnalysis;
using Agcowork.Application.Common.Results;

namespace Agcowork.Application.Common.Validation;

/// <summary>
/// Collects field errors of one request so the caller gets all problems at once, not one per round trip.
/// </summary>
/// <example>
/// <code>
/// var errors = new FieldErrors();
/// if (errors.Required("name", request.Name)) { errors.MaxLength("name", request.Name, Actor.NameMaxLength); }
/// if (errors.Any) { return errors.ToError(); }
/// </code>
/// </example>
public sealed class FieldErrors
{
    private readonly List<FieldError> _errors = [];

    public bool Any => _errors.Count > 0;

    public IReadOnlyList<FieldError> Items => _errors;

    public void Add(string field, string code, string message, IReadOnlyList<string>? allowed = null) =>
        _errors.Add(new FieldError(field, code, message, allowed));

    /// <summary>Adds errors found elsewhere, e.g. by <c>PropertyValueValidator</c>.</summary>
    public void AddRange(IEnumerable<FieldError> errors) => _errors.AddRange(errors);

    /// <summary>Adds <c>required</c> when the value is null or whitespace. Returns true when a value is present.</summary>
    public bool Required(string field, [NotNullWhen(true)] string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        Add(field, ErrorCodes.Required, $"'{field}' is required.");
        return false;
    }

    /// <summary>Adds <c>too_long</c> when the trimmed value exceeds <paramref name="maxLength"/>. Returns true when within bounds.</summary>
    public bool MaxLength(string field, string? value, int maxLength)
    {
        if (value is null || value.Trim().Length <= maxLength)
        {
            return true;
        }

        Add(field, ErrorCodes.TooLong, $"'{field}' must not exceed {maxLength} characters.");
        return false;
    }

    /// <summary>Adds <c>not_in_options</c> with the allowed values when the value is not one of them (ordinal).</summary>
    public bool OneOf(string field, string value, IReadOnlyList<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (allowed.Contains(value, StringComparer.Ordinal))
        {
            return true;
        }

        Add(field, ErrorCodes.NotInOptions, $"'{value}' is not a valid value for '{field}'.", allowed);
        return false;
    }

    /// <summary>Checks every element of an optional list against <paramref name="allowed"/>; errors name the index (<c>field[i]</c>).</summary>
    public bool AllOneOf(string field, IReadOnlyList<string>? values, IReadOnlyList<string> allowed)
    {
        if (values is null)
        {
            return true;
        }

        var valid = true;
        for (var i = 0; i < values.Count; i++)
        {
            valid &= OneOf($"{field}[{i}]", values[i] ?? string.Empty, allowed);
        }

        return valid;
    }

    public ValidationError ToError() => new([.. _errors]);
}
