using System;
using System.Threading.RateLimiting;

namespace NCache.OSS.RateLimiting;

public static class RateLimitPartition
{
    public static RateLimitPartition<TKey>
        GetConcurrencyRateLimiter<TKey>(
            TKey partitionKey,
            Func<TKey, ConcurrencyRateLimiterOptions> factory)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(factory);

        return System.Threading.RateLimiting.RateLimitPartition.Get(
            partitionKey,
            key => new ConcurrencyRateLimiter<TKey>(
                key,
                factory(key)));
    }

    public static RateLimitPartition<TKey>
        GetFixedWindowRateLimiter<TKey>(
            TKey partitionKey,
            Func<TKey, FixedWindowLimiterOptions> factory)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(factory);

        return System.Threading.RateLimiting.RateLimitPartition.Get(
            partitionKey,
            key => new FixedWindowRateLimiter<TKey>(
                key,
                factory(key)));
    }

    public static RateLimitPartition<TKey>
        GetTokenBucketRateLimiter<TKey>(
            TKey partitionKey,
            Func<TKey, TokenBucketLimiterOptions> factory)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(factory);

        return System.Threading.RateLimiting.RateLimitPartition.Get(
            partitionKey,
            key => new TokenBucketRateLimiter<TKey>(
                key,
                factory(key)));
    }

    public static RateLimitPartition<TKey> GetSlidingWindowRateLimiter<TKey>(
    TKey partitionKey,
    Func<TKey, SlidingWindowLimiterOptions> factory)
    where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(factory);

        return System.Threading.RateLimiting.RateLimitPartition.Get(
            partitionKey,
            key => new SlidingWindowRateLimiter<TKey>(key, factory(key)));
    }
}