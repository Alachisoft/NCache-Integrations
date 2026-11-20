using NCache.OSS.Quartz.Model;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzStorePausedUnitTest : NCacheQuartzScenarioTestBase
{
    private static readonly AddTriggerOptions StoredPaused = new() { PauseReason = "maintenance window", PauseRequestedBy = "ops@example" };

    [Fact]
    public async Task The_store_tells_Quartz_it_stores_triggers_paused()
    {
        IJobStore store = await NewNodeAsync();

        Assert.True(store.SupportsStoringPaused);
    }

    [Fact]
    public async Task AddTrigger_stored_paused_is_never_acquirable_and_carries_its_record()
    {
        var store = await NewNodeAsync();
        var job = Job("job");
        await store.AddJob(job);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        await store.AddTrigger(trigger, StoredPaused);

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
        var pause = (await store.GetTriggerPause(trigger.Key))!;
        Assert.Equal("maintenance window", pause.Reason);
        Assert.Equal("ops@example", pause.RequestedBy);
        Assert.InRange(pause.PausedAtUtc, before, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Empty(await AcquireDueAsync(store));
    }

    [Fact]
    public async Task A_reason_alone_stores_the_trigger_paused()
    {
        var store = await NewNodeAsync();
        var job = Job("job");
        await store.AddJob(job);

        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        await store.AddTrigger(trigger, new AddTriggerOptions { PauseReason = "why" });

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
        var pause = (await store.GetTriggerPause(trigger.Key))!;
        Assert.Equal("why", pause.Reason);
        Assert.Null(pause.RequestedBy);
    }

    [Fact]
    public async Task Paused_without_details_stores_it_paused_with_no_record()
    {
        var store = await NewNodeAsync();
        var job = Job("job");
        await store.AddJob(job);

        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        await store.AddTrigger(trigger, new AddTriggerOptions { Paused = true });

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
        Assert.Null(await store.GetTriggerPause(trigger.Key));
        Assert.Empty(await AcquireDueAsync(store));
    }

    [Fact]
    public async Task AddTrigger_without_Paused_still_stores_it_waiting()
    {
        var store = await NewNodeAsync();
        var job = Job("job");
        await store.AddJob(job);

        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        await store.AddTrigger(trigger);

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));
        Assert.Single(await AcquireDueAsync(store));
    }

    [Fact]
    public async Task Storing_paused_over_a_paused_trigger_replaces_its_record()
    {
        var store = await NewNodeAsync();
        var job = Job("job");
        await store.AddJob(job);
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        await store.AddTrigger(trigger);
        await store.PauseTriggerWith(trigger.Key, new PauseDetails { Reason = "first" });

        var replacement = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        await store.AddTrigger(replacement, StoredPaused with { Replace = true });

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
        Assert.Equal("maintenance window", (await store.GetTriggerPause(trigger.Key))!.Reason);
    }

    [Fact]
    public async Task Replacing_a_paused_trigger_without_Paused_keeps_the_record_it_had()
    {
        var store = await NewNodeAsync();
        var job = Job("job");
        await store.AddJob(job);
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        await store.AddTrigger(trigger);
        await store.PauseTriggerWith(trigger.Key, new PauseDetails { Reason = "first" });

        var replacement = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        await store.AddTrigger(replacement, AddTriggerOptions.Replacing);

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
        Assert.Equal("first", (await store.GetTriggerPause(trigger.Key))!.Reason);
    }

    [Fact]
    public async Task A_trigger_stored_paused_while_its_non_concurrent_job_runs_is_paused_and_blocked()
    {
        var store = await NewNodeAsync();
        var job = Job("job", nonConcurrent: true);
        await store.ScheduleJob(job, OneShot("running", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2)));
        await FireOneAsync(store);

        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        await store.AddTrigger(trigger, StoredPaused);

        var stored = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal(TriggerStates.PausedAndBlocked, stored.TriggerState);
        Assert.Equal("maintenance window", stored.Pause!.Reason);
    }

    [Fact]
    public async Task ScheduleJobs_stored_paused_pauses_every_trigger_with_the_same_record()
    {
        var store = await NewNodeAsync();
        var jobA = Job("job-a");
        var jobB = Job("job-b");
        var a1 = OneShot("a1", "tests", jobA.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        var a2 = OneShot("a2", "tests", jobA.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        var b1 = OneShot("b1", "tests", jobB.Key, DateTimeOffset.UtcNow.AddSeconds(-2));

        await store.ScheduleJobs(
            new Dictionary<IJobDetail, IReadOnlyCollection<IOperableTrigger>>
            {
                [jobA] = new[] { a1, a2 },
                [jobB] = new[] { b1 }
            },
            new ScheduleJobOptions { PauseReason = "maintenance window", PauseRequestedBy = "ops@example" });

        var pauses = new List<PauseInfo>();

        foreach (var trigger in new[] { a1, a2, b1 })
        {
            Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
            pauses.Add((await store.GetTriggerPause(trigger.Key))!);
        }

        Assert.All(pauses, p => Assert.Equal("maintenance window", p.Reason));
        Assert.Single(pauses.Select(p => p.PausedAtUtc).Distinct());
        Assert.Empty(await AcquireDueAsync(store, maxCount: 3));
    }

    [Fact]
    public async Task PauseTriggersWith_pauses_the_set_with_one_record_and_skips_missing_and_already_paused_triggers()
    {
        var store = await NewNodeAsync();
        var job = Job("job");
        await store.AddJob(job);
        var a = OneShot("a", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        var b = OneShot("b", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        var earlier = OneShot("earlier", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        await store.AddTrigger(a);
        await store.AddTrigger(b);
        await store.AddTrigger(earlier);
        await store.PauseTriggerWith(earlier.Key, new PauseDetails { Reason = "first" });
        var missing = new TriggerKey("missing", "tests");

        var paused = await store.PauseTriggersWith(new[] { a.Key, b.Key, earlier.Key, missing }, new PauseDetails { Reason = "batch", RequestedBy = "ops@example" });

        Assert.Equal(new[] { a.Key, b.Key }, paused);
        var pauseA = (await store.GetTriggerPause(a.Key))!;
        var pauseB = (await store.GetTriggerPause(b.Key))!;
        Assert.Equal("batch", pauseA.Reason);
        Assert.Equal("ops@example", pauseB.RequestedBy);
        Assert.Equal(pauseA.PausedAtUtc, pauseB.PausedAtUtc);
        Assert.Equal("first", (await store.GetTriggerPause(earlier.Key))!.Reason);
        Assert.Empty(await AcquireDueAsync(store, maxCount: 3));
    }

    [Fact]
    public async Task PauseJobsWith_pauses_every_trigger_of_the_jobs_with_one_record_and_skips_missing_jobs()
    {
        var store = await NewNodeAsync();
        var jobA = Job("job-a");
        var jobB = Job("job-b");
        var a1 = OneShot("a1", "tests", jobA.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        var a2 = OneShot("a2", "tests", jobA.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        var b1 = OneShot("b1", "tests", jobB.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        await store.ScheduleJob(jobA, a1);
        await store.AddTrigger(a2);
        await store.ScheduleJob(jobB, b1);
        var missing = new JobKey("missing", "tests");

        var paused = await store.PauseJobsWith(new[] { jobA.Key, jobB.Key, missing }, new PauseDetails { Reason = "batch" });

        Assert.Equal(new[] { jobA.Key, jobB.Key }, paused);
        var pauses = new List<PauseInfo>();

        foreach (var trigger in new[] { a1, a2, b1 })
        {
            Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
            pauses.Add((await store.GetTriggerPause(trigger.Key))!);
        }

        Assert.All(pauses, p => Assert.Equal("batch", p.Reason));
        Assert.Single(pauses.Select(p => p.PausedAtUtc).Distinct());
    }

    [Fact]
    public async Task PauseTriggersWith_without_details_pauses_with_no_record()
    {
        var store = await NewNodeAsync();
        var job = Job("job");
        await store.AddJob(job);
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2));
        await store.AddTrigger(trigger);

        Assert.Equal(new[] { trigger.Key }, await store.PauseTriggersWith(new[] { trigger.Key }, details: null));

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
        Assert.Null(await store.GetTriggerPause(trigger.Key));
    }
}
