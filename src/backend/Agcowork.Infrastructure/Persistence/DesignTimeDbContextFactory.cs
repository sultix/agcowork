using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agcowork.Infrastructure.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> only. Creating migrations does not open a connection, so a local
/// default is sufficient; override with the <c>AGCOWORK_DESIGN_CONNECTION</c> environment variable.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AgcoworkDbContext>
{
    public AgcoworkDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("AGCOWORK_DESIGN_CONNECTION")
            ?? "Host=localhost;Database=agcowork;Username=postgres;Password=postgres";

        var builder = new DbContextOptionsBuilder<AgcoworkDbContext>();
        AgcoworkDbContextOptions.Configure(builder, connectionString);
        return new AgcoworkDbContext(builder.Options);
    }
}
