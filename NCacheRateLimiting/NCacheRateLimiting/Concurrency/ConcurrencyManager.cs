using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Exceptions;
using System;
using System.Linq;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Alachisoft.NCache.Client.Extension;

namespace NCache.OSS.RateLimiting;

internal sealed class ConcurrencyManager
{
    private readonly ICache _cache;
    private readonly ConcurrencyRateLimiterOptions _options;

    private readonly string _leaseKey;
    private readonly string _queueKey;
    private readonly string _statsKey;
    private readonly string _lockKey;

    internal ConcurrencyManager(
        string partitionKey,
        ConcurrencyRateLimiterOptions options)
    {
        _options = options;

        _cache = CacheManager.GetCache(options.CacheName, options.GetCacheConnectionOptions());

        _leaseKey = $"rl:cc:{partitionKey}:leases";
        _queueKey = $"rl:cc:{partitionKey}:queue";
        _statsKey = $"rl:cc:{partitionKey}:stats";
        _lockKey = $"rl:cc:{partitionKey}:lock";

        EnsureInitialized();
    }

    private void EnsureInitialized()
    {
        try
        {
            _cache.Add(_leaseKey, new LeaseCollection());
            _cache.Add(_queueKey, new QueueCollection());
            _cache.Add(_statsKey, new RateLimiterStatisticsEntry());
            _cache.Add(_lockKey, "lock");
        }
        catch (Exception ex)
        {
            if (!ex.Message.Contains("The specified key already exists."))
                throw;
            // Expected: these keys already exist because another node (or a
            // prior instance on this node) already initialized this partition.
        }
    }

    internal async Task<ConcurrencyResponse> TryAcquireLeaseAsync(
        string requestId,
        bool tryEnqueue = false)
    {
        var lockInstance = await AcquireDistributedLockAsync();

        if (lockInstance == null)
        {
            return new ConcurrencyResponse
            {
                Allowed = false,
                Queued = false,
                Count = -1,
                QueueCount = -1
            };
        }

        using (lockInstance)
        {
            CleanupExpiredEntries();

            var leases = GetLeases();
            var queue = GetQueue();
            var stats = GetStats();

            long activeCount = leases.Leases.Count;
            long queueCount = queue.Entries.Count;

            bool allowed = queueCount == 0 && activeCount < _options.PermitLimit;
            bool queued = false;

            if (allowed)
            {
                leases.Leases[requestId] = new LeaseEntry
                {
                    RequestId = requestId,
                    CreatedUtc = DateTime.UtcNow
                };

                SaveLeases(leases);

                stats.TotalSuccessfulLeases++;
                SaveStats(stats);

                return new ConcurrencyResponse
                {
                    Allowed = true,
                    Queued = false,
                    Count = activeCount + 1,
                    QueueCount = queueCount
                };
            }

            if (_options.QueueLimit > 0 && tryEnqueue)
            {
                queued = queueCount < _options.QueueLimit;

                if (queued)
                {
                    stats.SequenceCounter++;

                    queue.Entries[requestId] = new QueueEntry
                    {
                        RequestId = requestId,
                        CreatedUtc = DateTime.UtcNow,
                        Sequence = stats.SequenceCounter
                    };

                    SaveQueue(queue);
                    SaveStats(stats);

                    return new ConcurrencyResponse
                    {
                        Allowed = false,
                        Queued = true,
                        Count = activeCount,
                        QueueCount = queueCount + 1
                    };
                }
            }

            stats.TotalFailedLeases++;
            SaveStats(stats);

            return new ConcurrencyResponse
            {
                Allowed = false,
                Queued = false,
                Count = activeCount,
                QueueCount = queueCount
            };
        }
    }

