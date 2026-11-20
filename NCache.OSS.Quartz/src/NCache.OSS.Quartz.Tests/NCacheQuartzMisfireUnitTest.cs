using Alachisoft.NCache.Client;
using NCache.OSS.Quartz.Index;
using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Locking;
using NCache.OSS.Quartz.Model;
using NCache.OSS.Quartz.Serialization;
using NCache.OSS.Quartz.Storage;
using Quartz;
using Quartz.Extensibility;
using Quartz.Impl.Calendar;
using Quartz.Impl.Triggers;
using Xunit;

namespace NCache.OSS.Quartz.Tests;
public sealed class NCacheQuartzMisfireUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task IsMisfired_returns_false_when_next_fire_time_is_missing()
    {
        var store = CreateStore("instance-1");
        store.MisfireThreshold = TimeSpan.FromSeconds(1);

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow);
        trigger.NextFireTimeUtc = null;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(trigger, replaceExisting: false);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Empty(acquired);
    }

    [Fact]
    public async Task IsMisfired_returns_false_when_trigger_is_within_threshold()
    {
        var store = CreateStore("instance-1");
        store.MisfireThreshold = TimeSpan.FromMinutes(1);

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Single(acquired);
    }

    [Fact]
    public async Task IsMisfired_returns_true_when_trigger_exceeds_threshold()
    {
        var store = CreateStore("instance-1");
        store.MisfireThreshold = TimeSpan.FromSeconds(1);

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-5));
        trigger.MisfireInstruction = 0;

        await StoreJobAndTriggerAsync(job, trigger);

        var before = trigger.NextFireTimeUtc;

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Single(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.NotEqual(before, stored.NextFireTimeUtc);
    }

    [Fact]
    public async Task AcquireNextTriggers_misfire_persists_the_whole_rewritten_schedule()
    {
        var store = CreateStore("instance-1");
        store.MisfireThreshold = TimeSpan.FromSeconds(1);

        var job = CreateJob("job-1");
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", start, repeatCount: 10);
        trigger.StartTimeUtc = start;
        trigger.Simple!.RepeatInterval = TimeSpan.FromMinutes(5);
        trigger.Simple.TimesTriggered = 2;
        trigger.MisfireInstruction = (int)SimpleTriggerMisfireInstruction.NowWithRemainingCount;

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = Assert.Single(await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddSeconds(1), 1)));
        var stored = (await Storage.GetTriggerAsync("tests", "trigger-1"))!;

        Assert.Equal(TriggerStates.Acquired, stored.TriggerState);
        Assert.True(stored.StartTimeUtc > start.AddMinutes(29), "start time was not moved to 'now' by the misfire policy");

        Assert.Equal(2, stored.Simple!.RepeatCount);
        Assert.Equal(0, stored.Simple.TimesTriggered);
        Assert.Equal(stored.StartTimeUtc, stored.NextFireTimeUtc);

        Assert.Equal(stored.Simple.RepeatCount, ((ISimpleTrigger)acquired).RepeatCount);
        Assert.Equal(stored.StartTimeUtc, acquired.StartTimeUtc);
    }

    [Fact]
    public async Task TriggersFired_persists_times_triggered_and_previous_fire_time()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 3);

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = Assert.Single(await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 1)));
        Assert.NotNull(Assert.Single(await store.TriggersFired(new[] { acquired })).TriggerFiredBundle);

        var stored = (await Storage.GetTriggerAsync("tests", "trigger-1"))!;

        Assert.Equal(1, stored.Simple!.TimesTriggered);
        Assert.Equal(trigger.NextFireTimeUtc, stored.PreviousFireTimeUtc);
        Assert.Equal(trigger.NextFireTimeUtc!.Value.AddSeconds(30), stored.NextFireTimeUtc);

        var reloaded = (ISimpleTrigger)(await store.GetTrigger(new TriggerKey("trigger-1", "tests")))!;
        Assert.Equal(1, reloaded.TimesTriggered);
    }

    [Fact]
    public async Task AcquireNextTriggers_processes_misfire_before_acquisition()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-10), repeatCount: 5);
        trigger.MisfireInstruction = 0;

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(5), 1));

        Assert.Single(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Acquired, stored.TriggerState);
    }

    [Fact]
    public async Task AcquireNextTriggers_skips_trigger_when_misfire_moves_it_beyond_cutoff()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-10), repeatCount: 5);
        trigger.MisfireInstruction = 0;

        await StoreJobAndTriggerAsync(job, trigger);

        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-1);

        var acquired = await store.AcquireNextTriggers(CreateRequest(cutoff, 1));

        Assert.Empty(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
        Assert.True(stored.NextFireTimeUtc > cutoff);
    }

    [Fact]
    public async Task AcquireNextTriggers_acquires_trigger_when_misfire_keeps_it_due()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-10), repeatCount: 5);
        trigger.MisfireInstruction = 0;

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(5), 1));

        Assert.Single(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Acquired, stored.TriggerState);
    }
     
    [Fact]
    public async Task AcquireNextTriggers_completes_trigger_when_misfire_removes_next_fire_time()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-10), repeatCount: 1);
        trigger.MisfireInstruction = (int)SimpleTriggerMisfireInstruction.NowWithExistingCount;
        trigger.EndTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-1);

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(5), 1));

        Assert.Empty(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Complete, stored.TriggerState);
        Assert.Null(stored.NextFireTimeUtc);
    }
    [Fact]
    public async Task ResumeTrigger_applies_misfire_when_resuming_paused_trigger()
    {
        var store = CreateStore("instance-1");
        store.MisfireThreshold = TimeSpan.FromSeconds(1);

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-10));
        trigger.TriggerState = TriggerStates.Paused;
        trigger.MisfireInstruction = 0;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        var resumed = await store.ResumeTrigger(new TriggerKey("trigger-1", "tests"));

        Assert.True(resumed);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
        Assert.True(stored.NextFireTimeUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task ResumeTriggers_applies_misfire_when_resuming_paused_trigger()
    {
        var store = CreateStore("instance-1");
        store.MisfireThreshold = TimeSpan.FromSeconds(1);

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-10));
        trigger.TriggerState = TriggerStates.Paused;
        trigger.MisfireInstruction = 0;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        var resumed = await store.ResumeTriggers(new[] { new TriggerKey("trigger-1", "tests") });

        Assert.Single(resumed);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
        Assert.True(stored.NextFireTimeUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task ResumeTriggerGroups_applies_misfire_when_resuming_group()
    {
        var store = CreateStore("instance-1");
        store.MisfireThreshold = TimeSpan.FromSeconds(1);

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-10));
        trigger.TriggerState = TriggerStates.Paused;
        trigger.MisfireInstruction = 0;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);
        await Storage.AddPausedTriggerGroupAsync("tests");

        var resumed = await store.ResumeTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals("tests"));

        Assert.Contains("tests", resumed);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
        Assert.True(stored.NextFireTimeUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task ResumeJob_applies_misfire_when_resuming_job()
    {
        var store = CreateStore("instance-1");
        store.MisfireThreshold = TimeSpan.FromSeconds(1);

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-10));
        trigger.TriggerState = TriggerStates.Paused;
        trigger.MisfireInstruction = 0;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        var resumed = await store.ResumeJob(new JobKey("job-1", "tests"));

        Assert.True(resumed);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
        Assert.True(stored.NextFireTimeUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task ResumeJobs_applies_misfire_when_resuming_jobs()
    {
        var store = CreateStore("instance-1");
        store.MisfireThreshold = TimeSpan.FromSeconds(1);

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-10));
        trigger.TriggerState = TriggerStates.Paused;
        trigger.MisfireInstruction = 0;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        var resumed = await store.ResumeJobs(new[] { new JobKey("job-1", "tests") });

        Assert.Single(resumed);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
        Assert.True(stored.NextFireTimeUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task ResumeJobGroups_applies_misfire_when_resuming_group()
    {
        var store = CreateStore("instance-1");
        store.MisfireThreshold = TimeSpan.FromSeconds(1);

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-10));
        trigger.TriggerState = TriggerStates.Paused;
        trigger.MisfireInstruction = 0;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);
        await Storage.AddPausedJobGroupAsync("tests");

        var resumed = await store.ResumeJobGroups(GroupMatcher<JobKey>.GroupEquals("tests"));

        Assert.Contains("tests", resumed);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
        Assert.True(stored.NextFireTimeUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task ResumeAll_applies_misfire_when_resuming_all()
    {
        var store = CreateStore("instance-1");
        store.MisfireThreshold = TimeSpan.FromSeconds(1);

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-10));
        trigger.TriggerState = TriggerStates.Paused;
        trigger.MisfireInstruction = 0;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);
        await Storage.AddPausedTriggerGroupAsync("tests");

        await store.ResumeAll();

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
        Assert.True(stored.NextFireTimeUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }
}

