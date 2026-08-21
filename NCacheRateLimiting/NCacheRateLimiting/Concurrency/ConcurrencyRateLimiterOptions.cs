using System;

namespace NCache.OSS.RateLimiting;

/// <summary>
/// Options for <see cref="ConcurrencyRateLimiter{TKey}"/>.
/// </summary>
public sealed class ConcurrencyRateLimiterOptions : RateLimiterOptions
{
    /// <summary>
    /// Maximum concurrent permits.
    /// </summary>
    public int PermitLimit { get; set; }

    /// <summary>
    /// Maximum queued requests.
    /// </summary>
    public int QueueLimit { get; set; }

    /// <summary>
    /// Polling interval for dequeue attempts.
    /// </summary>
    public TimeSpan TryDequeuePeriod { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum expected request duration.
    /// Expired leases are automatically reclaimed.
    /// </summary>
    public TimeSpan ExpectedRequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Lock timeout used for distributed coordination.
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(10);
}