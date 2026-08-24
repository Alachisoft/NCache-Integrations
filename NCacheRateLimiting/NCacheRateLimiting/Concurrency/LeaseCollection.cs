using System;
using System.Collections.Generic;

namespace NCache.OSS.RateLimiting;

[Serializable]
public sealed class LeaseCollection
{
    public Dictionary<string, LeaseEntry> Leases { get; set; } = new();
}