namespace Agcowork.Application.Common.Results;

/// <summary>One invalid input field.</summary>
/// <param name="Field">camelCase path as on the wire, e.g. <c>email</c> or <c>instanceScopes[1]</c>.</param>
/// <param name="Code">One of <see cref="ErrorCodes"/> field codes.</param>
/// <param name="Message">Explanation an agent can act on.</param>
/// <param name="Allowed">For enum-like fields: the accepted values.</param>
public sealed record FieldError(string Field, string Code, string Message, IReadOnlyList<string>? Allowed = null);

public static class FieldErrorExtensions
{
    /// <summary>Wraps a single field error, for checks that only become possible after loading state.</summary>
    public static ValidationError ToValidationError(this FieldError error) => new([error]);
}
