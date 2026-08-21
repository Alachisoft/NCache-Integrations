using System;

namespace NCache.OSS.RateLimiting;

[Serializable]
public sealed class RateLimiterStatisticsEntry
{
    public long TotalSuccessfulLeases { get; set; }

    public long TotalFailedLeases { get; set; }

    public long SequenceCounter { get; set; }
}