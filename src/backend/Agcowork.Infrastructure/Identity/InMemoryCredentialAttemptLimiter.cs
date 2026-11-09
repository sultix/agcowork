using Agcowork.Application.Identity.Throttling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agcowork.Infrastructure.Identity;

/// <summary>
/// <see cref="ICredentialAttemptLimiter"/> in process memory (AP-043). Assumes a single API instance (architecture §8):
/// with several instances each would count on its own. The table holds at most
/// <see cref="CredentialThrottlingOptions.MaxEntries"/> keys; keys are pseudonyms of fixed size, so random emails cannot
/// grow it. One lock guards everything: a check is a few dictionary operations, far cheaper than the hash it guards.
/// </summary>
/// <remarks>
/// Per key: failures within the window (sliding), a lock with its end, the number of locks in a row and the attempts in
/// flight. Attempts in flight count against the limit, so a burst of parallel requests cannot verify more passwords than
/// the limit allows before the first failure is recorded.
/// </remarks>
internal sealed partial class InMemoryCredentialAttemptLimiter(
    IOptions<CredentialThrottlingOptions> options,
    TimeProvider timeProvider,
    ILogger<InMemoryCredentialAttemptLimiter> logger) : ICredentialAttemptLimiter
{
    /// <summary>Wait when only attempts in flight (not yet failed) fill the limit; they settle within a hash or two.</summary>
    internal static readonly TimeSpan InFlightRetryAfter = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly CredentialThrottlingOptions _options = options.Value;
    private readonly Lock _gate = new();
    private readonly Dictionary<AttemptKey, Entry> _entries = [];
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;
    private DateTimeOffset _nextFullWarning = DateTimeOffset.MinValue;

    /// <summary>Number of tracked keys (for tests).</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public ICredentialAttempt? TryBegin(IReadOnlyList<AttemptKey> keys, out TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(keys);
        retryAfter = TimeSpan.Zero;
        if (!_options.Enabled)
        {
            return UnlimitedCredentialAttempts.Instance;
        }

        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (now >= _nextSweep)
            {
                RemoveExpired(now);
                _nextSweep = now + SweepInterval;
            }

            foreach (var key in keys)
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    var wait = entry.Wait(now, _options.For(key.Kind));
                    retryAfter = wait > retryAfter ? wait : retryAfter;
                }
            }

            if (retryAfter > TimeSpan.Zero)
            {
                return null;
            }

            var missing = keys.Distinct().Count(key => !_entries.ContainsKey(key));
            if (missing > 0 && !MakeRoom(now, missing, out retryAfter))
            {
                return null;
            }

            var reserved = new Entry[keys.Count];
            for (var i = 0; i < keys.Count; i++)
            {
                reserved[i] = GetOrAdd(keys[i]);
                reserved[i].Pending++;
            }

            return new Attempt(this, keys, reserved);
        }
    }

    private Entry GetOrAdd(AttemptKey key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry();
            _entries.Add(key, entry);
        }

        return entry;
    }

    /// <summary>
    /// Makes room for <paramref name="needed"/> new keys within <see cref="CredentialThrottlingOptions.MaxEntries"/>: drops
    /// expired keys, then at least a tenth of the table, those that would expire soonest. Locked keys and keys with attempts
    /// in flight are never dropped (that would lift a lock). When only those are left, new keys are refused until the
    /// first lock ends (<paramref name="retryAfter"/>): known keys keep working, the bound holds, nothing goes unlimited.
    /// </summary>
    private bool MakeRoom(DateTimeOffset now, int needed, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        var limit = _options.MaxEntries;
        if (_entries.Count + needed <= limit)
        {
            return true;
        }

        RemoveExpired(now);
        if (_entries.Count + needed <= limit)
        {
            return true;
        }

        var evict = _entries
            .Where(pair => pair.Value.Pending == 0 && !pair.Value.IsLocked(now))
            .OrderBy(pair => pair.Value.RetainUntil(_options.For(pair.Key.Kind)))
            .Take(Math.Max(_entries.Count + needed - limit, limit / 10))
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in evict)
        {
            _entries.Remove(key);
        }

        if (evict.Count > 0)
        {
            LogTableFull(logger, limit, evict.Count);
        }

        if (_entries.Count + needed <= limit)
        {
            return true;
        }

        var firstUnlock = _entries.Values.Where(entry => entry.IsLocked(now)).Select(entry => entry.LockedUntil).DefaultIfEmpty().Min();
        retryAfter = firstUnlock > now ? firstUnlock - now : InFlightRetryAfter;
        if (now >= _nextFullWarning)
        {
            LogTableLocked(logger, limit, retryAfter);
            _nextFullWarning = now + SweepInterval;
        }

        return false;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries)
        {
            if (entry.IsExpired(now, _options.For(key.Kind)))
            {
                _entries.Remove(key);
            }
        }
    }

    private void Settle(IReadOnlyList<AttemptKey> keys, Entry[] entries, Outcome outcome)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            for (var i = 0; i < keys.Count; i++)
            {
                var (key, entry) = (keys[i], entries[i]);
                entry.Pending--;
                var limit = _options.For(key.Kind);
                switch (outcome)
                {
                    case Outcome.Failed when entry.Fail(now, limit) is { } lockout:
                        LogLocked(logger, key.Kind, key.Subject, lockout, limit.MaxFailures, entry.Locks);
                        break;
                    case Outcome.Succeeded when key.Kind != AttemptKind.LoginAddress:
                        entry.Reset();
                        break;
                    default:
                        break;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Credential attempts locked: {Kind} {Subject} for {Lockout} after {MaxFailures} failures (lock {Locks} in a row).")]
    private static partial void LogLocked(ILogger logger, AttemptKind kind, string subject, TimeSpan lockout, int maxFailures, int locks);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Credential throttling table full ({MaxEntries} keys); dropped {Evicted} unlocked keys that expire soonest.")]
    private static partial void LogTableFull(ILogger logger, int maxEntries, int evicted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Credential throttling table full ({MaxEntries} keys), all locked or in use: new keys are refused for {RetryAfter}. Known keys are unaffected. Logged at most once a minute.")]
    private static partial void LogTableLocked(ILogger logger, int maxEntries, TimeSpan retryAfter);

    private enum Outcome
    {
        Abandoned,
        Failed,
        Succeeded,
    }

    /// <summary>State of one key. Only touched under <see cref="_gate"/>.</summary>
    private sealed class Entry
    {
        private readonly Queue<DateTimeOffset> _failures = new();
        private DateTimeOffset _lockedUntil = DateTimeOffset.MinValue;
        private DateTimeOffset _lastFailure = DateTimeOffset.MinValue;

        public int Pending { get; set; }

        /// <summary>Locks in a row; the next lock lasts <see cref="AttemptLimit.LockoutAfter"/> of it.</summary>
        public int Locks { get; private set; }

        /// <summary>After this, nothing of the key matters any more: no lock, no failure in the window, the row is over.</summary>
        public DateTimeOffset RetainUntil(AttemptLimit limit) =>
            (_lockedUntil > _lastFailure ? _lockedUntil : _lastFailure) + limit.Window;

        public bool IsExpired(DateTimeOffset now, AttemptLimit limit) => Pending == 0 && now >= RetainUntil(limit);

        public DateTimeOffset LockedUntil => _lockedUntil;

        public bool IsLocked(DateTimeOffset now) => _lockedUntil > now;

        public TimeSpan Wait(DateTimeOffset now, AttemptLimit limit)
        {
            if (_lockedUntil > now)
            {
                return _lockedUntil - now;
            }

            Prune(now, limit);
            return _failures.Count + Pending >= limit.MaxFailures ? InFlightRetryAfter : TimeSpan.Zero;
        }

        /// <summary>Records a failure; returns the lock duration when it locks the key.</summary>
        public TimeSpan? Fail(DateTimeOffset now, AttemptLimit limit)
        {
            if (now >= RetainUntil(limit))
            {
                // Quiet for a whole window since the last failure or lock: start over.
                _failures.Clear();
                Locks = 0;
            }

            Prune(now, limit);
            _failures.Enqueue(now);
            _lastFailure = now;
            if (_failures.Count < limit.MaxFailures)
            {
                return null;
            }

            var lockout = limit.LockoutAfter(Locks);
            _lockedUntil = now + lockout;
            Locks++;
            _failures.Clear();
            return lockout;
        }

        public void Reset()
        {
            _failures.Clear();
            _lockedUntil = DateTimeOffset.MinValue;
            _lastFailure = DateTimeOffset.MinValue;
            Locks = 0;
        }

        private void Prune(DateTimeOffset now, AttemptLimit limit)
        {
            while (_failures.TryPeek(out var oldest) && oldest <= now - limit.Window)
            {
                _failures.Dequeue();
            }
        }
    }

    private sealed class Attempt(InMemoryCredentialAttemptLimiter owner, IReadOnlyList<AttemptKey> keys, Entry[] entries) : ICredentialAttempt
    {
        private int _settled;

        public void Failed() => Settle(Outcome.Failed);

        public void Succeeded() => Settle(Outcome.Succeeded);

        public void Dispose() => Settle(Outcome.Abandoned);

        private void Settle(Outcome outcome)
        {
            if (Interlocked.Exchange(ref _settled, 1) == 0)
            {
                owner.Settle(keys, entries, outcome);
            }
        }
    }
}
