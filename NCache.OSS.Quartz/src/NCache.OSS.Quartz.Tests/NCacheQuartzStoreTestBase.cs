using Alachisoft.NCache.Client;
using NCache.OSS.Quartz.Index;
using NCache.OSS.Quartz.Locking;
using NCache.OSS.Quartz.Model;
using NCache.OSS.Quartz.Storage;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public abstract class NCacheQuartzStoreTestBase : IAsyncLifetime
{
    protected const string SchedulerName = "NCacheQuartzUnitTest";

    protected readonly string _prefix =
        $"ncache:quartz:test:{Guid.NewGuid():N}:";

    protected ICache? _cache;

    protected NCacheStorage Storage =>
        new(
            _cache!,
            _prefix,
            SchedulerName);

    protected NextFireTimeIndex Index =>
        new(
            _cache!,
            _prefix,
            SchedulerName);

    protected NCacheDistributedLocks Locks =>
        new(
            _cache!,
            _prefix,
            SchedulerName,
            TimeSpan.FromSeconds(30));

    public Task InitializeAsync()
    {
        _cache =
            TestCache.Get();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _cache = null;

        return Task.CompletedTask;
    }

    protected NCacheJobStore CreateStore(string instanceId)
    {
        var store =
            new NCacheJobStore(
                Storage,
                Index,
                Locks);

        var identity =
            new SchedulerIdentity
            {
                SchedulerName = SchedulerName,
                InstanceId = instanceId
            };

        store.Initialize(identity)
            .GetAwaiter()
            .GetResult();

        return store;
    }

    protected static NCacheJobData CreateJob(
        string name,
        string group = "tests",
        string? jobClassName = null,
        bool durable = true,
        bool nonConcurrent = false,
        bool persistData = false,
        bool requestsRecovery = false)
    {
        return new NCacheJobData
        {
            SchedulerName = SchedulerName,
            JobName = name,
            JobGroup = group,

            Description = $"Test job {name}",

            JobClassName =
                jobClassName ??
                typeof(TestJob).AssemblyQualifiedName!,

            IsDurable = durable,
            IsNonConcurrent = nonConcurrent,
            IsUpdateData = persistData,
            RequestsRecovery = requestsRecovery
        };
    }

    protected static NCacheTriggerData CreateWaitingTrigger(
    string name,
    string jobName,
    DateTimeOffset nextFire,
    string triggerGroup = "tests",
    string jobGroup = "tests",
    int priority = 5,
    int repeatCount = 1)
    {
        return new NCacheTriggerData
        {
            SchedulerName = SchedulerName,

            TriggerName = name,
            TriggerGroup = triggerGroup,

            JobName = jobName,
            JobGroup = jobGroup,

            Description = $"Test trigger {name}",

            NextFireTimeUtc = nextFire,
            PreviousFireTimeUtc = null,

            Priority = priority,

            TriggerState = TriggerStates.Waiting,

            TriggerKind = NCacheTriggerKind.Simple,

            StartTimeUtc = nextFire,

            EndTimeUtc = null,

            CalendarName = null,

            MisfireInstruction = 0,

            Simple = new NCacheSimpleTriggerData
            {
                RepeatCount = repeatCount,
                RepeatInterval = TimeSpan.FromSeconds(30),
                TimesTriggered = 0
            }
        };
    }

    protected static NCacheTriggerData CreateAcquiredTrigger(
        string name,
        string jobName,
        DateTimeOffset nextFire,
        int repeatCount = 1)
    {
        var trigger =
            CreateWaitingTrigger(
                name,
                jobName,
                nextFire,
                repeatCount: repeatCount);

        trigger.TriggerState =
            TriggerStates.Acquired;

        return trigger;
    }

    protected static IOperableTrigger ToQuartzTrigger(
        NCacheTriggerData trigger)
    {
        return Mapping.TriggerMapper.ToQuartzTrigger(trigger);
    }

    protected static TriggerAcquisitionRequest CreateRequest(
        DateTimeOffset noLaterThan,
        int maxCount,
        TimeSpan? timeWindow = null)
    {
        return new TriggerAcquisitionRequest
        {
            NoLaterThan = noLaterThan,
            MaxCount = maxCount,
            TimeWindow = timeWindow ?? TimeSpan.Zero
        };
    }

    protected static IOperableTrigger CreateQuartzSimpleTrigger(string triggerName, string triggerGroup, JobKey jobKey, DateTimeOffset startAt, int repeatCount = 1)
    {
        var trigger = (IOperableTrigger)TriggerBuilder.Create()
            .WithIdentity(triggerName, triggerGroup)
            .ForJob(jobKey)
            .StartAt(startAt)
            .WithSimpleSchedule(schedule => schedule.WithInterval(TimeSpan.FromSeconds(30)).WithRepeatCount(repeatCount))
            .Build();

        trigger.ComputeFirstFireTimeUtc(null);

        return trigger;
    }

    protected async Task<IOperableTrigger> AcquireForTestAsync(NCacheTriggerData storedTrigger, string instanceId = "instance-1")
    {
        var trigger = ToQuartzTrigger(storedTrigger);
        trigger.FireInstanceId = Guid.NewGuid().ToString("N");

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = trigger.FireInstanceId,
            TriggerName = storedTrigger.TriggerName,
            TriggerGroup = storedTrigger.TriggerGroup,
            InstanceName = instanceId,
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = storedTrigger.NextFireTimeUtc ?? DateTimeOffset.UtcNow,
            Priority = storedTrigger.Priority,
            State = FiredTriggerStates.Acquired,
            JobName = storedTrigger.JobName,
            JobGroup = storedTrigger.JobGroup,
            ExecutionGroup = storedTrigger.ExecutionGroup
        });

        return trigger;
    }

    protected async Task StoreJobAndTriggerAsync(NCacheJobData job, NCacheTriggerData trigger, bool addToIndex = true)
    {
        await Storage.StoreJobAsync(
            job,
            replaceExisting: false);

        if (addToIndex)
        {
            await Index.UpsertAsync(trigger);
        }

        await Storage.StoreTriggerAsync(
            trigger,
            replaceExisting: false);
    }

    public sealed class TestJob : IJob
    {
        public ValueTask Execute(
            IJobExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }
    }
}

