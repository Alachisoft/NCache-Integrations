namespace NCache.OSS.RateLimiting;

internal sealed class ConcurrencyResponse
{
    internal bool Allowed { get; set; }

    internal bool Queued { get; set; }

    internal long Count { get; set; }

    internal long QueueCount { get; set; }
}