using Agcowork.Domain.Identity;

namespace Agcowork.Domain.Tests.Identity;

public sealed class InstanceScopeTests
{
    [Fact]
    public void Grants_AdminImpliesWorkspacesCreate()
    {
        Assert.True(InstanceScope.Grants([InstanceScope.Admin], InstanceScope.WorkspacesCreate));
    }

    [Fact]
    public void Grants_WorkspacesCreateDoesNotImplyAdmin()
    {
        Assert.False(InstanceScope.Grants([InstanceScope.WorkspacesCreate], InstanceScope.Admin));
        Assert.True(InstanceScope.Grants([InstanceScope.WorkspacesCreate], InstanceScope.WorkspacesCreate));
    }

    [Fact]
    public void Grants_NoScopes_GrantsNothing()
    {
        Assert.False(InstanceScope.Grants([], InstanceScope.WorkspacesCreate));
        Assert.False(InstanceScope.Grants([], InstanceScope.Admin));
    }
}
