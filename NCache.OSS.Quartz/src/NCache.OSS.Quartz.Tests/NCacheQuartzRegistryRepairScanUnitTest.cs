using System.Collections.Concurrent;
using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Serialization;
using Quartz;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzRegistryRepairScanUnitTest : NCacheQuartzNodeScenarioTestBase
{
    private static Func<System.Reflection.MethodInfo, object?[]?, bool> FailReadsOfEachKey(int times, Func<string, bool> match)
    {
        var counts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        return (method, args) =>
            method.Name == "Get" &&
            args is { Length: > 0 } && args[0] is string key && match(key) &&
            counts.AddOrUpdate(key, 1, (_, n) => n + 1) <= times;
    }

    private List<string> RegisteredJobNames(string group)
    {
        var bytes = _cache!.Get<byte[]>(NCacheKeys.JobsInGroup(_prefix, SchedulerName, group));
        return bytes is null ? new List<string>() : NCacheSerializer.Deserialize<List<string>>(bytes);
    }

    [Fact]
    public async Task N17_start_up_repair_with_one_failed_read_per_row_keeps_the_job_and_trigger_registered()
    {
        var (cache, fault) = FaultInjectingCache.Wrap(_cache!);
        var first = await NewStoreAsync("node-1", cache);

        var job = QuartzJob("j");
        var trigger = Repeating("t", "tests", job, DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromHours(1));
        await first.ScheduleJob(job, trigger);
        await first.SchedulerStarted();
        await first.Shutdown();

        var jobRow = NCacheKeys.Job(_prefix, SchedulerName, "tests", "j");
        var triggerRow = NCacheKeys.Trigger(_prefix, SchedulerName, "tests", "t");
        fault.FailWhen = FailReadsOfEachKey(1, k => k == jobRow || k == triggerRow);

        var second = await NewStoreAsync("node-1", cache);

        try
        {
            await second.SchedulerStarted();
            fault.FailWhen = null;

            Assert.Single((await second.QueryJobs(new JobQuery())).Items);
            Assert.Single((await second.QueryTriggers(new TriggerQuery())).Items);
            Assert.Single(await second.GetTriggersForJob(job.Key));

            var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddHours(2), 10);
            Assert.Contains(candidates, c => c.TriggerName == "t");
        }
        finally
        {
            fault.FailWhen = null;
            await second.Shutdown();
        }
    }

    [Fact]
    public async Task N17_rebuild_retries_a_row_read_that_fails_twice()
    {
        await Storage.StoreJobAsync(CreateJob("job-1"), replaceExisting: false);
        await StoreTriggerRowAsync(CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddHours(1)));

        var (cache, fault) = FaultInjectingCache.Wrap(_cache!);
        var triggerRow = NCacheKeys.Trigger(_prefix, SchedulerName, "tests", "trigger-1");
        fault.FailWhen = FailReadsOfEachKey(2, k => k == triggerRow);

        await StorageOver(cache).RebuildRegistriesAsync();

        Assert.Contains(await Storage.GetTriggersInGroupAsync("tests"), k => k.Name == "trigger-1");
        Assert.Single(await Storage.GetTriggersForJobAsync("tests", "job-1"));
    }

    [Fact]
    public async Task N17_rebuild_aborts_without_writing_when_a_row_read_keeps_failing()
    {
        await Storage.StoreJobAsync(CreateJob("job-1"), replaceExisting: false);
        await StoreTriggerRowAsync(CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddHours(1)));

        await Storage.StoreJobAsync(CreateJob("job-gone"), replaceExisting: false);
        _cache!.Remove(NCacheKeys.Job(_prefix, SchedulerName, "tests", "job-gone"));

        var (cache, fault) = FaultInjectingCache.Wrap(_cache!);
        var triggerRow = NCacheKeys.Trigger(_prefix, SchedulerName, "tests", "trigger-1");
        fault.FailWhen = FailReadsOfEachKey(3, k => k == triggerRow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => StorageOver(cache).RebuildRegistriesAsync());

        Assert.Contains(await Storage.GetTriggersInGroupAsync("tests"), k => k.Name == "trigger-1");
        Assert.Single(await Storage.GetTriggersForJobAsync("tests", "job-1"));
        Assert.Contains("job-gone", RegisteredJobNames("tests"));

        fault.FailWhen = null;
        await StorageOver(cache).RebuildRegistriesAsync();

        Assert.DoesNotContain("job-gone", RegisteredJobNames("tests"));
        Assert.Contains("job-1", RegisteredJobNames("tests"));
    }

    [Fact]
    public async Task N17_scan_retry_budget_of_twenty_covers_twenty_failed_rows()
    {
        for (var i = 0; i < 20; i++)
        {
            await Storage.StoreJobAsync(CreateJob($"job-{i:D2}"), replaceExisting: false);
        }

        var (cache, fault) = FaultInjectingCache.Wrap(_cache!);
        var jobPrefix = $"{_prefix}{SchedulerName}:job:";
        fault.FailWhen = FailReadsOfEachKey(1, k => k.StartsWith(jobPrefix, StringComparison.Ordinal) && k.IndexOf(':', jobPrefix.Length) < 0);

        await StorageOver(cache).RebuildRegistriesAsync();

        Assert.Equal(20, (await Storage.GetJobsInGroupAsync("tests")).Count);
    }

    [Fact]
    public async Task N17_scan_retry_budget_is_shared_so_a_twenty_first_failed_row_aborts_the_rebuild()
    {
        for (var i = 0; i < 21; i++)
        {
            await Storage.StoreJobAsync(CreateJob($"job-{i:D2}"), replaceExisting: false);
        }

        var (cache, fault) = FaultInjectingCache.Wrap(_cache!);
        var jobPrefix = $"{_prefix}{SchedulerName}:job:";
        fault.FailWhen = FailReadsOfEachKey(1, k => k.StartsWith(jobPrefix, StringComparison.Ordinal) && k.IndexOf(':', jobPrefix.Length) < 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => StorageOver(cache).RebuildRegistriesAsync());

        Assert.Equal(21, (await Storage.GetJobsInGroupAsync("tests")).Count);
    }

    [Fact]
    public async Task N18_rebuild_without_the_scheduler_registry_leaves_the_instance_registry_as_it_is()
    {
        await Storage.RegisterSchedulerInstanceAsync("node-registering");

        await Storage.RebuildRegistriesAsync(includeSchedulerRegistry: false);

        Assert.Contains("node-registering", await Storage.GetRegisteredSchedulerInstanceNamesAsync());
    }

    [Fact]
    public async Task N18_rebuild_with_the_scheduler_registry_keeps_live_and_busy_nodes_and_drops_the_rest()
    {
        await Storage.RegisterSchedulerInstanceAsync("node-ghost");
        await CheckInAsync("node-live", DateTimeOffset.UtcNow);

        await Storage.StoreJobAsync(CreateJob("job-1"), replaceExisting: false);
        var acquired = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        await StoreTriggerRowAsync(acquired);
        await AcquireForTestAsync(acquired, "node-busy");

        await Storage.RebuildRegistriesAsync(includeSchedulerRegistry: true);

        var registered = await Storage.GetRegisteredSchedulerInstanceNamesAsync();
        Assert.Contains("node-live", registered);
        Assert.Contains("node-busy", registered);
        Assert.DoesNotContain("node-ghost", registered);
    }

    [Fact]
    public async Task N18_repair_started_from_a_trigger_access_failure_does_not_rewrite_the_instance_registry()
    {
        var logs = new CapturingLoggerProvider();
        var (cache, fault) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewStoreAsync("node-1", cache, logger: logs.CreateLogger<NCacheJobStore>());

        await Storage.RegisterSchedulerInstanceAsync("node-registering");

        var failingJob = NCacheKeys.Job(_prefix, SchedulerName, "tests", "x");
        fault.FailWhen = FaultInjectingCache.Times(1, FaultInjectingCache.Call("Add", k => k == failingJob));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.AddJob(QuartzJob("x")));
        fault.FailWhen = null;

        Assert.True(logs.Contains("rebuilding registries and index"), "the unexpected failure should have started a repair");
        Assert.Contains("node-registering", await Storage.GetRegisteredSchedulerInstanceNamesAsync());
        Assert.False(await Storage.JobExistsAsync("tests", "x"));
    }

    [Fact]
    public async Task N18_node_that_starts_while_a_repair_runs_stays_registered()
    {
        var (cache, fault) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewStoreAsync("node-1", cache);

        var failingJob = NCacheKeys.Job(_prefix, SchedulerName, "tests", "x");
        fault.FailWhen = FaultInjectingCache.Times(1, FaultInjectingCache.Call("Add", k => k == failingJob));

        var repairScanning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = 1;
        fault.DelayAfter = (method, _) =>
        {
            if (method.Name == "GetEnumerator" && Interlocked.Exchange(ref armed, 0) == 1)
            {
                repairScanning.TrySetResult();
                return TimeSpan.FromSeconds(2);
            }

            return null;
        };

        var failing = Task.Run(async () =>
        {
            try
            {
                await store.AddJob(QuartzJob("x"));
            }
            catch (InvalidOperationException)
            {
            }
        });

        Assert.Same(repairScanning.Task, await Task.WhenAny(repairScanning.Task, Task.Delay(TimeSpan.FromSeconds(20))));

        var nodeC = await NewStoreAsync("node-c", repairOnStart: false);

        try
        {
            await nodeC.SchedulerStarted();
            await failing;

            Assert.Contains("node-c", await Storage.GetRegisteredSchedulerInstanceNamesAsync());
        }
        finally
        {
            fault.FailWhen = null;
            fault.DelayAfter = null;
            await nodeC.Shutdown();
        }
    }
}

