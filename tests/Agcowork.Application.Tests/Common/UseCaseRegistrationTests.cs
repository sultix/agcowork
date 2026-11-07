using Agcowork.Application.Identity.Actors;
using Agcowork.Application.Identity.ApiKeys;
using Agcowork.Application.Identity.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Agcowork.Application.Tests.Common;

public sealed class UseCaseRegistrationTests
{
    [Fact]
    public void AddApplication_RegistersEveryUseCaseAsScopedConcreteType()
    {
        var services = new ServiceCollection().AddApplication();

        var useCases = DependencyInjection.FindUseCases(typeof(DependencyInjection).Assembly);

        Assert.Superset(
            new HashSet<Type>
            {
                typeof(Login), typeof(AuthenticateSession), typeof(AuthenticateApiKey), typeof(GetMe),
                typeof(CreateActor), typeof(UpdateActor), typeof(GetActor), typeof(ListActors),
                typeof(CreateApiKey), typeof(ListApiKeys), typeof(RevokeApiKey),
            },
            useCases.ToHashSet());
        foreach (var useCase in useCases)
        {
            var descriptor = Assert.Single(services, d => d.ServiceType == useCase);
            Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        }
    }
}
