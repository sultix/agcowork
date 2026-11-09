using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Agcowork.Application.Common.Results;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Agcowork.Api.Http;

/// <summary>
/// Wire format of errors (RFC 9457 ProblemDetails), binding for every REST endpoint:
/// <c>type</c>, <c>title</c>, <c>status</c>, <c>code</c> (machine-readable), <c>detail</c> (only for
/// 403 and 409), <c>errors[]</c> (400, and 409 when a field caused the conflict) with
/// <c>field</c>, <c>code</c>, <c>message</c> and, for enum-like fields, <c>allowed</c>.
/// 401 and 404 carry no detail, so they reveal nothing about accounts or resources. 429 (<c>rate_limited</c>) carries a
/// fixed detail and <c>retryAfterSeconds</c>; the endpoint also sets <c>Retry-After</c> (<see cref="ToProblem(Error, HttpResponse)"/>).
/// </summary>
public static partial class ProblemDetailsMapping
{
    public const string CodeExtension = "code";
    public const string ErrorsExtension = "errors";
    public const string RetryAfterSecondsExtension = "retryAfterSeconds";

    /// <summary>Endpoint helper: <c>return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();</c></summary>
    public static ProblemHttpResult ToProblem(this Error error) => TypedResults.Problem(ToProblemDetails(error));

    /// <summary>
    /// Like <see cref="ToProblem(Error)"/>, and sets <c>Retry-After</c> (seconds) on <paramref name="response"/> for
    /// <see cref="RateLimitedError"/>. For endpoints whose use case can be rate limited.
    /// </summary>
    public static ProblemHttpResult ToProblem(this Error error, HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (error is RateLimitedError limited)
        {
            response.Headers.RetryAfter = limited.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        return error.ToProblem();
    }

    public static ProblemDetails ToProblemDetails(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var (status, detail, fields) = error switch
        {
            ValidationError validation => (StatusCodes.Status400BadRequest, "One or more fields are invalid. See 'errors'.", validation.Errors),
            UnauthorizedError => (StatusCodes.Status401Unauthorized, null, null),
            ForbiddenError forbidden => (StatusCodes.Status403Forbidden, forbidden.Message, null),
            NotFoundError => (StatusCodes.Status404NotFound, null, null),
            RateLimitedError => (StatusCodes.Status429TooManyRequests, error.Message, null),
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

        if (error is RateLimitedError limited)
        {
            // ASP.NET Core has no defaults for 429; every problem carries type and title.
            problem.Type = "https://tools.ietf.org/html/rfc6585#section-4";
            problem.Title = "Too Many Requests";
            problem.Extensions[RetryAfterSecondsExtension] = limited.RetryAfterSeconds;
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

    /// <summary>
    /// The field error behind a request body that is valid JSON but has a value that cannot be read into its field: the
    /// serializer names it with a JSON path (<c>$.title</c>, <c>$.schema[0].options[1].label</c>). A value of the wrong type,
    /// one of the wrong format (uuid, date-time) and text with an unpaired surrogate (which no .NET string field accepts) look
    /// the same here, so the message names all three. Null for malformed JSON (a reader error) and failures without a path; those stay <c>bad_request</c>.
    /// </summary>
    public static FieldError? BindingFieldError(BadHttpRequestException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.InnerException is not JsonException { Path: { } path } json || json.InnerException is null or JsonException)
        {
            return null;
        }

        var field = FieldOfJsonPath(path);
        return field is null
            ? null
            : new FieldError(
                field,
                ErrorCodes.InvalidValue,
                $"'{field}' could not be read: wrong JSON type, invalid format (e.g. a uuid or date-time) or text with an unpaired "
                + "surrogate (\\ud800-\\udfff), which cannot be stored. See the request schema of this endpoint.");
    }

    /// <summary>Turns a framework 400 into <c>validation_failed</c> with <paramref name="error"/> in <c>errors[]</c>, like <see cref="ToProblemDetails"/>.</summary>
    public static void ApplyBindingFieldError(ProblemDetails problem, FieldError error)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(error);
        problem.Detail = "One or more fields are invalid. See 'errors'.";
        problem.Extensions[CodeExtension] = ErrorCodes.ValidationFailed;
        problem.Extensions[ErrorsExtension] = new List<ProblemFieldError> { ProblemFieldError.From(error) };
    }

    /// <summary><c>$.a.b[0]['c d']</c> → <c>a.b[0].c d</c>; null for the root (<c>$</c>).</summary>
    internal static string? FieldOfJsonPath(string path)
    {
        if (!path.StartsWith('$') || path.Length == 1)
        {
            return null;
        }

        // The serializer quotes member names with special characters as ['name'] without escaping inside.
        var field = QuotedMember().Replace(path[1..], m => "." + m.Groups[1].Value).TrimStart('.');
        return field.Length == 0 ? null : field;
    }

    [GeneratedRegex(@"\['(.*?)'\]")]
    private static partial Regex QuotedMember();

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
        StatusCodes.Status429TooManyRequests => ErrorCodes.RateLimited,
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
