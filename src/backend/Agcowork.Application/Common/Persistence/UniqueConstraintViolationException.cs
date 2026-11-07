using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Common.Persistence;

/// <summary>
/// A save hit a unique index. Thrown by the Infrastructure DbContext in place of the provider's
/// <see cref="DbUpdateException"/>, so the application layer can react without knowing Npgsql.
/// The provider exception stays available as <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class UniqueConstraintViolationException : DbUpdateException
{
    public UniqueConstraintViolationException(string constraintName, DbUpdateException original)
        : base($"Unique constraint '{constraintName}' violated.", original?.InnerException, original?.Entries ?? [])
    {
        ConstraintName = constraintName;
    }

    /// <summary>Database name of the violated index, e.g. <c>ix_actors_email</c> (see <see cref="UniqueConflict"/>).</summary>
    public string ConstraintName { get; }
}
