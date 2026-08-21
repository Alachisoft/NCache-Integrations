using System;

namespace NCache.OSS.RateLimiting;

[Serializable]
public sealed class SlidingWindowStatisticsEntry
{
    public long TotalSuccessful { get; set; }

    public long TotalFailed { get; set; }
}
