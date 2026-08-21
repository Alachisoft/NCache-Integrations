using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace NCache.OSS.RateLimiting
{
    public class TokenBucketRateLimiter<TKey> : RateLimiter
    {
        private readonly TokenBucketManager _ncacheManager;
        private readonly TokenBucketLimiterOptions _options;
        private readonly TokenBucketLease FailedLease = new(isAcquired: false, null);

        private int _activeRequestsCount;
        private long _idleSince = Stopwatch.GetTimestamp();

        public override TimeSpan? IdleDuration => Interlocked.CompareExchange(ref _activeRequestsCount, 0, 0) > 0
            ? null
            : Stopwatch.GetElapsedTime(_idleSince);

        public TokenBucketRateLimiter(TKey partitionKey, TokenBucketLimiterOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (options.TokenLimit <= 0)
                throw new ArgumentException($"{nameof(options.TokenLimit)} must be set to a value greater than 0.", nameof(options));
            if (options.TokensPerPeriod <= 0)
                throw new ArgumentException($"{nameof(options.TokensPerPeriod)} must be set to a value greater than 0.", nameof(options));
            if (options.ReplenishmentPeriod <= TimeSpan.Zero)
                throw new ArgumentException($"{nameof(options.ReplenishmentPeriod)} must be set to a value greater than TimeSpan.Zero.", nameof(options));
            if (!options.isValid(out var err))
                throw new ArgumentException(err);

            _options = new TokenBucketLimiterOptions
            {
                CacheName = options.CacheName,
                Port = options.Port,
                ServerList = options.ServerList,
                TokenLimit = options.TokenLimit,
                ReplenishmentPeriod = options.ReplenishmentPeriod,
                TokensPerPeriod = options.TokensPerPeriod,
                LockTimeout = options.LockTimeout
            };

            _ncacheManager = new TokenBucketManager(partitionKey?.ToString() ?? string.Empty, _options);
        }

        public override RateLimiterStatistics? GetStatistics() => _ncacheManager.GetStatistics();

        protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
        {
            _idleSince = Stopwatch.GetTimestamp();
            if (permitCount > _options.TokenLimit)
            {
                throw new ArgumentOutOfRangeException(nameof(permitCount), permitCount, $"{permitCount} permit(s) exceeds the token limit of {_options.TokenLimit}.");
            }

            Interlocked.Increment(ref _activeRequestsCount);
            try
            {
                return await AcquireAsyncCoreInternal(permitCount);
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequestsCount);
                _idleSince = Stopwatch.GetTimestamp();
            }
        }

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            // Enforces asynchronous evaluation logic patterns to ensure clean network synchronization loops
            return FailedLease;
        }

        private async ValueTask<RateLimitLease> AcquireAsyncCoreInternal(int permitCount)
        {
            var leaseContext = new TokenBucketLeaseContext
            {
                Limit = _options.TokenLimit,
            };

            var response = await _ncacheManager.TryAcquireLeaseAsync(permitCount);

            leaseContext.Allowed = response.Allowed;
            leaseContext.Count = response.Count;
            leaseContext.RetryAfter = response.RetryAfter;

            return new TokenBucketLease(response.Allowed, leaseContext);
        }
    }
}
