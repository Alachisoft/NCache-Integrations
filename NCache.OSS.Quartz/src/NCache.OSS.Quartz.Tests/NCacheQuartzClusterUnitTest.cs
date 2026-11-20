using System.Collections.Concurrent;
using Alachisoft.NCache.Client;
using NCache.OSS.Quartz.Extensions;
using NCache.OSS.Quartz.Index;
using NCache.OSS.Quartz.Locking;
using NCache.OSS.Quartz.Model;
using NCache.OSS.Quartz.Storage;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzClusterUnitTest : IAsyncLifetime
{
    private const string SchedulerName = "NCacheQuartzClusterUnitTest";

    private readonly string _prefix = $"ncache:quartz:test:cluster:{Guid.NewGuid():N}:";
    private ICache? _cache;

    private NCacheStorage Storage => new(_cache!, _prefix, SchedulerName);
    private NextFireTimeIndex Index => new(_cache!, _prefix, SchedulerName);

    public Task InitializeAsync()
    {
        _cache = TestCache.Get();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _cache = null;
        return Task.CompletedTask;
    }

    private NCacheJobStore CreateStore(string instanceId)
    {
        var store = new NCacheJobStore(
            new NCacheStorage(_cache!, _prefix, SchedulerName),
            new NextFireTimeIndex(_cache!, _prefix, SchedulerName),
            new NCacheDistributedLocks(_cache!, _prefix, SchedulerName, TimeSpan.FromSeconds(30)));

        store.Initialize(new SchedulerIdentity { SchedulerName = SchedulerName, InstanceId = instanceId }).GetAwaiter().GetResult();
        return store;
    }

    private static NCacheJobData CreateJob(string name, bool nonConcurrent = false) => new()
    {
        SchedulerName = SchedulerName,
        JobName = name,
        JobGroup = "tests",
        JobClassName = typeof(RecordingJob).AssemblyQualifiedName!,
        IsDurable = true,
        IsNonConcurrent = nonConcurrent
    };

    private static NCacheTriggerData CreateDueTrigger(string name, string jobName, int priority = 5, int repeatCount = 0)
    {
        var due = DateTimeOffset.UtcNow.AddSeconds(-1);

        return new NCacheTriggerData
        {
            SchedulerName = SchedulerName,
            TriggerName = name,
            TriggerGroup = "tests",
            JobName = jobName,
            JobGroup = "tests",
            NextFireTimeUtc = due,
            Priority = priority,
            TriggerState = TriggerStates.Waiting,
            TriggerKind = NCacheTriggerKind.Simple,
            StartTimeUtc = due,
            Simple = new NCacheSimpleTriggerData { RepeatCount = repeatCount, RepeatInterval = TimeSpan.FromSeconds(30) }
        };
    }

    private async Task IndexAndStoreTriggersAsync(IReadOnlyList<NCacheTriggerData> triggers)
    {
        await Index.UpsertManyAsync(triggers);

        foreach (var trigger in triggers)
        {
            await Storage.StoreTriggerAsync(trigger, false);
        }
    }

    private static TriggerAcquisitionRequest Request(int maxCount) => new()
    {
        NoLaterThan = DateTimeOffset.UtcNow,
        MaxCount = maxCount,
        TimeWindow = TimeSpan.Zero
    };

    private async Task<IScheduler> BuildSchedulerAsync(string instanceId, int threads = 5)
    {
        var scheduler = await QuartzSchedulerBuilder
            .Create(q => q
                .UseNCacheStore(new NCacheJobStoreOptions
                {
                    CacheName = TestCache.CacheName,
                    SchedulerName = SchedulerName,
                    InstanceId = instanceId,
                    KeyPrefix = _prefix,
                    ClusterCheckinInterval = TimeSpan.FromSeconds(1),
                    ClusterCheckinMisfireThreshold = TimeSpan.FromSeconds(2),
                    RepairRegistriesOnStart = false
                })
                .ConfigureScheduler(o =>
                {
                    o.IdleWaitTime = TimeSpan.FromSeconds(1);
                    o.MaxBatchSize = 5;
                })
                .UseDefaultThreadPool(threads))
            .BuildScheduler();

        return scheduler;
    }

    [Fact]
    public async Task Concurrent_acquisition_from_several_instances_never_hands_out_a_trigger_twice()
    {
        const int triggerCount = 40;
        var stores = new[] { CreateStore("node-a"), CreateStore("node-b"), CreateStore("node-c") };

        var job = CreateJob("job-1");
        await Storage.StoreJobAsync(job, false);

        var triggers = new List<NCacheTriggerData>();
        for (var i = 0; i < triggerCount; i++)
        {
            triggers.Add(CreateDueTrigger($"trigger-{i:D3}", "job-1", priority: i % 7));
        }

        await IndexAndStoreTriggersAsync(triggers);

        var acquiredBy = new ConcurrentBag<(string Instance, string Trigger, string FireInstance)>();

        var workers = stores.Select(store => Task.Run(async () =>
        {
            var idle = 0;

            while (idle < 3)
            {
                var batch = await store.AcquireNextTriggers(Request(5));

                if (batch.Count == 0)
                {
                    idle++;
                    continue;
                }

                foreach (var t in batch)
                {
                    acquiredBy.Add((store.InstanceId, t.Key.Name, t.FireInstanceId!));
                }
            }
        }));

        await Task.WhenAll(workers);

        var byTrigger = acquiredBy.GroupBy(x => x.Trigger).ToList();

        Assert.Equal(triggerCount, acquiredBy.Count);
        Assert.Equal(triggerCount, byTrigger.Count);
        Assert.All(byTrigger, g => Assert.Single(g));
        Assert.Equal(triggerCount, acquiredBy.Select(x => x.FireInstance).Distinct().Count());

        foreach (var trigger in triggers)
        {
            Assert.Equal(TriggerStates.Acquired, (await Storage.GetTriggerAsync("tests", trigger.TriggerName))!.TriggerState);
        }

        Assert.Empty(await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddHours(1), 100));

        var firings = new List<NCacheFiredTriggerData>();
        foreach (var store in stores)
        {
            var own = await Storage.GetFiredTriggersForInstanceAsync(store.InstanceId);
            Assert.All(own, f => Assert.Equal(store.InstanceId, f.InstanceName));
            firings.AddRange(own);
        }

        Assert.Equal(triggerCount, firings.Count);
        Assert.Equal(triggerCount, firings.Select(f => f.EntryId).Distinct().Count());
        Assert.All(firings, f => Assert.Equal(FiredTriggerStates.Acquired, f.State));
    }

    [Fact]
    public async Task Concurrent_full_firing_lifecycle_across_instances_leaves_consistent_state()
    {
        const int triggerCount = 30;
        var stores = new[] { CreateStore("node-a"), CreateStore("node-b"), CreateStore("node-c"), CreateStore("node-d") };

        for (var i = 0; i < triggerCount; i++)
        {
            await Storage.StoreJobAsync(CreateJob($"job-{i:D3}"), false);
        }

        var triggers = new List<NCacheTriggerData>();
        for (var i = 0; i < triggerCount; i++)
        {
            triggers.Add(CreateDueTrigger($"trigger-{i:D3}", $"job-{i:D3}", repeatCount: 3));
        }

        await IndexAndStoreTriggersAsync(triggers);

        var executed = new ConcurrentBag<(string Instance, string Trigger)>();

        var workers = stores.Select(store => Task.Run(async () =>
        {
            var idle = 0;

            while (idle < 5)
            {
                var batch = await store.AcquireNextTriggers(Request(4));

                if (batch.Count == 0)
                {
                    idle++;
                    await Task.Delay(20);
                    continue;
                }

                idle = 0;

                var results = await store.TriggersFired(batch);

                for (var i = 0; i < batch.Count; i++)
                {
                    var bundle = results[i].TriggerFiredBundle;
                    Assert.NotNull(bundle);

                    executed.Add((store.InstanceId, batch[i].Key.Name));
                    await store.TriggeredJobComplete(batch[i], bundle!.JobDetail, SchedulerInstruction.NoInstruction);
                }
            }
        }));

        await Task.WhenAll(workers);

        var missed = new List<string>();
        var executedNames = executed.Select(x => x.Trigger).ToHashSet();
        var indexedNow = (await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100)).Select(c => c.TriggerName).ToHashSet();

        foreach (var trigger in triggers.Where(t => !executedNames.Contains(t.TriggerName)))
        {
            var row = await Storage.GetTriggerAsync("tests", trigger.TriggerName);
            missed.Add($"{trigger.TriggerName}: state={row?.TriggerState ?? "<missing>"}, next={row?.NextFireTimeUtc:HH:mm:ss.fff}, bucket={row?.IndexBucketId}, indexed={indexedNow.Contains(trigger.TriggerName)}");
        }

        Assert.True(missed.Count == 0, $"{missed.Count} trigger(s) never executed:\n{string.Join("\n", missed)}");
        Assert.Equal(triggerCount, executed.Count);
        Assert.Equal(triggerCount, executed.Select(x => x.Trigger).Distinct().Count());

        foreach (var trigger in triggers)
        {
            var stored = (await Storage.GetTriggerAsync("tests", trigger.TriggerName))!;
            Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
            Assert.Equal(1, stored.Simple!.TimesTriggered);
            Assert.NotNull(stored.PreviousFireTimeUtc);
            Assert.Empty(await Storage.GetFiredTriggersForJobAsync("tests", trigger.JobName));
        }

        foreach (var store in stores)
        {
            Assert.Empty(await Storage.GetFiredTriggersForInstanceAsync(store.InstanceId));
        }

        var indexed = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(5), 100);
        Assert.Equal(triggerCount, indexed.Count);
    }

    [Fact]
    public async Task Concurrent_schedule_and_delete_from_several_instances_keeps_registries_and_index_consistent()
    {
        const int jobsPerNode = 15;
        var stores = new[] { CreateStore("node-a"), CreateStore("node-b"), CreateStore("node-c") };

        await Task.WhenAll(stores.Select((store, n) => Task.Run(async () =>
        {
            for (var i = 0; i < jobsPerNode; i++)
            {
                var job = JobBuilder.Create<RecordingJob>().WithIdentity($"job-{n}-{i}", "tests").StoreDurably().Build();
                var trigger = (IOperableTrigger)TriggerBuilder.Create()
                    .WithIdentity($"trigger-{n}-{i}", "tests")
                    .ForJob(job.Key)
                    .StartAt(DateTimeOffset.UtcNow.AddMinutes(1))
                    .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromMinutes(1)).RepeatForever())
                    .Build();
                trigger.ComputeFirstFireTimeUtc(null);

                await store.ScheduleJob(job, trigger);
            }
        })));

        var total = jobsPerNode * stores.Length;

        Assert.Equal(total, (await stores[0].QueryJobs(new JobQuery { Take = 1000 })).Items.Count);
        Assert.Equal(total, (await stores[0].QueryTriggers(new TriggerQuery { Take = 1000 })).Items.Count);
        Assert.Equal(total, (await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(5), 1000)).Count);
        Assert.Equal(total, (await Storage.GetTriggersInGroupAsync("tests")).Count);
        Assert.Equal(total, (await Storage.GetJobsInGroupAsync("tests")).Count);

        await Task.WhenAll(stores.Select((store, n) => Task.Run(async () =>
        {
            for (var i = 0; i < jobsPerNode; i++)
            {
                Assert.True(await store.DeleteJob(new JobKey($"job-{n}-{i}", "tests")));
            }
        })));

        Assert.Empty((await stores[0].QueryJobs(new JobQuery { Take = 1000 })).Items);
        Assert.Empty((await stores[0].QueryTriggers(new TriggerQuery { Take = 1000 })).Items);
        Assert.Empty(await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(5), 1000));
        Assert.Empty(await Storage.GetAllTriggerGroupsAsync());
        Assert.Empty(await Storage.GetAllJobGroupsAsync());
    }

    [Fact]
    public async Task Concurrent_pause_and_resume_from_several_instances_never_loses_index_entries()
    {
        const int triggerCount = 20;
        var stores = new[] { CreateStore("node-a"), CreateStore("node-b") };

        await Storage.StoreJobAsync(CreateJob("job-1"), false);

        var triggers = new List<NCacheTriggerData>();
        for (var i = 0; i < triggerCount; i++)
        {
            var trigger = CreateDueTrigger($"trigger-{i:D3}", "job-1");
            trigger.NextFireTimeUtc = DateTimeOffset.UtcNow.AddMinutes(1);
            triggers.Add(trigger);
        }

        await IndexAndStoreTriggersAsync(triggers);

        await Task.WhenAll(
            Task.Run(async () =>
            {
                for (var round = 0; round < 3; round++)
                {
                    foreach (var t in triggers)
                    {
                        await stores[0].PauseTrigger(new TriggerKey(t.TriggerName, "tests"));
                    }
                }
            }),
            Task.Run(async () =>
            {
                for (var round = 0; round < 3; round++)
                {
                    foreach (var t in triggers)
                    {
                        await stores[1].ResumeTrigger(new TriggerKey(t.TriggerName, "tests"));
                    }
                }
            }));

        var indexed = (await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(5), 100))
            .Select(c => c.TriggerName)
            .ToHashSet();

        foreach (var t in triggers)
        {
            var stored = (await Storage.GetTriggerAsync("tests", t.TriggerName))!;
            Assert.Equal(stored.TriggerState == TriggerStates.Waiting, indexed.Contains(t.TriggerName));
        }

        await stores[0].ResumeAll();
        Assert.Equal(triggerCount, (await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(5), 100)).Count);
    }

    [Fact]
    public async Task Losing_the_lock_mid_operation_aborts_the_operation_instead_of_corrupting_state()
    {
        var triggerLock = Keys.NCacheKeys.TriggerAccessLock(_prefix, SchedulerName);
        var lockKey = "ncache#distLock#" + triggerLock;
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var armed = 0;
        var stolen = 0;
        Func<System.Reflection.MethodInfo, object?[]?, TimeSpan?> stealLock = (method, args) =>
        {
            var key = args is { Length: > 0 } && args[0] is string k ? k : null;

            if (key == lockKey && method.Name == "Add")
            {
                Volatile.Write(ref armed, 1);
                return null;
            }

            if (key is not null && key.Contains(triggerLock, StringComparison.Ordinal))
            {
                return null;
            }

            if (Volatile.Read(ref armed) == 1 && Interlocked.Exchange(ref stolen, 1) == 0)
            {
                _cache!.Remove(lockKey);
                return TimeSpan.FromSeconds(2.5);
            }

            return null;
        };

        var store = new NCacheJobStore(
            new NCacheStorage(cache, _prefix, SchedulerName),
            new NextFireTimeIndex(cache, _prefix, SchedulerName),
            new NCacheDistributedLocks(cache, _prefix, SchedulerName, TimeSpan.FromSeconds(1)));
        await store.Initialize(new SchedulerIdentity { SchedulerName = SchedulerName, InstanceId = "short-lock" });

        control.DelayAfter = stealLock;

        var jobs = new Dictionary<IJobDetail, IReadOnlyCollection<IOperableTrigger>>();

        for (var i = 0; i < 200; i++)
        {
            var job = JobBuilder.Create<RecordingJob>().WithIdentity($"job-{i}", "tests").StoreDurably().Build();
            var trigger = (IOperableTrigger)TriggerBuilder.Create()
                .WithIdentity($"trigger-{i}", "tests").ForJob(job.Key).StartAt(DateTimeOffset.UtcNow.AddMinutes(1))
                .WithSimpleSchedule(s => s.WithRepeatCount(0)).Build();
            trigger.ComputeFirstFireTimeUtc(null);
            jobs[job] = new[] { trigger };
        }

        var ex = await Assert.ThrowsAsync<JobPersistenceException>(async () => await store.ScheduleJobs(jobs));
        Assert.Equal(1, Volatile.Read(ref stolen));

        Assert.Contains("lock expired", ex.Message, StringComparison.OrdinalIgnoreCase);

        var jobsListed = (await store.QueryJobs(new JobQuery { Take = 1000 })).Items;
        var jobsRegistered = await Storage.GetJobsInGroupAsync("tests");
        Assert.Equal(jobsRegistered.Count, jobsListed.Count);

        foreach (var key in jobsRegistered)
        {
            Assert.NotNull(await Storage.GetJobHeaderAsync(key.Group, key.Name));
        }

        var follow = JobBuilder.Create<RecordingJob>().WithIdentity("after-loss", "tests").StoreDurably().Build();
        await store.AddJob(follow);
        Assert.True(await store.Exists(follow.Key));
    }

    [Fact]
    public async Task Two_schedulers_share_repeating_triggers_without_duplicate_executions()
    {
        RecordingJob.Reset();
        const int jobCount = 8;

        var a = await BuildSchedulerAsync("sched-a");
        var b = await BuildSchedulerAsync("sched-b");

        try
        {
            await a.Start();
            await b.Start();

            var interval = TimeSpan.FromMilliseconds(400);

            for (var i = 0; i < jobCount; i++)
            {
                var job = JobBuilder.Create<RecordingJob>().WithIdentity($"job-{i}", "cluster").Build();
                var trigger = TriggerBuilder.Create()
                    .WithIdentity($"trigger-{i}", "cluster")
                    .StartNow()
                    .WithSimpleSchedule(s => s.WithInterval(interval).RepeatForever())
                    .Build();

                await (i % 2 == 0 ? a : b).ScheduleJob(job, trigger);
            }

            await Task.Delay(TimeSpan.FromSeconds(6));
        }
        finally
        {
            await ShutdownQuietlyAsync(a);
            await ShutdownQuietlyAsync(b);
        }

        var executions = RecordingJob.Executions.ToList();

        Assert.NotEmpty(executions);

        var duplicates = executions
            .GroupBy(e => (e.Trigger, e.ScheduledFireTime))
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.True(duplicates.Count == 0, $"{duplicates.Count} occurrence(s) executed more than once: {string.Join(", ", duplicates.Take(5).Select(d => $"{d.Key.Trigger}@{d.Key.ScheduledFireTime:HH:mm:ss.fff}"))}");

        Assert.Contains(executions, e => e.Instance == "sched-a");
        Assert.Contains(executions, e => e.Instance == "sched-b");

        foreach (var trigger in executions.GroupBy(e => e.Trigger))
        {
            Assert.True(trigger.Count() >= 5, $"{trigger.Key} fired only {trigger.Count()} times");
        }

        Assert.Empty(await Storage.GetAllFiredTriggersAsync());
        var triggers = await Storage.GetAllTriggersAsync();
        Assert.Equal(jobCount, triggers.Count);
        Assert.All(triggers, t => Assert.Equal(TriggerStates.Waiting, t.TriggerState));
    }

    [Fact]
    public async Task Non_concurrent_job_never_overlaps_across_two_schedulers()
    {
        OverlapDetectingJob.Reset();

        var a = await BuildSchedulerAsync("sched-a");
        var b = await BuildSchedulerAsync("sched-b");

        try
        {
            await a.Start();
            await b.Start();

            var job = JobBuilder.Create<OverlapDetectingJob>().WithIdentity("serial-job", "cluster").Build();
            var trigger = TriggerBuilder.Create()
                .WithIdentity("serial-trigger", "cluster")
                .StartNow()
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromMilliseconds(150)).RepeatForever())
                .Build();

            await b.ScheduleJob(job, trigger);

            await Task.Delay(TimeSpan.FromSeconds(6));
        }
        finally
        {
            await ShutdownQuietlyAsync(a);
            await ShutdownQuietlyAsync(b);
        }

        Assert.True(OverlapDetectingJob.Executions >= 4, $"only {OverlapDetectingJob.Executions} executions");
        Assert.Equal(1, OverlapDetectingJob.MaxObservedConcurrency);
        Assert.NotEmpty(OverlapDetectingJob.Instances);
    }

    private static async Task ShutdownQuietlyAsync(IScheduler scheduler)
    {
        try
        {
            await scheduler.Shutdown(waitForJobsToComplete: true);
        }
        catch (SchedulerException)
        {
        }
    }

    public sealed class RecordingJob : IJob
    {
        public static readonly ConcurrentBag<(string Instance, string Trigger, DateTimeOffset? ScheduledFireTime)> Executions = new();

        public static void Reset() => Executions.Clear();

        public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
        {
            Executions.Add((context.Scheduler.SchedulerInstanceId, context.Trigger.Key.Name, context.ScheduledFireTimeUtc));
            return ValueTask.CompletedTask;
        }
    }

    [DisallowConcurrentExecution]
    public sealed class OverlapDetectingJob : IJob
    {
        private static int _running;
        private static int _executions;
        private static int _maxObserved;

        public static readonly ConcurrentBag<string> Instances = new();

        public static int Executions => _executions;
        public static int MaxObservedConcurrency => _maxObserved;

        public static void Reset()
        {
            _running = 0;
            _executions = 0;
            _maxObserved = 0;
            Instances.Clear();
        }

        public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref _running);
            Interlocked.Increment(ref _executions);
            Instances.Add(context.Scheduler.SchedulerInstanceId);

            int seen;
            do
            {
                seen = _maxObserved;
            } while (now > seen && Interlocked.CompareExchange(ref _maxObserved, now, seen) != seen);

            try
            {
                await Task.Delay(400, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }
}

