using System.Collections;
using Alachisoft.NCache.Client;

namespace NCache.OSS.Quartz.Tests;

internal static class TestCache
{
    public const string CacheName = "defaultCache";

    private static readonly string[] TestPrefixes =
    {
        "ncache:quartz:test:",
        "ncache:quartz:index:test:",
        "ncache:quartz:storage:test:",
        "ncache:quartz:repair:test:",
        "ncache:quartz:probe:",
        "ncache:quartz:stress:"
    };

    private static readonly Lazy<ICache> Instance = new(() =>
    {
        var cache = CacheManager.GetCache(CacheName);
        SweepLeftovers(cache);
        return cache;
    });

    public static ICache Get() => Instance.Value;

    private static void SweepLeftovers(ICache cache)
    {
        if (Environment.GetEnvironmentVariable("NCACHE_QUARTZ_TEST_NO_SWEEP") == "1")
        {
            return;
        }

        var stale = new List<string>();

        foreach (DictionaryEntry entry in cache)
        {
            if (entry.Key is string key && TestPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
            {
                stale.Add(key);
            }
        }

        foreach (var chunk in stale.Chunk(500))
        {
            cache.RemoveBulk(chunk);
        }
    }
}

