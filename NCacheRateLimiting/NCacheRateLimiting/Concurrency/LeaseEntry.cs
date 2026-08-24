using System;

namespace NCache.OSS.RateLimiting;

[Serializable]
public sealed class LeaseEntry
{
    public string RequestId { get; set; } = default!;

    public DateTime CreatedUtc { get; set; }
}