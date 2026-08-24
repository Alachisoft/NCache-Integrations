using Alachisoft.NCache.Client;
using Alachisoft.NCache.Client.Extension;
using Alachisoft.NCache.Runtime.Caching;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace NCache.OSS.RateLimiting;

internal class SlidingWindowManager
{
    private readonly ICache _cache;
    private readonly SlidingWindowLimiterOptions _options;
    private readonly string _stateKey;
    private readonly string _statsKey;
    private readonly string _lockKey;

    public SlidingWindowManager(string partitionKey, SlidingWindowLimiterOptions options)
    {
        _options = options;
        _cache = CacheManager.GetCache(options.CacheName, options.GetCacheConnectionOptions());

        _stateKey = $"rl:sw:{partitionKey}:state";
        _statsKey = $"rl:sw:{partitionKey}:stats";
        _lockKey = $"rl:sw:{partitionKey}:lock";

        EnsureInitialized();
    }

    private void EnsureInitialized()
    {
        try
        {
            _cache.Add(_stateKey, new SlidingWindowState());

            _cache.Add(_statsKey, new SlidingWindowStatisticsEntry());

            _cache.Add(_lockKey, "lock");
        }
        catch (Exception ex)
        {
            if (!ex.Message.Contains("The specified key already exists."))
                throw;
            // Silent failure in case the follow keys already exist in cache
        }
    }

    internal async Task<SlidingWindowResponse> TryAcquireLeaseAsync()
    {
        var response = new SlidingWindowResponse();

        // 1. Acquire client-side distributed lock
        var distributedLock = await AcquireDistributedLockAsync();

        if (distributedLock == null)
        {
            response.Allowed = false;
            response.Count = 0;
            return response;
        }

        using (distributedLock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var windowMs = (long)_options.Window.TotalMilliseconds;

            // 2. Fetch the state block
            var state = _cache.Get<SlidingWindowState>(_stateKey) ?? new SlidingWindowState();
            var stats = _cache.Get<SlidingWindowStatisticsEntry>(_statsKey) ?? new SlidingWindowStatisticsEntry();

            // 3. Prune timestamps that have slid outside the current window boundary
            long boundaryTimestamp = now - windowMs;
            state.RequestTimestamps.RemoveAll(timestamp => timestamp <= boundaryTimestamp);

            // 4. Check capacity limits
            int currentCount = state.RequestTimestamps.Count;
            bool allowed = currentCount < _options.PermitLimit;

            if (allowed)
            {
                state.RequestTimestamps.Add(now);
                stats.TotalSuccessful++;
                currentCount++; // Increment count to reflect current acquisition
            }
            else
            {
                stats.TotalFailed++;
            }

            var cacheItem = new CacheItem(state)
            {
                Expiration = new Expiration(ExpirationType.Absolute, _options.Window.Add(TimeSpan.FromSeconds(1)))
            };
            _cache.Insert(_stateKey, cacheItem);

            // Stats key deliberately has no expiration -- it's a permanent,
            // ever-growing counter, not a windowed value.
            _cache.Insert(_statsKey, stats);

            response.Allowed = allowed;
            response.Count = currentCount;

            return response;
        }
    }

    internal RateLimiterStatistics? GetStatistics()
    {
        var state = _cache.Get<SlidingWindowState>(_stateKey);
        var stats = _cache.Get<SlidingWindowStatisticsEntry>(_statsKey) ?? new SlidingWindowStatisticsEntry();

        if (state == null)
        {
            return new RateLimiterStatistics
            {
                CurrentAvailablePermits = _options.PermitLimit,
                TotalSuccessfulLeases = stats.TotalSuccessful,
                TotalFailedLeases = stats.TotalFailed
            };
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowMs = (long)_options.Window.TotalMilliseconds;

        // Quick local count clone to accurately filter live elements for telemetry snapshot updates
        long boundaryTimestamp = now - windowMs;
        int activeRequestsCount = state.RequestTimestamps.FindAll(t => t > boundaryTimestamp).Count;

        return new RateLimiterStatistics
        {
            CurrentAvailablePermits = Math.Max(_options.PermitLimit - activeRequestsCount, 0),
            TotalSuccessfulLeases = stats.TotalSuccessful,
            TotalFailedLeases = stats.TotalFailed
        };
    }

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
                // Swallowing exceptions during spin loop
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
                // Swallowed to prevent connection exceptions from tearing down active middleware pipeline contexts
            }
        }
    }
}

internal class SlidingWindowResponse
{
    internal bool Allowed { get; set; }
    internal long Count { get; set; }
}