    internal async Task<ConcurrencyResponse> TryPromoteQueuedAsync(string requestId)
    {
        var lockInstance = await AcquireDistributedLockAsync();

        if (lockInstance == null)
        {
            return new ConcurrencyResponse
            {
                Allowed = false,
                Queued = true,
                Count = -1,
                QueueCount = -1
            };
        }

        using (lockInstance)
        {
            CleanupExpiredEntries();

            var leases = GetLeases();
            var queue = GetQueue();
            var stats = GetStats();

            long activeCount = leases.Leases.Count;
            long queueCount = queue.Entries.Count;

            if (!queue.Entries.TryGetValue(requestId, out _))
            {
                // Already promoted/expired/cancelled by someone else.
                return new ConcurrencyResponse
                {
                    Allowed = false,
                    Queued = false,
                    Count = activeCount,
                    QueueCount = queueCount
                };
            }

            QueueEntry? headOfLine = null;

            foreach (var candidate in queue.Entries.Values)
            {
                if (headOfLine is null
                    || candidate.Sequence < headOfLine.Sequence
                    || (candidate.Sequence == headOfLine.Sequence
                        && string.CompareOrdinal(candidate.RequestId, headOfLine.RequestId) < 0))
                {
                    headOfLine = candidate;
                }
            }

            if (!string.Equals(headOfLine!.RequestId, requestId, StringComparison.Ordinal))
            {
                // Someone else -- possibly on another node -- has been
                // waiting longer. Let them go first.
                return new ConcurrencyResponse
                {
                    Allowed = false,
                    Queued = true,
                    Count = activeCount,
                    QueueCount = queueCount
                };
            }

            if (activeCount >= _options.PermitLimit)
            {
                return new ConcurrencyResponse
                {
                    Allowed = false,
                    Queued = true,
                    Count = activeCount,
                    QueueCount = queueCount
                };
            }

            queue.Entries.Remove(requestId);

            leases.Leases[requestId] = new LeaseEntry
            {
                RequestId = requestId,
                CreatedUtc = DateTime.UtcNow
            };

            SaveQueue(queue);
            SaveLeases(leases);

            stats.TotalSuccessfulLeases++;
            SaveStats(stats);

            return new ConcurrencyResponse
            {
                Allowed = true,
                Queued = false,
                Count = activeCount + 1,
                QueueCount = queueCount - 1
            };
        }
    }

    internal async Task ReleaseLeaseAsync(string requestId)
    {
        var lockInstance = await AcquireDistributedLockAsync();

        if (lockInstance == null)
        {
            return;
        }

        using (lockInstance)
        {
            var leases = GetLeases();
            if (leases.Leases.Remove(requestId))
            {
                SaveLeases(leases);
            }
        }
    }

    internal async Task ReleaseQueueLeaseAsync(string requestId)
    {
        var lockInstance = await AcquireDistributedLockAsync();

        if (lockInstance == null)
        {
            return;
        }

        using (lockInstance)
        {
            var queue = GetQueue();
            if (queue.Entries.Remove(requestId))
            {
                SaveQueue(queue);
            }
        }
    }

    internal RateLimiterStatistics? GetStatistics()
    {
        var leases = GetLeases();
        var queue = GetQueue();
        var stats = GetStats();

        return new RateLimiterStatistics
        {
            CurrentAvailablePermits =
                Math.Max(_options.PermitLimit - leases.Leases.Count, 0),

            CurrentQueuedCount = queue.Entries.Count,

            TotalSuccessfulLeases = stats.TotalSuccessfulLeases,

            TotalFailedLeases = stats.TotalFailedLeases
        };
    }

    private void CleanupExpiredEntries()
    {
        DateTime cutoff = DateTime.UtcNow - _options.ExpectedRequestTimeout;

        var leases = GetLeases();
        bool leaseChanged = false;

        foreach (var entry in leases.Leases.Values.ToList())
        {
            if (entry.CreatedUtc < cutoff)
            {
                leases.Leases.Remove(entry.RequestId);
                leaseChanged = true;
            }
        }

        if (leaseChanged) SaveLeases(leases);
    }

    private LeaseCollection GetLeases() => _cache.Get<LeaseCollection>(_leaseKey) ?? new LeaseCollection();
    private QueueCollection GetQueue() => _cache.Get<QueueCollection>(_queueKey) ?? new QueueCollection();
    private RateLimiterStatisticsEntry GetStats() => _cache.Get<RateLimiterStatisticsEntry>(_statsKey) ?? new RateLimiterStatisticsEntry();

    private void SaveLeases(LeaseCollection collection) => _cache.Insert(_leaseKey, collection);
    private void SaveQueue(QueueCollection collection) => _cache.Insert(_queueKey, collection);
    private void SaveStats(RateLimiterStatisticsEntry stats) => _cache.Insert(_statsKey, stats);

    private async Task<IDisposable?> AcquireDistributedLockAsync()
    {
        LockToken lockHandle = null!;
        bool acquired = false;
        var timeout = TimeSpan.FromSeconds(3);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (!acquired && stopwatch.Elapsed < timeout)
        {
            try
            {
                acquired = _cache.LockKey(_lockKey, out lockHandle!, _options.LockTimeout);
            }
            catch (Exception)
            {
            }

            if (!acquired && stopwatch.Elapsed < timeout)
            {
                await Task.Delay(15);
            }
        }

        return acquired ? new DistributedLock(_cache, _lockKey, lockHandle) : null;
    }

    private sealed class DistributedLock : IDisposable
    {
        private readonly ICache _cache;
        private readonly string _key;
        private readonly LockToken _lockToken;

        public DistributedLock(ICache cache, string key, LockToken lockHandle)
        {
            _cache = cache;
            _key = key;
            _lockToken = lockHandle;
        }

        public void Dispose()
        {
            try
            {
                _cache.UnlockKey(_key, _lockToken);
            }
            catch
            {
                // Swallowed: If we can't unlock due to network loss, NCache auto-expires locks based on lease durations
            }
        }
    }
}