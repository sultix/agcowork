using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;

namespace Agcowork.Application.Tests.Common;

public sealed class ResultTests
{
    [Fact]
    public void ImplicitFromValue_IsSuccessWithValue()
    {
        Result<int> result = 42;

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Null(result.Error);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void ImplicitFromError_IsFailureWithoutValue()
    {
        Result<int> result = new NotFoundError("actor");

        Assert.True(result.IsFailure);
        var error = Assert.IsType<NotFoundError>(result.Error);
        Assert.Equal(ErrorCodes.NotFound, error.Code);
        var exception = Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Contains("not_found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Match_Success_CallsSuccessBranch()
    {
        var result = Result.Success("value");

        Assert.Equal("ok:value", result.Match(value => $"ok:{value}", error => $"error:{error.Code}"));
    }

    [Fact]
    public void Match_Failure_CallsFailureBranch()
    {
        var result = Result.Failure<string>(new UnauthorizedError());

        Assert.Equal("error:unauthorized", result.Match(value => $"ok:{value}", error => $"error:{error.Code}"));
    }

    [Fact]
    public void NonGenericResult_SuccessAndFailure_Behave()
    {
        var success = Result.Success();
        Result failure = new ConflictError("email_taken", "taken", "email");

        Assert.True(success.IsSuccess);
        Assert.True(failure.IsFailure);
        Assert.Equal("email_taken", failure.Error.Code);
        Assert.Equal(1, failure.Match(() => 0, _ => 1));
    }

    [Fact]
    public void ForbiddenMissingScope_NamesScope()
    {
        var error = ForbiddenError.MissingScope("instance:admin");

        Assert.Equal("requires instance:admin", error.Message);
        Assert.Equal(ErrorCodes.Forbidden, error.Code);
    }

    [Fact]
    public void FieldErrors_CollectsAllProblemsInOrder()
    {
        var errors = new FieldErrors();

        Assert.False(errors.Required("name", "  "));
        Assert.False(errors.MaxLength("role", new string('r', 11), 10));
        Assert.True(errors.MaxLength("role", "  short  ", 5));
        Assert.False(errors.OneOf("kind", "robot", ["human", "agent"]));
        Assert.False(errors.AllOneOf("scopes", ["read", "delete"], ["read", "write"]));

        var validation = errors.ToError();
        Assert.Equal(ErrorCodes.ValidationFailed, validation.Code);
        Assert.Collection(
            validation.Errors,
            e => Assert.Equal(("name", ErrorCodes.Required), (e.Field, e.Code)),
            e => Assert.Equal(("role", ErrorCodes.TooLong), (e.Field, e.Code)),
            e =>
            {
                Assert.Equal(("kind", ErrorCodes.NotInOptions), (e.Field, e.Code));
                Assert.Equal(["human", "agent"], e.Allowed);
            },
            e => Assert.Equal(("scopes[1]", ErrorCodes.NotInOptions), (e.Field, e.Code)));
    }

    [Fact]
    public void FieldErrors_NoProblems_HasNone()
    {
        var errors = new FieldErrors();

        Assert.True(errors.Required("name", "Ada"));
        Assert.True(errors.AllOneOf("scopes", null, ["read"]));
        Assert.False(errors.Any);
    }
}
