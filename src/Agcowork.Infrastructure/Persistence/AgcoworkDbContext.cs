using Agcowork.Application.Common.Persistence;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agcowork.Infrastructure.Persistence;

public sealed class AgcoworkDbContext(DbContextOptions<AgcoworkDbContext> options) : DbContext(options), IAgcoworkDbContext
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<Actor> Actors => Set<Actor>();

    public DbSet<LocalLogin> LocalLogins => Set<LocalLogin>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    /// <summary>Translates unique violations into <see cref="UniqueConstraintViolationException"/> (see <see cref="UniqueConflict"/>).</summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: { } constraintName,
        })
        {
            throw new UniqueConstraintViolationException(constraintName, exception);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgcoworkDbContext).Assembly);
    }
}
