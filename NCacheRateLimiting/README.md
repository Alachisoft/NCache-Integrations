# NCache.OSS.RateLimiting

An [NCache](https://www.alachisoft.com/ncache/) implementation of the [RateLimiting](https://learn.microsoft.com/en-us/dotnet/api/system.threading.ratelimiting?view=net-11.0-pp) API, providing concurrency, fixed window, sliding window, and token bucket rate limiters backed by an NCache cluster.

This package plugs into the `System.Threading.RateLimiting` abstractions (`RateLimiter`, `RateLimitPartition`, `PartitionedRateLimiter`) as well as ASP.NET Core's `Microsoft.AspNetCore.RateLimiting` middleware, so it can be used as a drop-in rate limiting provider anywhere those types are expected.

## Features

- **Concurrency Limiter** — bounds the number of concurrent in-flight requests, queuing excess requests in strict, cross-node FIFO order and rejecting once the queue is full.
- **Fixed Window Limiter** — allows up to `N` requests per fixed time window, resetting the count when the window elapses.
- **Sliding Window Limiter** — tracks individual request timestamps and continuously slides the evaluation window forward, avoiding the burst-at-boundary behavior of fixed windows.
- **Token Bucket Limiter** — a bucket of tokens that refills at a steady rate, allowing short bursts up to the bucket's capacity while enforcing a long-run average rate.
- State keys are auto-expired in NCache as a safety net against processes that crash while holding a lease.

## Installation

```
dotnet add package NCache.OSS.RateLimiting
```

## Requirements

- .NET 8.0 or higher
- An NCache client connection (`Alachisoft.NCache.Client`) to a running NCache cache/cluster
- References `Alachisoft.NCache.Opensource.SDK` and `System.Threading.RateLimiting`

## Usage

### Setting up rate limiting

```csharp
using Microsoft.AspNetCore.RateLimiting;
using NCache.OSS.RateLimiting;

builder.Services.AddRateLimiter(options =>
{
    options.AddNCacheConcurrencyLimiter("my-policy", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.QueueLimit = 5;
        limiter.CacheName = "myCache";
    });
});
```

`AddRateLimiter` accepts any combination of the four `AddNCacheXxxLimiter` policy registrations below, so it can configure any of the four primitives shown here.

### Concurrency Limiter

```csharp
options.AddNCacheConcurrencyLimiter("my-resource-name", limiter =>
{
    limiter.PermitLimit = 10;
    limiter.QueueLimit = 5;
    limiter.CacheName = "myCache";
});
// or: var concurrencyLimiter = new ConcurrencyRateLimiter<string>("my-resource-name", options);
```

### Fixed Window Limiter

```csharp
options.AddNCacheFixedWindowLimiter("my-resource-name", limiter =>
{
    limiter.PermitLimit = 100;
    limiter.Window = TimeSpan.FromMinutes(1);
    limiter.CacheName = "myCache";
});
// or: var fixedWindowLimiter = new FixedWindowRateLimiter<string>("my-resource-name", options);
```

### Sliding Window Limiter

```csharp
options.AddNCacheSlidingWindowLimiter("my-resource-name", limiter =>
{
    limiter.PermitLimit = 100;
    limiter.Window = TimeSpan.FromMinutes(1);
    limiter.CacheName = "myCache";
});
// or: var slidingWindowLimiter = new NCacheSlidingWindowRateLimiter<string>("my-resource-name", options);
```

### Token Bucket Limiter

```csharp
options.AddNCacheTokenBucketLimiter("my-resource-name", limiter =>
{
    limiter.TokenLimit = 100;
    limiter.TokensPerPeriod = 10;
    limiter.ReplenishmentPeriod = TimeSpan.FromSeconds(10);
    limiter.CacheName = "myCache";
});
// or: var tokenBucketLimiter = new TokenBucketRateLimiting<string>("my-resource-name", options);
```

## Important

If NCache is not installed on the machine, you must ensure that Client. ncconf and Config.ncconf contain all required configuration information.

## Documentation

A guide to NCache API can be found at:

- [NCache Integration Docs](https://www.alachisoft.com/resources/docs/ncache/prog-guide/dot-net-third-party-integrations.html)

- [ASP.NET Core Rate Limiting Docs](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit)
