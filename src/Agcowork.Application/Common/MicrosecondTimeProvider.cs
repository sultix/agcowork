namespace Agcowork.Application.Common;

/// <summary>
/// System clock truncated to microsecond precision. PostgreSQL <c>timestamptz</c> stores microseconds, while
/// <see cref="TimeProvider.System"/> yields 100 ns ticks on Linux; without truncation a timestamp returned
/// right after creation differs from the one read back from the database (CI on Linux failed, macOS with its
/// microsecond clock did not). Every <c>CreatedAt</c>/<c>UpdatedAt</c> comes from this provider.
/// </summary>
public sealed class MicrosecondTimeProvider : TimeProvider
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    public override DateTimeOffset GetUtcNow()
    {
        var now = base.GetUtcNow();
        return new DateTimeOffset(now.UtcTicks - now.UtcTicks % TicksPerMicrosecond, TimeSpan.Zero);
    }
}
