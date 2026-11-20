using System.Collections.Concurrent;
using NCache.OSS.Quartz.Extensions;
using NCache.OSS.Quartz.Model;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzMisfireRetryListenerUnitTest : NCacheQuartzNodeScenarioTestBase
{
    private static IOperableTrigger IgnoringMisfires(string name, string group, IJobDetail job, DateTimeOffset at, TimeSpan every)
    {
        var trigger = (IOperableTrigger) TriggerBuilder.Create()
            .WithIdentity(name, group)
            .ForJob(job)
            .StartAt(at)
            .WithSimpleSchedule(x => x.WithInterval(every).RepeatForever().WithMisfireInstruction(SimpleTriggerMisfireInstruction.IgnoreMisfires))
            .Build();

        trigger.ComputeFirstFireTimeUtc(null);
        return trigger;
    }

    private static async Task<DateTimeOffset> CompleteWithRetryAsync(NCacheJobStore store, TriggerFiredBundle bundle)
    {
        var retryAt = DateTimeOffset.UtcNow.AddSeconds(5);
        bundle.Trigger.NextFireTimeUtc = retryAt;
        bundle.Trigger.RetryAttempt = 1;

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.RetryTrigger);

        return retryAt;
    }

    [Fact]
    public async Task N23_retry_stays_paused_when_its_job_group_was_paused_during_the_run()
    {
        var store = await NewStoreAsync("node-1");
        var job = QuartzJob("j", "reports");
        var trigger = Repeating("t", "triggers", job, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);
        var bundle = await FireOneAsync(store);

        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals("reports"));
        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));

        var retryAt = await CompleteWithRetryAsync(store, bundle);

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));

        var row = await Storage.GetTriggerAsync("triggers", "t");
        Assert.Equal(retryAt, row!.NextFireTimeUtc);
        Assert.Equal(1, row.RetryAttempt);

        await store.ResumeJobGroups(GroupMatcher<JobKey>.GroupEquals("reports"));

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));
        row = await Storage.GetTriggerAsync("triggers", "t");
        Assert.Equal(retryAt, row!.NextFireTimeUtc);
        Assert.Equal(1, row.RetryAttempt);
    }

    [Fact]
    public async Task N23_retry_stays_paused_when_its_trigger_group_was_paused_during_the_run()
    {
        var store = await NewStoreAsync("node-1");
        var job = QuartzJob("j", "reports");
        var trigger = Repeating("t", "triggers", job, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);
        var bundle = await FireOneAsync(store);

        await store.PauseTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals("triggers"));
        await CompleteWithRetryAsync(store, bundle);

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
    }

    [Fact]
    public async Task N23_retry_stays_paused_after_PauseAll_during_the_run()
    {
        var store = await NewStoreAsync("node-1");
        var job = QuartzJob("j", "reports");
        var trigger = Repeating("t", "triggers", job, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);
        var bundle = await FireOneAsync(store);

        await store.PauseAll();
        await CompleteWithRetryAsync(store, bundle);

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
    }

    [Fact]
    public async Task N23_retry_with_nothing_paused_is_scheduled_and_acquired_at_its_retry_time()
    {
        var store = await NewStoreAsync("node-1");
        var job = QuartzJob("j", "reports");
        var trigger = Repeating("t", "triggers", job, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);
        var bundle = await FireOneAsync(store);

        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals("other"));

        var retryAt = await CompleteWithRetryAsync(store, bundle);

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));

        var row = await Storage.GetTriggerAsync("triggers", "t");
        Assert.Equal(TriggerStates.Waiting, row!.TriggerState);
        Assert.Equal(retryAt, row.NextFireTimeUtc);
        Assert.Equal(1, row.RetryAttempt);

        var acquired = await store.AcquireNextTriggers(CreateRequest(retryAt.AddSeconds(1), 1));
        Assert.Equal(retryAt, Assert.Single(acquired).NextFireTimeUtc);
    }

    [Fact]
    public async Task N24_ignore_misfire_trigger_keeps_its_retry_attempt_and_fire_time_when_resumed_late()
    {
        var store = await NewStoreAsync("node-1");
        var job = QuartzJob("j");
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var trigger = IgnoringMisfires("t", "tests", job, start, TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);

        var row = await Storage.GetTriggerAsync("tests", "t");
        row!.RetryAttempt = 1;
        await Storage.UpdateTriggerAsync(row);

        await store.PauseTrigger(trigger.Key);
        Assert.True(await store.ResumeTrigger(trigger.Key));

        row = await Storage.GetTriggerAsync("tests", "t");
        Assert.Equal(1, row!.RetryAttempt);
        Assert.Equal(start, row.NextFireTimeUtc);
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));
    }

    [Fact]
    public async Task N24_trigger_with_a_misfire_policy_is_still_misfired_when_resumed_late()
    {
        var store = await NewStoreAsync("node-1");
        var job = QuartzJob("j");
        var trigger = Repeating("t", "tests", job, DateTimeOffset.UtcNow.AddMinutes(-5), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);

        var row = await Storage.GetTriggerAsync("tests", "t");
        row!.RetryAttempt = 1;
        await Storage.UpdateTriggerAsync(row);

        await store.PauseTrigger(trigger.Key);
        await store.ResumeTrigger(trigger.Key);

        row = await Storage.GetTriggerAsync("tests", "t");
        Assert.Equal(0, row!.RetryAttempt);
        Assert.True(row.NextFireTimeUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task C11_overdue_ignore_misfire_trigger_is_acquired_at_once_for_its_missed_fire_time()
    {
        var store = await NewStoreAsync("node-1");
        var job = QuartzJob("j");
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        await store.ScheduleJob(job, IgnoringMisfires("t", "tests", job, start, TimeSpan.FromHours(1)));

        var acquired = await AcquireAsync(store);

        Assert.Equal(start, Assert.Single(acquired).NextFireTimeUtc);

        var fired = await store.TriggersFired(acquired);
        var bundle = Assert.Single(fired).TriggerFiredBundle;
        Assert.NotNull(bundle);
        Assert.Equal(start, bundle.ScheduledFireTimeUtc);
        Assert.Equal(start.AddHours(1), bundle.NextFireTimeUtc);
    }

    [Fact]
    public async Task N24_unblocking_late_keeps_an_ignore_misfire_sibling_due_but_misfires_the_others()
    {
        var store = await NewStoreAsync("node-1");
        var job = QuartzJob("nc", nonConcurrent: true);
        await store.ScheduleJob(job, Repeating("running", "tests", job, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1)));
        var bundle = await FireOneAsync(store);

        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        await store.AddTrigger(IgnoringMisfires("ignoring", "tests", job, start, TimeSpan.FromHours(1)));
        await store.AddTrigger(Repeating("smart", "tests", job, start, TimeSpan.FromHours(1)));
        Assert.Equal(TriggerStates.Blocked, (await Storage.GetTriggerAsync("tests", "ignoring"))!.TriggerState);

        var ignoring = await Storage.GetTriggerAsync("tests", "ignoring");
        ignoring!.RetryAttempt = 1;
        await Storage.UpdateTriggerAsync(ignoring);

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);

        ignoring = await Storage.GetTriggerAsync("tests", "ignoring");
        Assert.Equal(TriggerStates.Waiting, ignoring!.TriggerState);
        Assert.Equal(start, ignoring.NextFireTimeUtc);
        Assert.Equal(1, ignoring.RetryAttempt);

        var smart = await Storage.GetTriggerAsync("tests", "smart");
        Assert.Equal(TriggerStates.Waiting, smart!.TriggerState);
        Assert.True(smart.NextFireTimeUtc > DateTimeOffset.UtcNow);
    }

    private sealed class RecordingSignaler : ISchedulerSignaler
    {
        public ConcurrentQueue<string> Events { get; } = new();

        public Func<ITrigger, ValueTask>? OnMisfired { get; set; }

        public ValueTask NotifyTriggerListenersMisfired(ITrigger trigger, CancellationToken cancellationToken = default)
        {
            Events.Enqueue($"misfired:{trigger.Key}");
            return OnMisfired?.Invoke(trigger) ?? default;
        }

        public ValueTask NotifySchedulerListenersFinalized(ITrigger trigger, CancellationToken cancellationToken = default)
        {
            Events.Enqueue($"finalized:{trigger.Key}");
            return default;
        }

        public ValueTask NotifySchedulerListenersJobDeleted(JobKey jobKey, CancellationToken cancellationToken = default)
        {
            Events.Enqueue($"job-deleted:{jobKey}");
            return default;
        }

        public ValueTask NotifySchedulerListenersTriggerInError(TriggerKey triggerKey, CancellationToken cancellationToken = default)
        {
            Events.Enqueue($"trigger-in-error:{triggerKey}");
            return default;
        }

        public ValueTask NotifySchedulerListenersTriggersInError(JobKey jobKey, CancellationToken cancellationToken = default)
        {
            Events.Enqueue($"triggers-in-error:{jobKey}");
            return default;
        }

        public ValueTask SignalSchedulingChange(DateTimeOffset? candidateNewNextFireTimeUtc, CancellationToken cancellationToken = default)
            => default;

        public ValueTask NotifySchedulerListenersError(SchedulerErrorContext errorContext, CancellationToken cancellationToken = default)
        {
            Events.Enqueue("scheduler-error");
            return default;
        }
    }

    [Fact]
    public async Task N25_acquisition_that_reschedules_a_misfire_tells_the_trigger_listeners_once()
    {
        var signaler = new RecordingSignaler();
        var store = await NewStoreAsync("node-1", signaler: signaler);
        var job = QuartzJob("j");
        var trigger = Repeating("t", "tests", job, DateTimeOffset.UtcNow.AddMinutes(-5), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);

        var acquired = await AcquireAsync(store);

        Assert.Empty(acquired);
        Assert.Equal(new[] { $"misfired:{trigger.Key}" }, signaler.Events.ToArray());

        await AcquireAsync(store);
        Assert.Single(signaler.Events);
    }

    [Fact]
    public async Task N25_misfire_that_leaves_nothing_to_fire_also_tells_scheduler_listeners_it_is_finalized()
    {
        var signaler = new RecordingSignaler();
        var store = await NewStoreAsync("node-1", signaler: signaler);
        var job = QuartzJob("j");

        var trigger = (IOperableTrigger) TriggerBuilder.Create()
            .WithIdentity("t", "tests")
            .ForJob(job)
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(-5))
            .WithSimpleSchedule(x => x.WithRepeatCount(0).WithMisfireInstruction(SimpleTriggerMisfireInstruction.NextWithRemainingCount))
            .Build();
        trigger.ComputeFirstFireTimeUtc(null);
        await store.ScheduleJob(job, trigger);

        Assert.Empty(await AcquireAsync(store));

        Assert.Equal(new[] { $"misfired:{trigger.Key}", $"finalized:{trigger.Key}" }, signaler.Events.ToArray());
    }

    [Fact]
    public async Task N25_ignore_misfire_trigger_raises_no_misfire_notification()
    {
        var signaler = new RecordingSignaler();
        var store = await NewStoreAsync("node-1", signaler: signaler);
        var job = QuartzJob("j");
        await store.ScheduleJob(job, IgnoringMisfires("t", "tests", job, DateTimeOffset.UtcNow.AddMinutes(-5), TimeSpan.FromHours(1)));

        Assert.Single(await AcquireAsync(store));
        Assert.Empty(signaler.Events);
    }

    [Fact]
    public async Task N25_misfire_on_resume_is_raised_after_the_locks_are_released_so_a_listener_can_call_back()
    {
        var signaler = new RecordingSignaler();
        var store = await NewStoreAsync("node-1", signaler: signaler);
        store.LockAcquireTimeout = TimeSpan.FromSeconds(5);

        var job = QuartzJob("j");
        var trigger = Repeating("t", "tests", job, DateTimeOffset.UtcNow.AddMinutes(-5), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);
        await store.PauseTrigger(trigger.Key);

        Exception? callbackFailure = null;
        signaler.OnMisfired = async misfired =>
        {
            try
            {
                await store.PauseTrigger(misfired.Key);
            }
            catch (Exception ex)
            {
                callbackFailure = ex;
            }
        };

        Assert.True(await store.ResumeTrigger(trigger.Key));

        Assert.Null(callbackFailure);
        Assert.Equal(new[] { $"misfired:{trigger.Key}" }, signaler.Events.ToArray());
        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
    }

    [Fact]
    public async Task N25_listener_that_throws_does_not_fail_the_operation_that_notified_it()
    {
        var logs = new CapturingLoggerProvider();
        var signaler = new RecordingSignaler { OnMisfired = _ => throw new InvalidOperationException("listener broke") };
        var store = await NewStoreAsync("node-1", signaler: signaler, logger: logs.CreateLogger<NCacheJobStore>());

        var job = QuartzJob("j");
        var trigger = Repeating("t", "tests", job, DateTimeOffset.UtcNow.AddMinutes(-5), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);
        await store.PauseTrigger(trigger.Key);

        Assert.True(await store.ResumeTrigger(trigger.Key));

        Assert.Single(signaler.Events);
        Assert.True(logs.Contains("A Quartz listener failed"));
        Assert.True((await Storage.GetTriggerAsync("tests", "t"))!.NextFireTimeUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task N25_completion_with_error_instructions_tells_scheduler_listeners()
    {
        var signaler = new RecordingSignaler();
        var store = await NewStoreAsync("node-1", signaler: signaler);
        var job = QuartzJob("j");
        var now = DateTimeOffset.UtcNow;
        var t1 = Repeating("t1", "tests", job, now.AddSeconds(-2), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, t1);
        await store.AddTrigger(Repeating("t2", "tests", job, now.AddSeconds(-1), TimeSpan.FromHours(1)));

        var first = await FireOneAsync(store);
        await store.TriggeredJobComplete(first.Trigger, first.JobDetail, SchedulerInstruction.SetTriggerError);

        Assert.Equal(new[] { $"trigger-in-error:{t1.Key}" }, signaler.Events.ToArray());

        var second = await FireOneAsync(store);
        await store.TriggeredJobComplete(second.Trigger, second.JobDetail, SchedulerInstruction.SetAllJobTriggersError);

        Assert.Equal($"triggers-in-error:{job.Key}", signaler.Events.Last());
        Assert.Equal(TriggerState.Error, await store.GetTriggerState(new TriggerKey("t2", "tests")));
    }

    private sealed class CountingTriggerListener : ITriggerListener
    {
        private int _misfired;

        public int Misfired => Volatile.Read(ref _misfired);

        public string Name => "counting-listener";

        public ValueTask TriggerMisfired(ITrigger trigger, IScheduler scheduler, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _misfired);
            return default;
        }
    }

    [Fact]
    public async Task N25_a_real_scheduler_reports_the_store_handled_misfire_to_its_trigger_listeners()
    {
        var schedulerName = $"r2b-n25-{Guid.NewGuid():N}";

        var scheduler = await QuartzSchedulerBuilder
            .Create(q => q
                .UseNCacheStore(new NCacheJobStoreOptions
                {
                    CacheName = TestCache.CacheName,
                    SchedulerName = schedulerName,
                    InstanceId = "n25",
                    KeyPrefix = _prefix,
                    RepairRegistriesOnStart = false
                })
                .UseDefaultThreadPool(1))
            .BuildScheduler();

        var listener = new CountingTriggerListener();
        scheduler.ListenerManager.AddTriggerListener(listener);

        try
        {
            var job = JobBuilder.Create<TestJob>().WithIdentity("j", "g").Build();
            var trigger = TriggerBuilder.Create()
                .WithIdentity("t", "g")
                .StartAt(DateTimeOffset.UtcNow.AddMinutes(-5))
                .WithSimpleSchedule(x => x.WithRepeatCount(0))
                .Build();

            await scheduler.ScheduleJob(job, trigger);
            await scheduler.Start();

            var deadline = DateTime.UtcNow.AddSeconds(20);

            while (listener.Misfired == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Assert.Equal(1, listener.Misfired);
        }
        finally
        {
            try
            {
                await scheduler.Shutdown(waitForJobsToComplete: false);
            }
            catch (SchedulerException)
            {
            }
        }
    }
}

