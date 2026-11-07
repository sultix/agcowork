using System.Diagnostics.CodeAnalysis;

namespace Agcowork.Application.Common.Results;

/// <summary>
/// Expected failure of a use case. Adapters (REST, MCP) map each subtype to their wire format;
/// see <c>ProblemDetailsMapping</c> in the Api project for REST.
/// </summary>
/// <param name="Code">Machine-readable code (snake_case), stable across releases. Agents branch on it.</param>
/// <param name="Message">Human/agent-readable explanation. Written to the wire except for <see cref="UnauthorizedError"/> and <see cref="NotFoundError"/>.</param>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "C#-only code base; the VB keyword is irrelevant and Error is the clearest name.")]
public abstract record Error(string Code, string Message);

/// <summary>Input is invalid. Carries one entry per offending field so callers can correct themselves.</summary>
public sealed record ValidationError(IReadOnlyList<FieldError> Errors)
    : Error(ErrorCodes.ValidationFailed, "One or more fields are invalid.");

/// <summary>
/// Resource does not exist or is not visible to the caller. Deliberately indistinguishable,
/// so existence is never revealed. <paramref name="Resource"/> is for logs, not for the wire.
/// </summary>
public sealed record NotFoundError(string Resource)
    : Error(ErrorCodes.NotFound, $"{Resource} not found.");

/// <summary>Caller is authenticated but lacks a right. <see cref="Error.Message"/> names the missing right.</summary>
public sealed record ForbiddenError(string Message)
    : Error(ErrorCodes.Forbidden, Message)
{
    public static ForbiddenError MissingScope(string scope) => new($"requires {scope}");
}

/// <summary>
/// No or invalid credentials. <paramref name="Reason"/> is for logs only and never reaches the
/// caller: a 401 must not reveal whether an account or key exists.
/// </summary>
public sealed record UnauthorizedError(string Reason = "Authentication required.")
    : Error(ErrorCodes.Unauthorized, Reason);

/// <summary>
/// Request is valid but clashes with current state (duplicate, invariant). <see cref="Error.Code"/>
/// is specific (e.g. <c>email_taken</c>); <paramref name="Field"/> names the input that caused it, if any.
/// </summary>
public sealed record ConflictError(string Code, string Message, string? Field = null)
    : Error(Code, Message);
