using Agcowork.Application.Common.Results;

namespace Agcowork.Application.Common.Persistence;

/// <summary>
/// Maps a unique index (by its database name) to the <see cref="ConflictError"/> a caller should get.
/// </summary>
/// <remarks>
/// Pattern for every unique field (emails, slugs, names, …):
/// <list type="number">
/// <item>Declare a <see cref="UniqueConflict"/> next to the use case's module (here <see cref="Identity.IdentityConflicts"/>);
/// the constraint name is the EF index name (<c>ix_&lt;table&gt;_&lt;columns&gt;</c>).</item>
/// <item>Optionally check before inserting (<c>AnyAsync</c>) as the fast path with a clean error.</item>
/// <item>Always save with <see cref="UniqueConflictSaveExtensions.SaveChangesOrConflictAsync"/>, which turns the
/// race the pre-check cannot catch into the same 409 instead of a 500.</item>
/// </list>
/// </remarks>
public sealed record UniqueConflict(string ConstraintName, ConflictError Error);

public static class UniqueConflictSaveExtensions
{
    /// <summary>
    /// Saves; returns the matching <see cref="ConflictError"/> when one of <paramref name="conflicts"/>
    /// is violated, null on success. Violations of other constraints are rethrown (programming error).
    /// After a conflict the context still tracks the rejected entities: return the error, do not save again.
    /// </summary>
    public static async Task<ConflictError?> SaveChangesOrConflictAsync(
        this IAgcoworkDbContext db,
        CancellationToken cancellationToken,
        params UniqueConflict[] conflicts)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(conflicts);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (UniqueConstraintViolationException exception)
        {
            var match = conflicts.FirstOrDefault(c => string.Equals(c.ConstraintName, exception.ConstraintName, StringComparison.Ordinal));
            if (match is null)
            {
                throw;
            }

            return match.Error;
        }
    }
}
