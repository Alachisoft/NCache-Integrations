using Alachisoft.NCache.Caching.Distributed;
using Alachisoft.NCache.Caching.Distributed.Configuration;
using Alachisoft.NCache.Client;
using FusionCacheTests.Stuff;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NCache.ZiggyCreatures.FusionCache.Backplane;
using NCache.ZiggyCreatures.FusionCache.Backplane.Configuration;
using Xunit;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane;
using ZiggyCreatures.Caching.Fusion.Backplane.Memory;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;
using ZiggyCreatures.Caching.Fusion.Chaos;
using System;

namespace FusionCacheTests;

public partial class AutoRecoveryTests
	: AbstractTests
{
	public AutoRecoveryTests(ITestOutputHelper output)
		: base(output, "MyCache:")
	{
	}

	private static readonly bool UseRedis = false;
	private static readonly bool UseRedisBackplane = false;
	private static readonly bool UseNCache = true;
	private static readonly bool UseNCacheBackplane = true;

	private static readonly string RedisConnection = "127.0.0.1:6379,ssl=False,abortConnect=false,connectTimeout=1000,syncTimeout=1000";
	private static readonly string NCacheCacheName = "demoCache";
	
	private readonly TimeSpan InitialBackplaneDelay = TimeSpan.FromMilliseconds(300);
	private readonly TimeSpan MultiNodeOperationsDelay = TimeSpan.FromMilliseconds(300);

	private FusionCacheOptions CreateFusionCacheOptions()
	{
		var res = new FusionCacheOptions
		{
			CacheKeyPrefix = TestingCacheKeyPrefix
		};

		return res;
	}

	private static IDistributedCache CreateDistributedCache()
	{
		if (UseRedis)
			return new RedisCache(new RedisCacheOptions { Configuration = RedisConnection });


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

	private IFusionCacheBackplane CreateBackplane(string connectionId)
	{
		if (UseRedisBackplane)
			return new RedisBackplane(new RedisBackplaneOptions { Configuration = RedisConnection }, logger: CreateXUnitLogger<RedisBackplane>());

		if (UseNCacheBackplane)
			return new NCacheBackplane(NCacheCacheName, new NCacheBackplaneOptions()
			{
				ServerList = new List<FusionCacheServerInfo>
				{
					new FusionCacheServerInfo("20.200.17.24")
				},
				AppName = "TestApp"
			});

		return new MemoryBackplane(new MemoryBackplaneOptions() { ConnectionId = connectionId }, logger: CreateXUnitLogger<MemoryBackplane>());
	}

	private ChaosBackplane CreateChaosBackplane(string connectionId, ILogger<ChaosBackplane>? logger = null)
	{
		return new ChaosBackplane(CreateBackplane(connectionId), logger);
	}

	private IFusionCache CreateFusionCache(string? cacheName, SerializerType? serializerType, IDistributedCache? distributedCache, IFusionCacheBackplane? backplane, Action<FusionCacheOptions>? setupAction = null)
	{
		var options = CreateFusionCacheOptions();

		options.CacheName = cacheName!;
		options.EnableSyncEventHandlersExecution = true;

		setupAction?.Invoke(options);
		var fusionCache = new FusionCache(options, logger: CreateXUnitLogger<FusionCache>());
		fusionCache.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
		fusionCache.DefaultEntryOptions.AllowBackgroundDistributedCacheOperations = false;
		if (distributedCache is not null && serializerType.HasValue)
			fusionCache.SetupDistributedCache(distributedCache, TestsUtils.GetSerializer(serializerType.Value));
		if (backplane is not null)
			fusionCache.SetupBackplane(backplane);

		return fusionCache;
	}
}
