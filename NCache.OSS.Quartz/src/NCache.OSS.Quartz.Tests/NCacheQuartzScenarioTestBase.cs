using Alachisoft.NCache.Client;
using Microsoft.Extensions.Logging;
using NCache.OSS.Quartz.Index;
using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Locking;
using NCache.OSS.Quartz.Storage;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public abstract class NCacheQuartzScenarioTestBase : NCacheQuartzStoreTestBase
{
    protected string TriggerAccessLockKey => "ncache#distLock#" + NCacheKeys.TriggerAccessLock(_prefix, SchedulerName);

    protected async Task<NCacheJobStore> NewStoreAsync(
        string instanceId = "node-1",
        ICache? cache = null,
        ILogger<NCacheJobStore>? logger = null,
        TimeSpan? lockExpiration = null,
        ISchedulerSignaler? signaler = null)
    {
        var over = cache ?? _cache!;

        var store = new NCacheJobStore(
            new NCacheStorage(over, _prefix, SchedulerName),
            new NextFireTimeIndex(over, _prefix, SchedulerName),
            new NCacheDistributedLocks(over, _prefix, SchedulerName, lockExpiration),
            logger,
            signaler);

        await store.Initialize(new SchedulerIdentity { SchedulerName = SchedulerName, InstanceId = instanceId });

        return store;
    }

    protected async Task<NCacheJobStore> NewNodeAsync(
        string instanceId = "node-1",
        ICache? cache = null,
        ILogger<NCacheJobStore>? logger = null,
        ISchedulerSignaler? signaler = null)
    {
        var store = await NewStoreAsync(instanceId, cache, logger, signaler: signaler);
        await CheckInAsync(instanceId, DateTimeOffset.UtcNow);
        return store;
    }

    protected Task CheckInAsync(string instanceId, DateTimeOffset lastCheckIn)
        => Storage.CheckInAsync(new Model.NCacheSchedulerState
        {
            SchedulerName = SchedulerName,
            InstanceName = instanceId,
            LastCheckinTimeUtc = lastCheckIn,
            CheckinInterval = TimeSpan.FromSeconds(10)
        });

    protected static IJobDetail Job(
        string name,
        string group = "tests",
        bool durable = true,
        JobDataMap? data = null,
        bool nonConcurrent = false,
        bool persistData = false,
        bool requestsRecovery = false)
    {
        var builder = JobBuilder.Create<TestJob>()
            .WithIdentity(name, group)
            .StoreDurably(durable)
            .RequestRecovery(requestsRecovery);

        if (data is not null)
        {
            builder = builder.UsingJobData(data);
        }

        if (nonConcurrent)
        {
            builder = builder.DisallowConcurrentExecution();
        }

        if (persistData)
        {
            builder = builder.PersistJobDataAfterExecution();
        }

        return builder.Build();
    }

    protected static IOperableTrigger OneShot(string name, string group, JobKey job, DateTimeOffset at, JobDataMap? data = null)
    {
        var builder = TriggerBuilder.Create()
            .WithIdentity(name, group)
            .ForJob(job)
            .StartAt(at)
            .WithSimpleSchedule(s => s.WithRepeatCount(0));

        if (data is not null)
        {
            builder = builder.UsingJobData(data);
        }

        var trigger = (IOperableTrigger) builder.Build();
        trigger.ComputeFirstFireTimeUtc(null);
        return trigger;
    }

    protected static IOperableTrigger Repeating(string name, string group, JobKey job, DateTimeOffset at, TimeSpan every)
    {
        var trigger = (IOperableTrigger) TriggerBuilder.Create()
            .WithIdentity(name, group)
            .ForJob(job)
            .StartAt(at)
            .WithSimpleSchedule(s => s.WithInterval(every).RepeatForever())
            .Build();

        trigger.ComputeFirstFireTimeUtc(null);
        return trigger;
    }

    protected static async Task<List<IOperableTrigger>> AcquireDueAsync(NCacheJobStore store, int maxCount = 1)
        => await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddSeconds(5), maxCount));

    protected static async Task<TriggerFiredBundle> FireOneAsync(NCacheJobStore store)
    {
        var acquired = Assert.Single(await AcquireDueAsync(store));
        var fired = Assert.Single(await store.TriggersFired(new[] { acquired }));

        Assert.NotNull(fired.TriggerFiredBundle);
        return fired.TriggerFiredBundle!;
    }
}

