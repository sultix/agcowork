namespace Agcowork.Application.Tenancy;

/// <summary>
/// The instance area (architecture §2a, step 5): the one explicit way past PostgreSQL row-level security for the few
/// queries that have to read across workspaces. While a block entered with <see cref="Enter"/> is open, the database
/// session of this DI scope runs with <c>agcowork.scope = 'instance'</c>, which the tenant policies let through; outside it
/// every tenant table shows only the rows of the current tenant (none without one).
/// </summary>
/// <remarks>
/// <para>
/// Users, fixed by the architecture test <c>InstanceAccessArchitectureTests</c>: <see cref="ResolveTenant"/> and
/// <c>ListMyWorkspaces</c> (the caller's own memberships, before or without a tenant) and <c>InstanceActivityReader</c>
/// (realtime dispatcher, reads the activity log of all workspaces). Each of them also bypasses the EF tenant filter with
/// <c>IgnoreQueryFilters([TenantQueryFilter.Name])</c> in the same file and says in a comment what bounds the query: the
/// two safety nets are lifted together or not at all.
/// </para>
/// <para>
/// Keep the block as small as the queries that need it, and never call a workspace use case inside it:
/// <code>
/// using (instanceAccess.Enter("own memberships before the tenant exists"))
/// {
///     rows = await query.ToListAsync(cancellationToken);
/// }
/// </code>
/// One instance per DI scope (the DbContext of the scope reads <see cref="IsActive"/> before every command). Blocks nest.
/// </para>
/// </remarks>
public sealed class InstanceAccess
{
    private int _depth;

    /// <summary>True while at least one block entered with <see cref="Enter"/> is open.</summary>
    public bool IsActive => Volatile.Read(ref _depth) > 0;

    /// <summary>Opens an instance block; disposing the result closes it. <paramref name="reason"/> documents the call site.</summary>
    /// <param name="reason">Why this query may read across workspaces; required, shown in nothing but the code.</param>
    public IDisposable Enter(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Interlocked.Increment(ref _depth);
        return new Block(this);
    }

    private sealed class Block(InstanceAccess owner) : IDisposable
    {
        private int _closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._depth);
            }
        }
    }
}
