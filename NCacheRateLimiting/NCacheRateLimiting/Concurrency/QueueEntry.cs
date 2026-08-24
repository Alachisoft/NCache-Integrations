using System;

namespace NCache.OSS.RateLimiting;

[Serializable]
public sealed class QueueEntry
{
    public string RequestId { get; set; } = default!;

    public DateTime CreatedUtc { get; set; }

    public long Sequence { get; set; }
}