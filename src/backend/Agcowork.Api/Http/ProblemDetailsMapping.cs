using System.Diagnostics;
using System.Text.Json.Serialization;
using Agcowork.Application.Common.Results;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Agcowork.Api.Http;

/// <summary>
/// Wire format of errors (RFC 9457 ProblemDetails), binding for every REST endpoint:
/// <c>type</c>, <c>title</c>, <c>status</c>, <c>code</c> (machine-readable), <c>detail</c> (only for
/// 403 and 409), <c>errors[]</c> (400, and 409 when a field caused the conflict) with
/// <c>field</c>, <c>code</c>, <c>message</c> and, for enum-like fields, <c>allowed</c>.
/// 401 and 404 carry no detail, so they reveal nothing about accounts or resources.
/// </summary>
public static class ProblemDetailsMapping
{
    public const string CodeExtension = "code";
    public const string ErrorsExtension = "errors";

    /// <summary>Endpoint helper: <c>return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();</c></summary>
    public static ProblemHttpResult ToProblem(this Error error) => TypedResults.Problem(ToProblemDetails(error));

    public static ProblemDetails ToProblemDetails(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var (status, detail, fields) = error switch
        {
            ValidationError validation => (StatusCodes.Status400BadRequest, "One or more fields are invalid. See 'errors'.", validation.Errors),
            UnauthorizedError => (StatusCodes.Status401Unauthorized, null, null),
            ForbiddenError forbidden => (StatusCodes.Status403Forbidden, forbidden.Message, null),
            NotFoundError => (StatusCodes.Status404NotFound, null, null),
            ConflictError conflict => (
                StatusCodes.Status409Conflict,
                conflict.Message,
                conflict.Field is null ? null : (IReadOnlyList<FieldError>)[new FieldError(conflict.Field, ErrorCodes.Conflict, conflict.Message)]),
            _ => throw new UnreachableException($"Unmapped error type {error.GetType().Name}."),
        };

        var problem = new ProblemDetails { Status = status, Detail = detail };
        problem.Extensions[CodeExtension] = error.Code;
        if (fields is not null)
        {
            problem.Extensions[ErrorsExtension] = fields.Select(ProblemFieldError.From).ToList();
        }

        if (error is ConflictError { Details: { } details })
        {
            foreach (var (key, value) in details)
            {
                problem.Extensions.TryAdd(key, value);
            }
        }

        return problem;
    }

    /// <summary>Fallback <c>code</c> for ProblemDetails produced by the framework (status code pages, exception handler).</summary>
    public static string CodeForStatus(int? status) => status switch
    {
        StatusCodes.Status400BadRequest => "bad_request",
        StatusCodes.Status401Unauthorized => ErrorCodes.Unauthorized,
        StatusCodes.Status403Forbidden => ErrorCodes.Forbidden,
        StatusCodes.Status404NotFound => ErrorCodes.NotFound,
        StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
        StatusCodes.Status409Conflict => "conflict",
        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
        >= 500 => "internal_error",
        _ => "error",
    };
}

/// <summary>One entry of <c>errors[]</c> on the wire.</summary>
public sealed record ProblemFieldError(
    string Field,
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Allowed)
{
    public static ProblemFieldError From(FieldError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ProblemFieldError(error.Field, error.Code, error.Message, error.Allowed);
    }
}
