using Alachisoft.NCache.Client;
using NCache.OSS.Quartz.Extensions;
using NCache.OSS.Quartz.Index;
using NCache.OSS.Quartz.Locking;
using NCache.OSS.Quartz.Storage;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

internal static class ScenarioKit
{
    public static IJobDetail Job(
        string name,
        string group,
        bool durable = true,
        bool nonConcurrent = false,
        bool persistData = false,
        bool requestsRecovery = false,
        (string Key, object? Value)? data = null)
    {
        var builder = JobBuilder.Create<NCacheQuartzStoreTestBase.TestJob>()
            .WithIdentity(name, group)
            .StoreDurably(durable)
            .DisallowConcurrentExecution(nonConcurrent)
            .PersistJobDataAfterExecution(persistData)
            .RequestRecovery(requestsRecovery);

        if (data is { } entry)
        {
            var map = new JobDataMap();
            map[entry.Key] = entry.Value;
            builder = builder.UsingJobData(map);
        }

        return builder.Build();
    }

    public static IOperableTrigger Repeating(string name, string group, JobKey job, DateTimeOffset at, TimeSpan every)
    {
        var trigger = (IOperableTrigger)TriggerBuilder.Create()
            .WithIdentity(name, group)
            .ForJob(job)
            .StartAt(at)
            .WithSimpleSchedule(x => x.WithInterval(every).RepeatForever())
            .Build();
        trigger.ComputeFirstFireTimeUtc(null);
        return trigger;
    }

    public static IOperableTrigger OneShot(string name, string group, JobKey job, DateTimeOffset at)
    {
        var trigger = (IOperableTrigger)TriggerBuilder.Create()
            .WithIdentity(name, group)
            .ForJob(job)
            .StartAt(at)
            .WithSimpleSchedule(x => x.WithRepeatCount(0))
            .Build();
        trigger.ComputeFirstFireTimeUtc(null);
        return trigger;
    }

    public static TriggerAcquisitionRequest DueNow(int maxCount) => new()
    {
        NoLaterThan = DateTimeOffset.UtcNow.AddSeconds(5),
        MaxCount = maxCount,
        TimeWindow = TimeSpan.Zero
    };

    public static async Task<TriggerFiredBundle> FireOneAsync(NCacheJobStore store)
    {
        var acquired = await store.AcquireNextTriggers(DueNow(1));
        Assert.Single(acquired);
        var bundle = (await store.TriggersFired(acquired))[0].TriggerFiredBundle;
        Assert.NotNull(bundle);
        return bundle!;
    }

    public static NCacheJobStoreOptions Options(string prefix, string schedulerName, string instanceId) => new()
    {
        CacheName = TestCache.CacheName,
        SchedulerName = schedulerName,
        InstanceId = instanceId,
        KeyPrefix = prefix
    };

    public static async Task<NCacheJobStore> CreateStoreAsync(
        ICache cache,
        string prefix,
        string schedulerName,
        string instanceId,
        CapturingLoggerProvider? log = null,
        ISchedulerSignaler? signaler = null,
        IObjectSerializer? calendarSerializer = null,
        TimeSpan? lockExpiration = null)
    {
        var store = new NCacheJobStore(
            new NCacheStorage(cache, prefix, schedulerName),
            new NextFireTimeIndex(cache, prefix, schedulerName),
            new NCacheDistributedLocks(cache, prefix, schedulerName, lockExpiration ?? TimeSpan.FromSeconds(30)),
            log?.CreateLogger<NCacheJobStore>(),
            signaler,
            calendarSerializer);

        await store.Initialize(new SchedulerIdentity { SchedulerName = schedulerName, InstanceId = instanceId });

        return store;
    }

    public static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return await condition();
    }
}

