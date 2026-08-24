using Alachisoft.NCache.Client;
using System;

namespace NCache.OSS.RateLimiting;

public class SlidingWindowLimiterOptions : RateLimiterOptions
{
    public int PermitLimit { get; set; }
    public TimeSpan Window { get; set; } = TimeSpan.Zero;
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(5);
}