using Agcowork.Application.Common.Results;

namespace Agcowork.Application.Common;

/// <summary>
/// One application operation, shared by REST, MCP and SignalR adapters. Implementations are
/// sealed classes named after the operation (e.g. <c>CreateActor</c>), receive dependencies via
/// the primary constructor, authorize the caller themselves and are registered automatically by
/// <see cref="DependencyInjection.AddApplication"/>.
/// </summary>
public interface IUseCase<in TRequest, TResponse>
{
    Task<Result<TResponse>> HandleAsync(TRequest request, CancellationToken cancellationToken);
}

/// <summary>Use case without a response value.</summary>
public interface IUseCase<in TRequest>
{
    Task<Result> HandleAsync(TRequest request, CancellationToken cancellationToken);
}
