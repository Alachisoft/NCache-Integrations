using System;

namespace NCache.OSS.RateLimiting
{
    [Serializable]
    public sealed class TokenBucketStatisticsEntry
    {
        public long TotalSuccessfulLeases { get; set; }

        public long TotalFailedLeases { get; set; }
    }
}
