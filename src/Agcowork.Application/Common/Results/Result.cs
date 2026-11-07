using System.Diagnostics.CodeAnalysis;

namespace Agcowork.Application.Common.Results;

/// <summary>
/// Outcome of a use case without a value. Use cases return <see cref="Error"/> subtypes for
/// expected failures (implicit conversion) and throw only for programming errors.
/// </summary>
public sealed class Result
{
    private static readonly Result SuccessInstance = new(null);

    private Result(Error? error) => Error = error;

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => !IsSuccess;

    public Error? Error { get; }

    public static Result Success() => SuccessInstance;

    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    public static Result<T> Failure<T>(Error error) => Result<T>.Failure(error);

    public static implicit operator Result(Error error) => Failure(error);

    public TOut Match<TOut>(Func<TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess() : onFailure(Error);
    }
}

/// <summary>
/// Outcome of a use case with a value. <c>return value;</c> and <c>return new NotFoundError(…);</c>
/// both convert implicitly.
/// </summary>
/// <remarks>
/// C# does not apply user-defined conversions from interface types, so the implicit conversion from
/// the value is unreliable when <typeparamref name="T"/> is an interface (e.g. <c>IReadOnlyList&lt;X&gt;</c>).
/// Write <c>Result.Success&lt;IReadOnlyList&lt;X&gt;&gt;([.. items])</c> for list results.
/// </remarks>
public sealed class Result<T>
{
    private readonly T? _value;

    private Result(T value)
    {
        _value = value;
    }

    private Result(Error error)
    {
        Error = error;
    }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => !IsSuccess;

    public Error? Error { get; }

    /// <summary>The value. Throws when accessed on a failure: check <see cref="IsSuccess"/> first.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result is a failure ({Error.Code}); it has no value.");

    // Internal: the public factories are Result.Success(value) / Result.Failure<T>(error) (CA1000).
    internal static Result<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Result<T>(value);
    }

    internal static Result<T> Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(error);
    }

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(_value!) : onFailure(Error);
    }
}
