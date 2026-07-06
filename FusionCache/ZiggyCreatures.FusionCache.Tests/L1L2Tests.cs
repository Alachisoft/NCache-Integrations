using Alachisoft.NCache.Caching.Distributed;
using Alachisoft.NCache.Caching.Distributed.Configuration;
using FusionCacheTests.Stuff;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace FusionCacheTests;

public partial class L1L2Tests
	: AbstractTests
{
	private static readonly bool UseRedis = false;
	private static readonly bool UseNCache = true;

	private static readonly string RedisConnection = "127.0.0.1:6379,ssl=False,abortConnect=false,connectTimeout=1000,syncTimeout=1000";
	private static readonly string NCacheCacheName = "demoCache";

	public L1L2Tests(ITestOutputHelper output)
		: base(output, "MyCache:")
	{
	}

	private FusionCacheOptions CreateFusionCacheOptions(string? cacheName = null, Action<FusionCacheOptions>? configure = null)
	{
		var res = new FusionCacheOptions
		{
			CacheKeyPrefix = TestingCacheKeyPrefix
		};

		if (string.IsNullOrWhiteSpace(cacheName) == false)
		{
			res.CacheName = cacheName;
			res.CacheKeyPrefix = cacheName + ":";
		}

		configure?.Invoke(res);

		return res;
	}

	private static IDistributedCache CreateDistributedCache()
	{
		if (UseRedis)
			return new RedisCache(new RedisCacheOptions() { Configuration = RedisConnection });

		if (UseNCache)
		{

			var config = new NCacheConfiguration
			{
				CacheName = NCacheCacheName,
				EnableLogs = true,
				ExceptionsEnabled = true
			};

			var options = Options.Create(config);

			return new NCacheDistributedCache(options);
		}

		return new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
	}

	private static string CreateRandomCacheName(string cacheName)
	{
		return cacheName + "_" + Guid.NewGuid().ToString("N");
	}

	private static string CreateRandomCacheKey(string key)
	{
		return key + "_" + Guid.NewGuid().ToString("N");
	}
}
