using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzLastFiringCompletionUnitTest : NCacheQuartzScenarioTestBase
{
    [Fact]
    public async Task B2_DeleteTrigger_completion_keeps_a_trigger_the_job_rescheduled_while_it_ran()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1", durable: false);
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        var bundle = await FireOneAsync(store);
        Assert.Null(bundle.Trigger.NextFireTimeUtc);

        var rescheduledAt = DateTimeOffset.UtcNow.AddHours(1);
        Assert.True(await store.ReplaceTrigger(trigger.Key, OneShot("t", "tests", job.Key, rescheduledAt)));

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.DeleteTrigger);

        var stored = await store.GetTrigger(trigger.Key);
        Assert.NotNull(stored);
        Assert.Equal(rescheduledAt, stored!.NextFireTimeUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));
        Assert.True(await store.Exists(job.Key));
    }

    [Fact]
    public async Task B2_the_rescheduled_trigger_is_still_acquired_when_its_new_time_comes()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1", durable: false);
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        var bundle = await FireOneAsync(store);
        await store.ReplaceTrigger(trigger.Key, OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(2)));
        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.DeleteTrigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddSeconds(10), 1));

        Assert.Equal(trigger.Key, Assert.Single(acquired).Key);
    }

    [Fact]
    public async Task B2_rescheduling_under_a_new_key_keeps_the_new_trigger_and_the_job()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1", durable: false);
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        var bundle = await FireOneAsync(store);
        var replacement = OneShot("t-next", "tests", job.Key, DateTimeOffset.UtcNow.AddHours(1));
        Assert.True(await store.ReplaceTrigger(trigger.Key, replacement));

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.DeleteTrigger);

        Assert.False(await store.Exists(trigger.Key));
        Assert.True(await store.Exists(replacement.Key));
        Assert.True(await store.Exists(job.Key));
    }

    [Fact]
    public async Task C2_DeleteTrigger_completion_still_deletes_a_spent_trigger_that_was_not_rescheduled_and_its_non_durable_job()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1", durable: false);
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        var bundle = await FireOneAsync(store);
        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.DeleteTrigger);

        Assert.Null(await store.GetTrigger(trigger.Key));
        Assert.Null(await store.GetJob(job.Key));
    }

    [Fact]
    public async Task C2_DeleteTrigger_completion_keeps_a_durable_job_after_deleting_its_spent_trigger()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1", durable: true);
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        var bundle = await FireOneAsync(store);
        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.DeleteTrigger);

        Assert.Null(await store.GetTrigger(trigger.Key));
        Assert.NotNull(await store.GetJob(job.Key));
    }

    [Fact]
    public async Task C2_DeleteTrigger_completion_keeps_a_non_durable_job_that_still_has_another_trigger()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1", durable: false);
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        var other = OneShot("other", "tests", job.Key, DateTimeOffset.UtcNow.AddHours(1));
        await store.ScheduleJob(job, trigger);
        await store.AddTrigger(other);

        var bundle = await FireOneAsync(store);
        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.DeleteTrigger);

        Assert.False(await store.Exists(trigger.Key));
        Assert.True(await store.Exists(other.Key));
        Assert.True(await store.Exists(job.Key));
    }

    [Fact]
    public async Task C2_DeleteTrigger_for_a_trigger_that_can_still_fire_deletes_it_without_the_reschedule_check()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1", durable: true);
        var trigger = Repeating("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);

        var bundle = await FireOneAsync(store);
        Assert.NotNull(bundle.Trigger.NextFireTimeUtc);

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.DeleteTrigger);

        Assert.Null(await store.GetTrigger(trigger.Key));
    }

    [Fact]
    public async Task C2_DeleteTrigger_for_a_trigger_already_unscheduled_while_the_job_ran_is_a_no_op()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1", durable: true);
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        var bundle = await FireOneAsync(store);
        Assert.True(await store.DeleteTrigger(trigger.Key));

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.DeleteTrigger);

        Assert.False(await store.Exists(trigger.Key));
        Assert.True(await store.Exists(job.Key));
    }

    [Fact]
    public async Task B5_a_last_firing_completed_with_NoInstruction_does_not_leave_a_COMPLETE_trigger_or_its_non_durable_job()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1", durable: false);
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        var bundle = await FireOneAsync(store);

        Assert.Equal(TriggerState.Complete, await store.GetTriggerState(trigger.Key));

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.None, await store.GetTriggerState(trigger.Key));
        Assert.Null(await store.GetJob(job.Key));
        Assert.Empty((await store.QueryTriggers(new TriggerQuery())).Items);
    }

    [Fact]
    public async Task B5_a_last_firing_completed_with_NoInstruction_keeps_a_durable_job()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1", durable: true);
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        var bundle = await FireOneAsync(store);
        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.None, await store.GetTriggerState(trigger.Key));
        Assert.NotNull(await store.GetJob(job.Key));
    }

    [Fact]
    public async Task B5_the_same_holds_on_the_direct_completion_path_of_a_firing_without_a_fire_instance_id()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1", durable: false);
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        var bundle = await FireOneAsync(store);
        bundle.Trigger.FireInstanceId = null!;

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.None, await store.GetTriggerState(trigger.Key));
        Assert.Null(await store.GetJob(job.Key));
    }

    [Fact]
    public async Task B5_a_last_firing_completed_with_NoInstruction_keeps_a_trigger_the_job_rescheduled_meanwhile()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1", durable: false);
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        var bundle = await FireOneAsync(store);
        await store.ReplaceTrigger(trigger.Key, OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddHours(1)));

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));
        Assert.True(await store.Exists(job.Key));
    }

    [Fact]
    public async Task C5_NoInstruction_completion_of_a_repeating_trigger_keeps_it_waiting_for_its_next_time()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1", durable: false);
        var trigger = (IOperableTrigger) TriggerBuilder.Create()
            .WithIdentity("t", "tests")
            .ForJob(job.Key)
            .StartAt(DateTimeOffset.UtcNow.AddSeconds(-1))
            .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromHours(1)).WithRepeatCount(5))
            .Build();
        trigger.ComputeFirstFireTimeUtc(null);
        await store.ScheduleJob(job, trigger);

        var bundle = await FireOneAsync(store);
        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));
        Assert.True((await store.GetTrigger(trigger.Key))!.NextFireTimeUtc > DateTimeOffset.UtcNow.AddMinutes(50));
        Assert.True(await store.Exists(job.Key));
    }

    [Fact]
    public async Task C5_NoInstruction_completion_of_the_second_to_last_firing_keeps_the_trigger_for_its_last()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1", durable: false);
        var trigger = CreateQuartzSimpleTrigger("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 1);
        await store.ScheduleJob(job, trigger);

        var bundle = await FireOneAsync(store);
        Assert.NotNull(bundle.Trigger.NextFireTimeUtc);

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));
        Assert.True(await store.Exists(job.Key));
    }
}

