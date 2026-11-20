using System.Diagnostics;
using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Model;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzCompletionReleaseRetryUnitTest : NCacheQuartzStoreTestBase
{
    private const string RetryLogFormat = "{0} failed (attempt {1})";

    private const string Completion = "FiringComplete";

    [Fact]
    public async Task N2_one_transient_cache_error_while_completing_does_not_leave_a_non_concurrent_job_blocked()
    {
        var (store, control, log, t2, bundle) = await FireNonConcurrentJobAsync();

        var jobKey = NCacheKeys.Job(_prefix, SchedulerName, "g", "j");
        control.FailWhen = FaultInjectingCache.Times(1, FaultInjectingCache.Call("Get", k => k == jobKey));

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.True(log.Contains(string.Format(RetryLogFormat, Completion, 1)), Dump(log));
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(t2.Key));
        Assert.Empty(await Storage.GetFiredTriggersForJobAsync("g", "j"));
        Assert.Empty(await Storage.GetFiredTriggersForInstanceAsync("instance-1"));

        Assert.Equal(t2.Key, Assert.Single(await store.AcquireNextTriggers(ScenarioKit.DueNow(5))).Key);
    }

    [Fact]
    public async Task N2_only_the_first_failed_completion_attempt_runs_the_registry_repair()
    {
        var (store, control, log, t2, bundle) = await FireNonConcurrentJobAsync();

        var jobKey = NCacheKeys.Job(_prefix, SchedulerName, "g", "j");
        control.FailWhen = FaultInjectingCache.Call("Get", k => k == jobKey);

        var completing = store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction).AsTask();

        Assert.True(await ScenarioKit.WaitUntilAsync(() => Task.FromResult(log.Contains(string.Format(RetryLogFormat, Completion, 2))), TimeSpan.FromSeconds(90)), Dump(log));
        control.FailWhen = null;
        await completing;

        Assert.Equal(1, log.Lines.Count(l => l.Contains("rebuilding registries and index while still holding", StringComparison.Ordinal)));
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(t2.Key));
    }

    [Fact]
    public async Task C7_a_completion_that_keeps_failing_retries_until_the_store_shuts_down()
    {
        var (store, control, log, t2, bundle) = await FireNonConcurrentJobAsync();

        var jobKey = NCacheKeys.Job(_prefix, SchedulerName, "g", "j");
        control.FailWhen = FaultInjectingCache.Call("Get", k => k == jobKey);

        var completing = store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction).AsTask();

        Assert.True(await ScenarioKit.WaitUntilAsync(() => Task.FromResult(log.Contains(string.Format(RetryLogFormat, Completion, 3))), TimeSpan.FromSeconds(90)), Dump(log));
        Assert.False(completing.IsCompleted);

        await store.Shutdown();

        var finished = await Task.WhenAny(completing, Task.Delay(TimeSpan.FromSeconds(10))) == completing;
        control.FailWhen = null;

        Assert.True(finished);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => completing);
        Assert.Contains("Simulated cache failure", ex.Message);
    }

    [Fact]
    public async Task C7_a_completion_that_keeps_failing_stops_when_the_caller_cancels()
    {
        var (store, control, log, _, bundle) = await FireNonConcurrentJobAsync();

        var jobKey = NCacheKeys.Job(_prefix, SchedulerName, "g", "j");
        control.FailWhen = FaultInjectingCache.Call("Get", k => k == jobKey);

        using var cts = new CancellationTokenSource();
        var completing = store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction, cts.Token).AsTask();

        Assert.True(await ScenarioKit.WaitUntilAsync(() => Task.FromResult(log.Contains(string.Format(RetryLogFormat, Completion, 2))), TimeSpan.FromSeconds(90)), Dump(log));
        cts.Cancel();

        var finished = await Task.WhenAny(completing, Task.Delay(TimeSpan.FromSeconds(10))) == completing;
        control.FailWhen = null;

        Assert.True(finished);
        Assert.True(completing.IsFaulted || completing.IsCanceled);
    }

    [Fact]
    public async Task N2_a_release_that_fails_once_is_retried_and_releases_the_trigger_and_its_blocked_sibling()
    {
        var (store, control, log, acquired, sibling) = await AcquireOneOfTwoNonConcurrentAsync();
        var triggerKey = NCacheKeys.Trigger(_prefix, SchedulerName, acquired.Key.Group, acquired.Key.Name);

        control.FailWhen = FaultInjectingCache.Times(1, FaultInjectingCache.Call("Get", k => k == triggerKey));

        await store.ReleaseAcquiredTrigger(acquired);

        Assert.True(log.Contains(string.Format(RetryLogFormat, "ReleaseAcquiredTrigger", 1)), Dump(log));
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync(acquired.Key.Group, acquired.Key.Name))!.TriggerState);
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(sibling));
        Assert.Empty(await Storage.GetFiredTriggersForInstanceAsync("instance-1"));
    }

    [Fact]
    public async Task N2_a_release_whose_record_removal_reached_the_cache_but_whose_reply_was_lost_still_releases_the_row()
    {
        var (store, control, log, acquired, sibling) = await AcquireOneOfTwoNonConcurrentAsync();
        var firedKey = NCacheKeys.FiredTrigger(_prefix, SchedulerName, acquired.FireInstanceId!);

        var armed = 1;
        control.FailWhen = (method, args) =>
        {
            if (method.Name == "Remove" && args?[0] is string key && key == firedKey && Interlocked.Exchange(ref armed, 0) == 1)
            {
                control.Inner.Remove(firedKey);
                return true;
            }

            return false;
        };

        await store.ReleaseAcquiredTrigger(acquired);

        Assert.True(log.Contains(string.Format(RetryLogFormat, "ReleaseAcquiredTrigger", 1)), Dump(log));
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync(acquired.Key.Group, acquired.Key.Name))!.TriggerState);
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(sibling));
    }

    [Fact]
    public async Task N2_a_release_whose_record_another_node_already_took_over_leaves_the_row_alone()
    {
        var (store, _, _, acquired, _) = await AcquireOneOfTwoNonConcurrentAsync();

        await Storage.RemoveFiredTriggerAsync(acquired.FireInstanceId!);
        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = "node-2-entry",
            TriggerName = acquired.Key.Name,
            TriggerGroup = acquired.Key.Group,
            InstanceName = "node-2",
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = DateTimeOffset.UtcNow,
            State = FiredTriggerStates.Acquired,
            JobName = "j",
            JobGroup = "g",
            IsNonConcurrent = true
        });

        await store.ReleaseAcquiredTrigger(acquired);

        Assert.Equal(TriggerStates.Acquired, (await Storage.GetTriggerAsync(acquired.Key.Group, acquired.Key.Name))!.TriggerState);
        Assert.NotNull(await Storage.GetFiredTriggerAsync("node-2-entry"));
    }

    [Fact]
    public async Task N2_a_release_that_keeps_failing_gives_up_after_a_bounded_number_of_attempts_and_the_start_up_sweep_releases_the_row()
    {
        var (store, control, log, acquired, sibling) = await AcquireOneOfTwoNonConcurrentAsync();
        var triggerKey = NCacheKeys.Trigger(_prefix, SchedulerName, acquired.Key.Group, acquired.Key.Name);
        control.FailWhen = FaultInjectingCache.Call("Get", k => k == triggerKey);

        var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.ReleaseAcquiredTrigger(acquired));
        elapsed.Stop();
        control.FailWhen = null;

        Assert.Equal(3, log.Lines.Count(l => l.Contains("ReleaseAcquiredTrigger failed (attempt", StringComparison.Ordinal)));
        Assert.False(log.Contains(string.Format(RetryLogFormat, "ReleaseAcquiredTrigger", 4)), Dump(log));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20), $"release took {elapsed.Elapsed}");

        Assert.Equal(TriggerStates.Acquired, (await Storage.GetTriggerAsync(acquired.Key.Group, acquired.Key.Name))!.TriggerState);
        Assert.Empty(await Storage.GetFiredTriggersForInstanceAsync("instance-1"));

        var sweepLog = new CapturingLoggerProvider();
        var restarted = await ScenarioKit.CreateStoreAsync(_cache!, _prefix, SchedulerName, "instance-1", sweepLog);

        try
        {
            await restarted.SchedulerStarted();

            Assert.True(sweepLog.Contains("left ACQUIRED without a firing record"), Dump(sweepLog));
            Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync(acquired.Key.Group, acquired.Key.Name))!.TriggerState);
            Assert.Contains(acquired.Key, (await restarted.AcquireNextTriggers(ScenarioKit.DueNow(5))).Select(t => t.Key));
        }
        finally
        {
            await restarted.Shutdown();
        }
    }

    private async Task<(NCacheJobStore Store, FaultInjectingCache Control, CapturingLoggerProvider Log, IOperableTrigger T2, TriggerFiredBundle Bundle)> FireNonConcurrentJobAsync()
    {
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var log = new CapturingLoggerProvider();
        var store = await ScenarioKit.CreateStoreAsync(cache, _prefix, SchedulerName, "instance-1", log);

        var job = ScenarioKit.Job("j", "g", nonConcurrent: true);
        var now = DateTimeOffset.UtcNow;
        var t2 = ScenarioKit.Repeating("t2", "g", job.Key, now.AddSeconds(2), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, ScenarioKit.Repeating("t1", "g", job.Key, now.AddSeconds(-1), TimeSpan.FromHours(1)));
        await store.AddTrigger(t2);

        var bundle = await ScenarioKit.FireOneAsync(store);
        Assert.Equal("t1", bundle.Trigger.Key.Name);
        Assert.Equal(TriggerState.Blocked, await store.GetTriggerState(t2.Key));

        return (store, control, log, t2, bundle);
    }

    private async Task<(NCacheJobStore Store, FaultInjectingCache Control, CapturingLoggerProvider Log, IOperableTrigger Acquired, TriggerKey Sibling)> AcquireOneOfTwoNonConcurrentAsync()
    {
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var log = new CapturingLoggerProvider();
        var store = await ScenarioKit.CreateStoreAsync(cache, _prefix, SchedulerName, "instance-1", log);

        var job = ScenarioKit.Job("j", "g", nonConcurrent: true);
        var now = DateTimeOffset.UtcNow;
        await store.ScheduleJob(job, ScenarioKit.Repeating("t1", "g", job.Key, now.AddSeconds(-2), TimeSpan.FromHours(1)));
        await store.AddTrigger(ScenarioKit.Repeating("t2", "g", job.Key, now.AddSeconds(-1), TimeSpan.FromHours(1)));

        var acquired = Assert.Single(await store.AcquireNextTriggers(ScenarioKit.DueNow(2)));
        var sibling = new TriggerKey(acquired.Key.Name == "t1" ? "t2" : "t1", "g");
        Assert.Equal(TriggerState.Blocked, await store.GetTriggerState(sibling));

        return (store, control, log, acquired, sibling);
    }

    private static string Dump(CapturingLoggerProvider log) => string.Join(Environment.NewLine, log.Lines);
}

