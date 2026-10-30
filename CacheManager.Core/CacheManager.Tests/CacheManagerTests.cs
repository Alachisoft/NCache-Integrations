using CacheManager.Core;
using Microsoft.Extensions.Logging;
using NCache.OSS.CacheManager.Core;

namespace NCache.CacheManager.Tests;

public class CacheManagerTests : IDisposable
{
    private readonly ICacheManager<string> _cache;

    public CacheManagerTests()
    {
        var options = new NCacheOptions { CacheName = "defaultCache" };

        _cache = CacheFactory.Build<string>("testCache", settings =>
        {
            settings.WithHandle(typeof(NCacheCacheHandle<>), "ncache_handle_name", true, options);
        }, LoggerFactory.Create(_ => { }));
    }

    public void Dispose() => _cache.Dispose();

    private static string Key() => $"key-{Guid.NewGuid():N}";

    [Fact]
    public void Put_Get_Exists_Remove()
    {
        var key = Key();

        _cache.Put(new CacheItem<string>(key, "Gaming Laptop"));

        Assert.Equal("Gaming Laptop", _cache.Get(key));
        Assert.True(_cache.Exists(key));

        Assert.True(_cache.Remove(key));
        Assert.False(_cache.Exists(key));
    }

    [Fact]
    public void Put_WithExpiration_ItemIsStillReadable()
    {
        var key = Key();

        _cache.Put(new CacheItem<string>(key, "expiring", ExpirationMode.Absolute, TimeSpan.FromMinutes(5)));

        Assert.Equal("expiring", _cache.Get(key));
    }

    [Fact]
    public void Region_Put_Get_Exists_ClearRegion()
    {
        var region = $"region-{Guid.NewGuid():N}";
        var key = Key();

        _cache.Put(new CacheItem<string>(key, region, "John Doe"));

        Assert.Equal("John Doe", _cache.Get<string>(key, region));
        Assert.True(_cache.Exists(key, region));

        _cache.ClearRegion(region);

        Assert.False(_cache.Exists(key, region));
    }
}
