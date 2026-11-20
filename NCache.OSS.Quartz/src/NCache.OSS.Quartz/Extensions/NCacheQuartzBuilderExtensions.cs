using Alachisoft.NCache.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NCache.OSS.Quartz.Index;
using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Locking;
using NCache.OSS.Quartz.Storage;
using Quartz;
using Quartz.Extensibility;
using Quartz.Impl;
using Quartz.Serialization.SystemTextJson;

namespace NCache.OSS.Quartz.Extensions;

public sealed class NCacheJobStoreOptions
{
    public const string AutoInstanceId = "AUTO";

    public required string CacheName { get; set; }
    public string? SchedulerName { get; set; }
    public string? InstanceId { get; set; }

    public string? KeyPrefix { get; set; }

    public TimeSpan? LockExpiration { get; set; }

    public TimeSpan? LockAcquireTimeout { get; set; }

    public TimeSpan? MisfireThreshold { get; set; }

    public TimeSpan? ClusterCheckinInterval { get; set; }

    public TimeSpan? ClusterCheckinMisfireThreshold { get; set; }

    public bool? RepairRegistriesOnStart { get; set; }

    public Action<SystemTextJsonSerializerRegistry>? ConfigureSerializer { get; set; }
}

public static class NCacheQuartzBuilderExtensions
{
    public static IQuartzBuilder UseNCacheStore(
        this IQuartzBuilder q,
        string cacheName,
        string? schedulerName = null,
        string? instanceId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheName);

