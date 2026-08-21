using System;
using System.Collections.Generic;

namespace NCache.OSS.RateLimiting;

[Serializable]
public class SlidingWindowState
{
    public List<long> RequestTimestamps { get; set; } = new();
}
