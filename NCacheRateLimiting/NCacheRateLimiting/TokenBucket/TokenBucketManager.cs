using Alachisoft.NCache.Client;
using Alachisoft.NCache.Client.Extension;
using Alachisoft.NCache.Runtime.Caching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace NCache.OSS.RateLimiting
{
    internal class TokenBucketManager
    {
        private readonly ICache _cache;
        private readonly TokenBucketLimiterOptions _options;
        private readonly string _stateKey;
        private readonly string _statsKey;
        private readonly string _lockKey;

        public TokenBucketManager(string partitionKey, TokenBucketLimiterOptions options)
        {
            _options = options;
            _cache = CacheManager.GetCache(options.CacheName, options.GetCacheConnectionOptions());

            _stateKey = $"rl:tb:{partitionKey}:state";
            _statsKey = $"rl:tb:{partitionKey}:stats";
            _lockKey = $"rl:tb:{partitionKey}:lock";

            EnsureInitialized();
        }

        private void EnsureInitialized()
        {
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                _cache.Add(_stateKey, new TokenBucketState
                {
                    CurrentTokens = _options.TokenLimit,
                    LastRefreshedMs = now
                });

                _cache.Add(_statsKey, new TokenBucketStatisticsEntry());

                _cache.Add(_lockKey, "lock");
            }
            catch (Exception ex)
            {
                if (!ex.Message.Contains("The specified key already exists."))
                    throw;
                // Silent failure in case the follow keys already exist in cache
            }
        }

        internal async Task<TokenBucketResponse> TryAcquireLeaseAsync(int permitCount)
        {
            var response = new TokenBucketResponse();

            // 1. Attempt to acquire the custom distributed lock
            var distributedLock = await AcquireDistributedLockAsync();

            if (distributedLock == null)
            {
                // Internal retry loop timed out (3 seconds), reject safely
                response.Allowed = false;
                response.Count = 0;
                response.RetryAfter = (int)Math.Ceiling(_options.ReplenishmentPeriod.TotalSeconds);
                return response;
            }

            using (distributedLock)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var periodMs = (long)_options.ReplenishmentPeriod.TotalMilliseconds;

                var state = _cache.Get<TokenBucketState>(_stateKey);
                if (state == null)
                {
                    state = new TokenBucketState
                    {
                        CurrentTokens = _options.TokenLimit,
                        LastRefreshedMs = now
                    };
                }

                var stats = _cache.Get<TokenBucketStatisticsEntry>(_statsKey) ?? new TokenBucketStatisticsEntry();

                // Calculate replenishment based on elapsed time
                long timeSinceLastRefreshed = Math.Max(0, now - state.LastRefreshedMs);
                long periodsSinceLastRefreshed = timeSinceLastRefreshed / periodMs;

                double replenishedTokens = state.CurrentTokens + (periodsSinceLastRefreshed * _options.TokensPerPeriod);
                double currentTokens = Math.Min(_options.TokenLimit, replenishedTokens);

                long timeOfLastReplenishment = now;
                if (state.LastRefreshedMs > 0)
                {
                    timeOfLastReplenishment = state.LastRefreshedMs + (periodsSinceLastRefreshed * periodMs);
                }

                // Evaluate token availability
                bool allowed = currentTokens >= permitCount;
                if (allowed)
                {
                    currentTokens -= permitCount;
                    state.CurrentTokens = currentTokens;
                    state.LastRefreshedMs = timeOfLastReplenishment;

                    // Determine dynamic TTL based on worst-case bucket recovery time
                    double periodsUntilFull = Math.Ceiling((double)_options.TokenLimit / _options.TokensPerPeriod);
                    double ttlMs = Math.Ceiling(periodsUntilFull * periodMs);

                    var cacheItem = new CacheItem(state)
                    {
                        Expiration = new Expiration(ExpirationType.Absolute, TimeSpan.FromMilliseconds(ttlMs))
                    };

                    // Update the state object in the cache
                    _cache.Insert(_stateKey, cacheItem);

                    stats.TotalSuccessfulLeases++;
                }
                else
                {
                    stats.TotalFailedLeases++;
                }

                // Stats key deliberately has no expiration -- it's a
                // permanent, ever-growing counter, not a windowed value.
                _cache.Insert(_statsKey, stats);

                // Calculate client retry backoff window if rejected
                int retryAfter = 0;
                if (!allowed)
                {
                    long msRemaining = periodMs - (now - timeOfLastReplenishment);
                    retryAfter = (int)Math.Ceiling(Math.Max(0, msRemaining) / 1000.0);
                }

                response.Allowed = allowed;
                response.Count = (long)currentTokens;
                response.RetryAfter = retryAfter;

                return response;
            }
        }

        internal RateLimiterStatistics? GetStatistics()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var periodMs = (long)_options.ReplenishmentPeriod.TotalMilliseconds;

            var state = _cache.Get<TokenBucketState>(_stateKey);
            var stats = _cache.Get<TokenBucketStatisticsEntry>(_statsKey) ?? new TokenBucketStatisticsEntry();

            double currentTokens = _options.TokenLimit;

            if (state != null)
            {
                long timeSinceLastRefreshed = Math.Max(0, now - state.LastRefreshedMs);
                long periodsSinceLastRefreshed = periodMs > 0 ? timeSinceLastRefreshed / periodMs : 0;
                double replenishedTokens = state.CurrentTokens + (periodsSinceLastRefreshed * _options.TokensPerPeriod);
                currentTokens = Math.Min(_options.TokenLimit, replenishedTokens);
            }

            return new RateLimiterStatistics
            {
                CurrentAvailablePermits = (long)currentTokens,
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
                    // Swallowing exception during spin loop to protect middleware runtime execution
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
}