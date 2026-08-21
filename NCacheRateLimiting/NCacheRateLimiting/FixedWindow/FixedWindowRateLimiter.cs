using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace NCache.OSS.RateLimiting
{
    public sealed class FixedWindowRateLimiter<TKey> : RateLimiter
    {
        private readonly FixedWindowManager _manager;
        private readonly FixedWindowLimiterOptions _options;
        private readonly FixedWindowLease _failedLease = new(isAcquired: false, context: null);
        private int _activeRequestsCount;
        private long _idleSince = Stopwatch.GetTimestamp();

        public override TimeSpan? IdleDuration =>
            Interlocked.CompareExchange(ref _activeRequestsCount, 0, 0) > 0
                    ? null
                    : Stopwatch.GetElapsedTime(
                        _idleSince);

        public FixedWindowRateLimiter(TKey partitionKey, FixedWindowLimiterOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (options.PermitLimit <= 0)
                throw new ArgumentException($"{nameof(options.PermitLimit)} " + "must be greater than 0.", nameof(options));

            if (options.Window <= TimeSpan.Zero)
                throw new ArgumentException($"{nameof(options.Window)} " + "must be greater than TimeSpan.Zero.", nameof(options));

            if (!options.isValid(out var err))
                throw new ArgumentException(err);
            

            _options = new FixedWindowLimiterOptions
            {
                CacheName = options.CacheName,
                Port = options.Port,
                ServerList = options.ServerList,
                PermitLimit = options.PermitLimit,
                Window = options.Window,
                LockTimeout = options.LockTimeout
            };

            _manager = new FixedWindowManager(partitionKey?.ToString() ?? string.Empty, _options);
        }

        public override RateLimiterStatistics? GetStatistics()
        {
            return _manager.GetStatisticsAsync().GetAwaiter().GetResult();
        }

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
        {
            if (permitCount > _options.PermitLimit)
                throw new ArgumentOutOfRangeException(nameof(permitCount), permitCount, $"{permitCount} permit(s) " + $"exceeds the permit limit " + $"of {_options.PermitLimit}.");

            return AcquireAsyncCoreInternal(permitCount);
        }

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            return _failedLease;
        }

        private async ValueTask<RateLimitLease> AcquireAsyncCoreInternal(int permitCount)
        {
            var leaseContext = new FixedWindowLeaseContext
            {
                Limit = _options.PermitLimit,
                Window = _options.Window
            };

            FixedWindowResponse response;

            Interlocked.Increment(ref _activeRequestsCount);

            try
            {
                response = await _manager.TryAcquireLeaseAsync(permitCount);
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequestsCount);

                _idleSince = Stopwatch.GetTimestamp();
            }

            leaseContext.Count = response.Count;
            leaseContext.RetryAfter = response.RetryAfter;
            leaseContext.ExpiresAt = response.ExpiresAt;

            return new FixedWindowLease(isAcquired: response.Allowed, context: leaseContext);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        protected override ValueTask DisposeAsyncCore()
        {
            Dispose(true);
            return default;
        }
    }
}