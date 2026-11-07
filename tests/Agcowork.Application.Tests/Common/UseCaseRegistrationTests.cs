using Agcowork.Application.Activity;
using Agcowork.Application.Collections;
using Agcowork.Application.Collections.Templates;
using Agcowork.Application.Identity.Actors;
using Agcowork.Application.Identity.ApiKeys;
using Agcowork.Application.Identity.Authentication;
using Agcowork.Application.Identity.Passwords;
using Agcowork.Application.Records;
using Agcowork.Application.Schema;
using Agcowork.Application.Spaces;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Views;
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
                typeof(ChangeOwnPassword), typeof(ResetPassword),
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

        var hooks = services.Where(d => d.ServiceType == typeof(ISchemaChangedHook)).ToList();
        Assert.Equal([typeof(TemplatePresetSchemaHook), typeof(RecordValuesSchemaHook), typeof(ViewSchemaHook)], hooks.Select(h => h.ImplementationType));
        Assert.All(hooks, h => Assert.Equal(ServiceLifetime.Scoped, h.Lifetime));
        var created = Assert.Single(services, d => d.ServiceType == typeof(ICollectionCreatedHook));
        Assert.Equal((typeof(DefaultViewsHook), ServiceLifetime.Scoped), (created.ImplementationType, created.Lifetime));
        Assert.Equal(
            "Agcowork.Application.Schema.RecordRelationTargetChecker",
            Assert.Single(services, d => d.ServiceType == typeof(IRelationTargetChecker)).ImplementationType?.FullName);
    }

    [Fact]
    public void AddApplication_RegistersActivityRecorderScoped()
    {
        var services = new ServiceCollection().AddApplication();

        var recorder = Assert.Single(services, d => d.ServiceType == typeof(IActivityRecorder));
        Assert.Equal((typeof(ActivityRecorder), ServiceLifetime.Scoped), (recorder.ImplementationType, recorder.Lifetime));
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
