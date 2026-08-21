using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace NCache.OSS.RateLimiting;

public class SlidingWindowRateLimiter<TKey> : RateLimiter
{
    private readonly SlidingWindowManager _ncacheManager;
    private readonly SlidingWindowLimiterOptions _options;
    private readonly SlidingWindowLease FailedLease = new(isAcquired: false, null);

    private int _activeRequestsCount;
    private long _idleSince = Stopwatch.GetTimestamp();

    public override TimeSpan? IdleDuration => Interlocked.CompareExchange(ref _activeRequestsCount, 0, 0) > 0
        ? null
        : Stopwatch.GetElapsedTime(_idleSince);

    public SlidingWindowRateLimiter(TKey partitionKey, SlidingWindowLimiterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.PermitLimit <= 0)
            throw new ArgumentException($"{nameof(options.PermitLimit)} must be set to a value greater than 0.", nameof(options));
        if (options.Window <= TimeSpan.Zero)
            throw new ArgumentException($"{nameof(options.Window)} must be set to a value greater than TimeSpan.Zero.", nameof(options));
        if (!options.isValid(out var err))
            throw new ArgumentException(err);

        _options = new SlidingWindowLimiterOptions
        {
            CacheName = options.CacheName,
            ServerList = options.ServerList,
            Port = options.Port,
            PermitLimit = options.PermitLimit,
            Window = options.Window,
            LockTimeout = options.LockTimeout
        };

        _ncacheManager = new SlidingWindowManager(partitionKey?.ToString() ?? string.Empty, _options);
    }

    public override RateLimiterStatistics? GetStatistics()
    {
        return _ncacheManager.GetStatistics();
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        _idleSince = Stopwatch.GetTimestamp();
        if (permitCount > _options.PermitLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(permitCount), permitCount, $"{permitCount} permit(s) exceeds the permit limit of {_options.PermitLimit}.");
        }

        Interlocked.Increment(ref _activeRequestsCount);
        try
        {
            return await AcquireAsyncCoreInternal();
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequestsCount);
            _idleSince = Stopwatch.GetTimestamp();
        }
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        return FailedLease;
    }

    private async ValueTask<RateLimitLease> AcquireAsyncCoreInternal()
    {
        var leaseContext = new SlidingWindowLeaseContext
        {
            Limit = _options.PermitLimit,
            Window = _options.Window
        };

        var response = await _ncacheManager.TryAcquireLeaseAsync();

        leaseContext.Count = response.Count;
        leaseContext.Allowed = response.Allowed;

        return new SlidingWindowLease(response.Allowed, leaseContext);
    }

    private sealed class SlidingWindowLeaseContext
    {
        public long Count { get; set; }
        public long Limit { get; set; }
        public TimeSpan Window { get; set; }
        public bool Allowed { get; set; }
    }

    private sealed class SlidingWindowLease : RateLimitLease
    {
        private static readonly string[] s_allMetadataNames = { "Limit", "Remaining" };
        private readonly SlidingWindowLeaseContext? _context;

        public SlidingWindowLease(bool isAcquired, SlidingWindowLeaseContext? context)
        {
            IsAcquired = isAcquired;
            _context = context;
        }

        public override bool IsAcquired { get; }
        public override IEnumerable<string> MetadataNames => s_allMetadataNames;

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (_context is null)
            {
                metadata = default;
                return false;
            }

            if (metadataName == "Limit")
            {
                metadata = _context.Limit.ToString();
                return true;
            }

            if (metadataName == "Remaining")
            {
                metadata = Math.Max(_context.Limit - _context.Count, 0);
                return true;
            }

            metadata = default;
            return false;
        }
    }
}