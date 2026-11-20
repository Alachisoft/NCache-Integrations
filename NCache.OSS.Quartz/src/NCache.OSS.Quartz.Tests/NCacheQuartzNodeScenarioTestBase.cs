using Alachisoft.NCache.Client;
using Microsoft.Extensions.Logging;
using NCache.OSS.Quartz.Index;
using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Locking;
using NCache.OSS.Quartz.Model;
using NCache.OSS.Quartz.Storage;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public abstract class NCacheQuartzNodeScenarioTestBase : NCacheQuartzStoreTestBase
{
    protected async Task<NCacheJobStore> NewStoreAsync(
        string instanceId,
        ICache? cache = null,
        ISchedulerSignaler? signaler = null,
        ILogger<NCacheJobStore>? logger = null,
        bool repairOnStart = true)
    {
        var target = cache ?? _cache!;

        var store = new NCacheJobStore(
            new NCacheStorage(target, _prefix, SchedulerName),
            new NextFireTimeIndex(target, _prefix, SchedulerName),
            new NCacheDistributedLocks(target, _prefix, SchedulerName, TimeSpan.FromSeconds(30)),
            logger,
            signaler,
            null)
        {
            RepairRegistriesOnStart = repairOnStart
        };

        await store.Initialize(new SchedulerIdentity { SchedulerName = SchedulerName, InstanceId = instanceId });

        return store;
    }

    protected NCacheStorage StorageOver(ICache cache) => new(cache, _prefix, SchedulerName);

    protected static IJobDetail QuartzJob(string name, string group = "tests", bool durable = true, bool nonConcurrent = false)
        => JobBuilder.Create<TestJob>()
            .WithIdentity(name, group)
            .StoreDurably(durable)
            .DisallowConcurrentExecution(nonConcurrent)
            .Build();

    protected static IOperableTrigger OneShot(string name, string group, IJobDetail job, DateTimeOffset at)
    {
        var trigger = (IOperableTrigger) TriggerBuilder.Create()
            .WithIdentity(name, group)
            .ForJob(job)
            .StartAt(at)
            .WithSimpleSchedule(x => x.WithRepeatCount(0))
            .Build();

        trigger.ComputeFirstFireTimeUtc(null);
        return trigger;
    }

    protected static IOperableTrigger Repeating(string name, string group, IJobDetail job, DateTimeOffset at, TimeSpan every)
    {
        var trigger = (IOperableTrigger) TriggerBuilder.Create()
            .WithIdentity(name, group)
            .ForJob(job)
            .StartAt(at)
            .WithSimpleSchedule(x => x.WithInterval(every).RepeatForever())
            .Build();

        trigger.ComputeFirstFireTimeUtc(null);
        return trigger;
    }

    protected static async Task<List<IOperableTrigger>> AcquireAsync(NCacheJobStore store, int maxCount = 1, IReadOnlyCollection<string>? excludedJobTypes = null)
        => await store.AcquireNextTriggers(new TriggerAcquisitionRequest
        {
            NoLaterThan = DateTimeOffset.UtcNow.AddSeconds(5),
            MaxCount = maxCount,
            TimeWindow = TimeSpan.Zero,
            ExcludedJobTypeNames = excludedJobTypes
        });

    protected static async Task<TriggerFiredBundle> FireOneAsync(NCacheJobStore store)
    {
        var acquired = await AcquireAsync(store);
        Assert.NotEmpty(acquired);

        var fired = await store.TriggersFired(acquired);
        return fired[0].TriggerFiredBundle ?? throw new InvalidOperationException("setup: the trigger did not fire");
    }

    protected Task CheckInAsync(string instanceId, DateTimeOffset lastCheckIn, TimeSpan? interval = null)
        => Storage.CheckInAsync(new NCacheSchedulerState
        {
            SchedulerName = SchedulerName,
            InstanceName = instanceId,
            LastCheckinTimeUtc = lastCheckIn,
            CheckinInterval = interval ?? TimeSpan.FromSeconds(10)
        });

    protected async Task StoreTriggerRowAsync(NCacheTriggerData trigger)
    {
        await Index.UpsertAsync(trigger);
        await Storage.StoreTriggerAsync(trigger, replaceExisting: false);
    }
}

