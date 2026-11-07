using Agcowork.Application.Collections;
using Agcowork.Application.Collections.Templates;
using Agcowork.Application.Identity.Actors;
using Agcowork.Application.Identity.ApiKeys;
using Agcowork.Application.Identity.Authentication;
using Agcowork.Application.Schema;
using Agcowork.Application.Spaces;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Workspaces;
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
                typeof(ResolveTenant), typeof(ListMyWorkspaces), typeof(CreateWorkspace), typeof(GetWorkspace),
                typeof(UpdateWorkspace), typeof(ArchiveWorkspace), typeof(ListMembers), typeof(AddMember),
                typeof(UpdateMember), typeof(RemoveMember),
                typeof(CreateSpace), typeof(GetSpace), typeof(ListSpaces), typeof(UpdateSpace), typeof(ArchiveSpace),
                typeof(CreateCollection), typeof(GetCollection), typeof(ListCollections), typeof(UpdateCollection), typeof(GetCollectionSchema),
                typeof(UpdateSchema), typeof(DescribeCollection), typeof(ArchiveCollection),
                typeof(CreateTemplate), typeof(UpdateTemplate), typeof(DeleteTemplate), typeof(ListTemplates),
            },
            useCases.ToHashSet());
        foreach (var useCase in useCases)
        {
            var descriptor = Assert.Single(services, d => d.ServiceType == useCase);
            Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        }
    }

    [Fact]
    public void AddApplication_RegistersTenantContextScoped()
    {
        var services = new ServiceCollection().AddApplication();

        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == typeof(ITenantContextAccessor)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == typeof(ITenantContext)).Lifetime);
    }

    [Fact]
    public void AddApplication_RegistersSchemaServicesScoped()
    {
        var services = new ServiceCollection().AddApplication();

        foreach (var type in new[] { typeof(PropertyValueValidator), typeof(IRelationTargetChecker), typeof(IActorMembershipChecker), typeof(ISchemaProvider) })
        {
            Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == type).Lifetime);
        }

        var hook = Assert.Single(services, d => d.ServiceType == typeof(ISchemaChangedHook));
        Assert.Equal((typeof(TemplatePresetSchemaHook), ServiceLifetime.Scoped), (hook.ImplementationType, hook.Lifetime));
    }

    [Fact]
    public void TenantContext_NotSet_ThrowsOnAccess()
    {
        using var provider = new ServiceCollection().AddApplication().BuildServiceProvider();
        using var scope = provider.CreateScope();

        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        Assert.Throws<InvalidOperationException>(() => tenant.WorkspaceId);
    }
}