        return q.UseNCacheStore(new NCacheJobStoreOptions
        {
            CacheName = cacheName,
            SchedulerName = schedulerName,
            InstanceId = instanceId
        });
    }

    /// <summary>
    /// Registers the NCache job store with the scheduler. Every node of a cluster uses the same
    /// cache, scheduler name and key prefix, and its own instance id.
    /// </summary>
    public static IQuartzBuilder UseNCacheStore(this IQuartzBuilder q, NCacheJobStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(q);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrEmpty(options.CacheName))
            throw new ArgumentException("NCache 'CacheName' is required.", nameof(options));

        if (!string.IsNullOrEmpty(options.SchedulerName) && !string.IsNullOrEmpty(q.SchedulerName) &&
            !string.Equals(q.SchedulerName, options.SchedulerName, StringComparison.Ordinal))
            throw new ArgumentException(
                $"NCache 'SchedulerName' is '{options.SchedulerName}' but this scheduler is registered as '{q.SchedulerName}'; " +
                "a named scheduler's name is its registration name, so use the same value for both.", nameof(options));

        var schedulerName = !string.IsNullOrEmpty(options.SchedulerName) ? options.SchedulerName
            : !string.IsNullOrEmpty(q.SchedulerName) ? q.SchedulerName
            : QuartzSchedulerOptions.DefaultInstanceName;

        var generateInstanceId = string.IsNullOrWhiteSpace(options.InstanceId) ||
                                 string.Equals(options.InstanceId, NCacheJobStoreOptions.AutoInstanceId, StringComparison.OrdinalIgnoreCase);

        var resolved = Copy(options, schedulerName);

        q.ConfigureScheduler(o =>
        {
            o.InstanceName = schedulerName;

            if (generateInstanceId)
            {
                o.GenerateInstanceId = true;
            }
            else
            {
                o.GenerateInstanceId = false;
                o.InstanceId = options.InstanceId!;
            }
        });

        q.UseJobStore(sp => CreateJobStore(
            resolved,
            sp.GetService<ILoggerFactory>(),
            sp.GetService<ISchedulerSignaler>(),
            sp.GetService<SystemTextJsonSerializerRegistry>()));

        return q;
    }

    public static IQuartzBuilder UseNCacheStore(this IQuartzBuilder q, IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(q);
        ArgumentNullException.ThrowIfNull(section);

        var options = new NCacheJobStoreOptions
        {
            CacheName = string.Empty
        };
        section.Bind(options);

        if (string.IsNullOrEmpty(options.CacheName))
            throw new InvalidOperationException("NCache 'CacheName' is required in the configuration section.");

        return q.UseNCacheStore(options);
    }

    public static IQuartzBuilder UseNCacheStore(
        this IQuartzBuilder q,
        IConfiguration configurationRoot,
        string sectionKey = "Quartz:NCache")
    {
        ArgumentNullException.ThrowIfNull(q);
        ArgumentNullException.ThrowIfNull(configurationRoot);
        ArgumentException.ThrowIfNullOrEmpty(sectionKey);

        return q.UseNCacheStore(configurationRoot.GetSection(sectionKey));
    }

    public static NCacheJobStore CreateJobStore(
        NCacheJobStoreOptions options,
        ILoggerFactory? loggerFactory = null,
        ISchedulerSignaler? signaler = null,
        SystemTextJsonSerializerRegistry? serializerRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        ICache cache = CacheManager.GetCache(options.CacheName);
        return CreateJobStore(cache, options, loggerFactory, signaler, serializerRegistry);
    }

    public static NCacheJobStore CreateJobStore(
        ICache cache,
        NCacheJobStoreOptions options,
        ILoggerFactory? loggerFactory = null,
        ISchedulerSignaler? signaler = null,
        SystemTextJsonSerializerRegistry? serializerRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);

        var prefix = options.KeyPrefix ?? NCacheKeys.DefaultPrefix;
        var schedulerName = string.IsNullOrEmpty(options.SchedulerName) ? QuartzSchedulerOptions.DefaultInstanceName : options.SchedulerName;

        var storage = new NCacheStorage(cache, prefix, schedulerName);
        var index = new NextFireTimeIndex(cache, prefix, schedulerName);
        var locks = new NCacheDistributedLocks(cache, prefix, schedulerName, options.LockExpiration);

        IObjectSerializer? calendarSerializer = null;

        if (options.ConfigureSerializer is not null)
        {
            var registry = new SystemTextJsonSerializerRegistry();
            options.ConfigureSerializer(registry);
            calendarSerializer = new SystemTextJsonObjectSerializer(registry);
        }
        else if (serializerRegistry is not null)
        {
            calendarSerializer = new SystemTextJsonObjectSerializer(serializerRegistry);
        }

        var jobStore = new NCacheJobStore(storage, index, locks, loggerFactory?.CreateLogger<NCacheJobStore>(), signaler, calendarSerializer);

        if (options.MisfireThreshold.HasValue)
        {
            jobStore.MisfireThreshold = options.MisfireThreshold.Value;
        }

        if (options.ClusterCheckinInterval.HasValue)
        {
            jobStore.ClusterCheckinInterval = options.ClusterCheckinInterval.Value;
        }

        if (options.ClusterCheckinMisfireThreshold.HasValue)
        {
            jobStore.ClusterCheckinMisfireThreshold = options.ClusterCheckinMisfireThreshold.Value;
        }

        if (options.LockAcquireTimeout.HasValue)
        {
            jobStore.LockAcquireTimeout = options.LockAcquireTimeout.Value;
        }

        if (options.RepairRegistriesOnStart.HasValue)
        {
            jobStore.RepairRegistriesOnStart = options.RepairRegistriesOnStart.Value;
        }

        return jobStore;
    }

    private static NCacheJobStoreOptions Copy(NCacheJobStoreOptions options, string schedulerName) => new()
    {
        CacheName = options.CacheName,
        SchedulerName = schedulerName,
        InstanceId = options.InstanceId,
        KeyPrefix = options.KeyPrefix,
        LockExpiration = options.LockExpiration,
        LockAcquireTimeout = options.LockAcquireTimeout,
        MisfireThreshold = options.MisfireThreshold,
        ClusterCheckinInterval = options.ClusterCheckinInterval,
        ClusterCheckinMisfireThreshold = options.ClusterCheckinMisfireThreshold,
        RepairRegistriesOnStart = options.RepairRegistriesOnStart,
        ConfigureSerializer = options.ConfigureSerializer
    };
}

