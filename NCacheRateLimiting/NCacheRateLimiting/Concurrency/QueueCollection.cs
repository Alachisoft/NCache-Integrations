using System;
using System.Collections.Generic;

namespace NCache.OSS.RateLimiting;

[Serializable]
public sealed class QueueCollection
{
    public Dictionary<string, QueueEntry> Entries { get; set; } = new();
}