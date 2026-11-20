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
    public void Regions_WithSameKey_RemainIndependent()
    {
        var key = Key();
        var firstRegion = $"region-{Guid.NewGuid():N}";
        var secondRegion = $"region-{Guid.NewGuid():N}";

        try
        {
            Assert.True(_cache.Add(new CacheItem<string>(key, "global")));
            Assert.True(_cache.Add(new CacheItem<string>(key, firstRegion, "first")));
            Assert.True(_cache.Add(new CacheItem<string>(key, secondRegion, "second")));

            Assert.Equal("global", _cache.Get(key));
            Assert.Equal("first", _cache.Get<string>(key, firstRegion));
            Assert.Equal("second", _cache.Get<string>(key, secondRegion));

            _cache.Put(new CacheItem<string>(key, firstRegion, "updated"));
            Assert.Equal("updated", _cache.Get<string>(key, firstRegion));
            Assert.Equal("second", _cache.Get<string>(key, secondRegion));

            _cache.ClearRegion(firstRegion);
            Assert.False(_cache.Exists(key, firstRegion));
            Assert.Null(_cache.Get<string>(key, firstRegion));
            Assert.True(_cache.Exists(key));
            Assert.True(_cache.Exists(key, secondRegion));

            Assert.True(_cache.Remove(key, secondRegion));
            Assert.False(_cache.Exists(key, secondRegion));
            Assert.Equal("global", _cache.Get(key));
        }
        finally
        {
            _cache.Remove(key);
            _cache.ClearRegion(firstRegion);
            _cache.ClearRegion(secondRegion);
        }
    }

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
