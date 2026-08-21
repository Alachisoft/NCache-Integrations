using Alachisoft.NCache.Client;
using Alachisoft.NCache.Client.Extension;
using Alachisoft.NCache.Runtime.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace NCache.OSS.RateLimiting
{
    internal sealed class FixedWindowManager
    {
        private readonly ICache _cache;
        private readonly FixedWindowLimiterOptions _options;

        private readonly string _windowKey;
        private readonly string _lockKey;
        private readonly string _statsKey;

        public FixedWindowManager(string partitionKey, FixedWindowLimiterOptions options)
        {
            _options = options;

            _cache = CacheManager.GetCache(options.CacheName, options.GetCacheConnectionOptions());

            _windowKey = $"rl:fw:{partitionKey}";
            _lockKey = $"rl:fw:{partitionKey}:lock";
            _statsKey = $"rl:fw:{partitionKey}:stats";

            EnsureInitialized();
        }

        private void EnsureInitialized()
        {
            try
            {
                _cache.Add(_windowKey, new FixedWindowState
                {
                    Count = 0,
                    WindowExpiresUtc = DateTime.UtcNow
                });
                _cache.Add(_lockKey, "lock");
                _cache.Add(_statsKey, new FixedWindowStatisticsEntry());
            }
            catch (Exception ex)
            {
                if (!ex.Message.Contains("The specified key already exists."))
                    throw;
                // Silent failure in case the follow keys already exist in cache
            }
        }

        internal async Task<FixedWindowResponse> TryAcquireLeaseAsync(int permitCount)
        {
            var now = DateTime.UtcNow;

            var state = _cache.Get<FixedWindowState>(_windowKey) ?? new FixedWindowState();

            if (state.WindowExpiresUtc <= now)
            {
                state.Count = 0;
                state.WindowExpiresUtc = now.Add(_options.Window);
            }

            if (state.Count + permitCount > _options.PermitLimit)
            {
                return new FixedWindowResponse
                {
                    Allowed = false,
                    Count = state.Count,
                    ExpiresAt = new DateTimeOffset(state.WindowExpiresUtc).ToUnixTimeMilliseconds(),
                    RetryAfter = state.WindowExpiresUtc - now
                };
            }

            var lockInstance = await AcquireDistributedLockAsync();

            if (lockInstance == null)
            {
                return new FixedWindowResponse
                {
                    Allowed = false,
                    Count = state.Count,
                    ExpiresAt = new DateTimeOffset(state.WindowExpiresUtc).ToUnixTimeMilliseconds(),
                    RetryAfter = _options.LockTimeout
                };
            }

            using (lockInstance)
            {
                state = _cache.Get<FixedWindowState>(_windowKey) ?? new FixedWindowState();

                if (state.WindowExpiresUtc <= now)
                {
                    state.Count = 0;
                    state.WindowExpiresUtc = now.Add(_options.Window);
                }

                bool allowed = state.Count + permitCount <= _options.PermitLimit;

                var stats = _cache.Get<FixedWindowStatisticsEntry>(_statsKey) ?? new FixedWindowStatisticsEntry();

                if (allowed)
                {
                    state.Count += permitCount;
                    _cache.Insert(_windowKey, state);

                    stats.TotalSuccessfulLeases++;
                }
                else
                {
                    stats.TotalFailedLeases++;
                }

                _cache.Insert(_statsKey, stats);

                return new FixedWindowResponse
                {
                    Allowed = allowed,
                    Count = state.Count,
                    ExpiresAt = new DateTimeOffset(state.WindowExpiresUtc).ToUnixTimeMilliseconds(),
                    RetryAfter = state.WindowExpiresUtc - now
                };
            }
        }

        internal async Task<RateLimiterStatistics?> GetStatisticsAsync()
        {
            var now = DateTime.UtcNow;

            var state = _cache.Get<FixedWindowState>(_windowKey) ?? new FixedWindowState();
            var stats = _cache.Get<FixedWindowStatisticsEntry>(_statsKey) ?? new FixedWindowStatisticsEntry();

            long currentCount = state.WindowExpiresUtc <= now ? 0 : state.Count;

            return new RateLimiterStatistics
            {
                CurrentAvailablePermits = Math.Max(_options.PermitLimit - currentCount, 0),
                CurrentQueuedCount = 0,
                TotalSuccessfulLeases = stats.TotalSuccessfulLeases,
                TotalFailedLeases = stats.TotalFailedLeases
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
                }

                if (!acquired && stopwatch.Elapsed < timeout)
                {
                    await Task.Delay(15);
                }
            }

            if (acquired)
            {
                Console.WriteLine($"LOCK-ACQUIRED {lockHandle.Id} Process={Environment.ProcessId} Time={DateTime.Now:HH:mm:ss.fff}");
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
                    Console.WriteLine($"LOCK-RELEASED {_lockToken.Id} Process={Environment.ProcessId} Time={DateTime.Now:HH:mm:ss.fff}");
                    _cache.UnlockKey(_key, _lockToken);
                }
                catch
                {
                }
            }
        }
    }
}
