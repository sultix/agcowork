using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Common.Persistence;

/// <summary>
/// Data access for use cases. Use cases query <see cref="DbSet{TEntity}"/> directly (no repositories)
/// and call <see cref="SaveChangesAsync"/> once per unit of work. Implemented by the EF Core context
/// in Infrastructure.
/// </summary>
public interface IAgcoworkDbContext
{
    DbSet<Actor> Actors { get; }

    DbSet<LocalLogin> LocalLogins { get; }

    DbSet<ApiKey> ApiKeys { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
