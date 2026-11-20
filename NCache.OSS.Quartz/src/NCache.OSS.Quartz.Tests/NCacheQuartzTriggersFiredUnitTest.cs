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
public sealed class NCacheQuartzTriggersFiredUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task TriggersFired_returns_NotFired_when_trigger_is_missing()
    {
        var store = CreateStore("instance-1");

        var trigger = CreateQuartzSimpleTrigger("trigger-1", "tests", new JobKey("job-1", "tests"), DateTimeOffset.UtcNow);

        var results = await store.TriggersFired(new[] { trigger });

        Assert.Single(results);
        Assert.Equal(TriggerFiredResult.NotFired, results[0]);
    }

    [Fact]
    public async Task TriggersFired_returns_NotFired_when_trigger_is_not_acquired()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow);

        await StoreJobAndTriggerAsync(job, storedTrigger);

        var trigger = await AcquireForTestAsync(storedTrigger);

        var results = await store.TriggersFired(new[] { trigger });

        Assert.Single(results);
        Assert.Equal(TriggerFiredResult.NotFired, results[0]);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
    }

    [Fact]
    public async Task TriggersFired_returns_NotFired_when_job_is_missing()
    {
        var store = CreateStore("instance-1");

        var storedTrigger = CreateAcquiredTrigger("trigger-1", "missing-job", DateTimeOffset.UtcNow);
        await Storage.StoreTriggerAsync(storedTrigger, replaceExisting: false);
        await Index.UpsertAsync(storedTrigger);

        var trigger = await AcquireForTestAsync(storedTrigger);

        var results = await store.TriggersFired(new[] { trigger });

        Assert.Single(results);
        Assert.Equal(TriggerFiredResult.NotFired, results[0]);
    }

    [Fact]
    public async Task TriggersFired_returns_NotFired_when_referenced_calendar_is_missing()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow);
        storedTrigger.CalendarName = "missing-calendar";

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        var results = await store.TriggersFired(new[] { trigger });

        Assert.Single(results);
        Assert.Equal(TriggerFiredResult.NotFired, results[0]);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Acquired, stored.TriggerState);
    }

    [Fact]
    public async Task TriggersFired_throws_when_trigger_has_no_fire_instance_id()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow);

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = ToQuartzTrigger(storedTrigger);
        trigger.FireInstanceId = null;

        await Assert.ThrowsAsync<JobPersistenceException>(async () => await store.TriggersFired(new[] { trigger }));
    }

    [Fact]
    public async Task TriggersFired_changes_trigger_from_Acquired_to_Waiting_for_repeating_trigger()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 5);

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        var results = await store.TriggersFired(new[] { trigger });

        Assert.Single(results);
        Assert.NotEqual(TriggerFiredResult.NotFired, results[0]);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
        Assert.NotNull(stored.NextFireTimeUtc);
        Assert.NotNull(stored.PreviousFireTimeUtc);
    }

    [Fact]
    public async Task TriggersFired_changes_trigger_from_Acquired_to_Complete_for_one_shot_trigger()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 0);

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        var results = await store.TriggersFired(new[] { trigger });

        Assert.Single(results);
        Assert.NotEqual(TriggerFiredResult.NotFired, results[0]);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Complete, stored.TriggerState);
        Assert.Null(stored.NextFireTimeUtc);
    }

    [Fact]
    public async Task TriggersFired_updates_next_fire_time()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 5);

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var originalNextFireTime = storedTrigger.NextFireTimeUtc;

        var trigger = await AcquireForTestAsync(storedTrigger);

        await store.TriggersFired(new[] { trigger });

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.NotEqual(originalNextFireTime, stored.NextFireTimeUtc);
    }

    [Fact]
    public async Task TriggersFired_updates_previous_fire_time()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 5);

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        await store.TriggersFired(new[] { trigger });

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.NotNull(stored.PreviousFireTimeUtc);
        Assert.NotEqual(stored.NextFireTimeUtc, stored.PreviousFireTimeUtc);
    }

    [Fact]
    public async Task TriggersFired_creates_executing_fired_trigger()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        var results = await store.TriggersFired(new[] { trigger });

        Assert.Single(results);

        var fired = await Storage.GetFiredTriggerAsync(trigger.FireInstanceId!);

        Assert.NotNull(fired);
        Assert.Equal(FiredTriggerStates.Executing, fired.State);
        Assert.Equal(trigger.FireInstanceId, fired.EntryId);
    }

    [Fact]
    public async Task TriggersFired_preserves_fire_instance_id()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = await AcquireForTestAsync(storedTrigger);
        var fireInstanceId = trigger.FireInstanceId!;

        await store.TriggersFired(new[] { trigger });

        var fired = await Storage.GetFiredTriggerAsync(fireInstanceId);

        Assert.NotNull(fired);
        Assert.Equal(fireInstanceId, fired.EntryId);
        Assert.Equal(FiredTriggerStates.Executing, fired.State);
        Assert.Equal("instance-1", fired.InstanceName);
    }

    [Fact]
    public async Task TriggersFired_refuses_a_fire_instance_that_was_recovered_by_another_node()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = ToQuartzTrigger(storedTrigger);
        trigger.FireInstanceId = Guid.NewGuid().ToString("N");

        var results = await store.TriggersFired(new[] { trigger });

        Assert.Equal(TriggerFiredResult.NotFired, Assert.Single(results));
        Assert.Equal(TriggerStates.Acquired, (await Storage.GetTriggerAsync("tests", "trigger-1"))!.TriggerState);
        Assert.Null(await Storage.GetFiredTriggerAsync(trigger.FireInstanceId!));
    }

    [Fact]
    public async Task TriggersFired_refuses_a_fire_instance_owned_by_another_instance()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = await AcquireForTestAsync(storedTrigger, instanceId: "instance-2");

        var results = await store.TriggersFired(new[] { trigger });

        Assert.Equal(TriggerFiredResult.NotFired, Assert.Single(results));
        Assert.Equal(TriggerStates.Acquired, (await Storage.GetTriggerAsync("tests", "trigger-1"))!.TriggerState);
        Assert.Equal(FiredTriggerStates.Acquired, (await Storage.GetFiredTriggerAsync(trigger.FireInstanceId!))!.State);
    }

    [Fact]
    public async Task TriggersFired_returns_Fired_result()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        var results = await store.TriggersFired(new[] { trigger });

        Assert.Single(results);
        Assert.NotEqual(TriggerFiredResult.NotFired, results[0]);
    }

    [Fact]
    public async Task TriggersFired_does_not_require_calendar_when_trigger_has_none()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        var results = await store.TriggersFired(new[] { trigger });

        Assert.Single(results);
        Assert.NotEqual(TriggerFiredResult.NotFired, results[0]);
    }

    [Fact]
    public async Task TriggersFired_loads_calendar_when_trigger_has_calendar()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        await Storage.StoreJobAsync(job, replaceExisting: false);

        await store.AddCalendar("calendar-1", new HolidayCalendar(), new AddCalendarOptions { Replace = false, UpdateTriggers = false });

        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 1);
        storedTrigger.CalendarName = "calendar-1";

        await Storage.StoreTriggerAsync(storedTrigger, replaceExisting: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        var results = await store.TriggersFired(new[] { trigger });

        Assert.Single(results);
        Assert.NotEqual(TriggerFiredResult.NotFired, results[0]);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal("calendar-1", stored.CalendarName);
    }
    [Fact]
    public async Task TriggersFired_updates_next_fire_time_using_calendar()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        await Storage.StoreJobAsync(job, replaceExisting: false);

        await store.AddCalendar("calendar-1", new HolidayCalendar(), new AddCalendarOptions { Replace = false, UpdateTriggers = false });

        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 1);
        storedTrigger.CalendarName = "calendar-1";

        await Storage.StoreTriggerAsync(storedTrigger, replaceExisting: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        await store.TriggersFired(new[] { trigger });

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.NotNull(stored.NextFireTimeUtc);
    }

    [Fact]
    public async Task TriggersFired_blocks_waiting_sibling_triggers_for_non_concurrent_job()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var executingTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-2));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(executingTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);
        await Index.UpsertAsync(siblingTrigger);

        var trigger = await AcquireForTestAsync(executingTrigger);

        await store.TriggersFired(new[] { trigger });

        var sibling = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(sibling);
        Assert.Equal(TriggerStates.Blocked, sibling.TriggerState);
    }

    [Fact]
    public async Task TriggersFired_changes_paused_sibling_to_paused_and_blocked()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var executingTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-2));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));

        siblingTrigger.TriggerState = TriggerStates.Paused;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(executingTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);
        await Index.UpsertAsync(siblingTrigger);

        var trigger = await AcquireForTestAsync(executingTrigger);

        await store.TriggersFired(new[] { trigger });

        var sibling = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(sibling);
        Assert.Equal(TriggerStates.PausedAndBlocked, sibling.TriggerState);
    }

    [Fact]
    public async Task TriggersFired_does_not_block_acquired_sibling()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var executingTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-2));
        var siblingTrigger = CreateAcquiredTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(executingTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var trigger = await AcquireForTestAsync(executingTrigger);

        await store.TriggersFired(new[] { trigger });

        var sibling = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(sibling);
        Assert.Equal(TriggerStates.Acquired, sibling.TriggerState);
    }

    [Fact]
    public async Task TriggersFired_does_not_block_complete_sibling()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var executingTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-2));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));

        siblingTrigger.TriggerState = TriggerStates.Complete;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(executingTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var trigger = await AcquireForTestAsync(executingTrigger);

        await store.TriggersFired(new[] { trigger });

        var sibling = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(sibling);
        Assert.Equal(TriggerStates.Complete, sibling.TriggerState);
    }

    [Fact]
    public async Task TriggersFired_does_not_block_unrelated_job_triggers()
    {
        var store = CreateStore("instance-1");

        var executingJob = CreateJob("job-1", nonConcurrent: true);
        var unrelatedJob = CreateJob("job-2");

        var executingTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-2));
        var unrelatedTrigger = CreateWaitingTrigger("trigger-2", "job-2", DateTimeOffset.UtcNow.AddMinutes(1));

        await Storage.StoreJobAsync(executingJob, replaceExisting: false);
        await Storage.StoreJobAsync(unrelatedJob, replaceExisting: false);
        await Storage.StoreTriggerAsync(executingTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(unrelatedTrigger, replaceExisting: false);
        await Index.UpsertAsync(unrelatedTrigger);

        var trigger = await AcquireForTestAsync(executingTrigger);

        await store.TriggersFired(new[] { trigger });

        var unrelated = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(unrelated);
        Assert.Equal(TriggerStates.Waiting, unrelated.TriggerState);
    }

    [Fact]
    public async Task TriggersFired_persists_fired_trigger_as_Executing()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        await store.TriggersFired(new[] { trigger });

        var fired = await Storage.GetFiredTriggerAsync(trigger.FireInstanceId!);

        Assert.NotNull(fired);
        Assert.Equal(FiredTriggerStates.Executing, fired.State);
    }

    [Fact]
    public async Task TriggersFired_associates_fired_trigger_with_instance()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        await store.TriggersFired(new[] { trigger });

        var firedForInstance = await Storage.GetFiredTriggersForInstanceAsync("instance-1");

        Assert.Single(firedForInstance);
        Assert.Equal(trigger.FireInstanceId, firedForInstance[0].EntryId);
    }

    [Fact]
    public async Task TriggersFired_associates_fired_trigger_with_job()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var storedTrigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger);

        var trigger = Assert.Single(await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 1)));

        Assert.NotNull(Assert.Single(await store.TriggersFired(new[] { trigger })).TriggerFiredBundle);

        var firedForJob = await Storage.GetFiredTriggersForJobAsync("tests", "job-1");

        Assert.Single(firedForJob);
        Assert.Equal(trigger.FireInstanceId, firedForJob[0].EntryId);
    }

    [Fact]
    public async Task TriggersFired_records_the_job_on_the_firing_of_a_concurrent_job_without_a_job_registry()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        await store.TriggersFired(new[] { trigger });

        var fired = await Storage.GetFiredTriggerAsync(trigger.FireInstanceId!);
        Assert.NotNull(fired);
        Assert.Equal("tests", fired!.JobGroup);
        Assert.Equal("job-1", fired.JobName);
        Assert.Empty(await Storage.GetFiredTriggersForJobAsync("tests", "job-1"));
    }

    [Fact]
    public async Task TriggersFired_persists_non_concurrent_flag()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        await store.TriggersFired(new[] { trigger });

        var fired = await Storage.GetFiredTriggerAsync(trigger.FireInstanceId!);

        Assert.NotNull(fired);
        Assert.True(fired.IsNonConcurrent);
    }

    [Fact]
    public async Task TriggersFired_persists_recovery_flag()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", requestsRecovery: true);
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        await store.TriggersFired(new[] { trigger });

        var fired = await Storage.GetFiredTriggerAsync(trigger.FireInstanceId!);

        Assert.NotNull(fired);
        Assert.True(fired.RequestsRecovery);
    }

    [Fact]
    public async Task TriggersFired_persists_execution_group()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        storedTrigger.ExecutionGroup = "group-1";

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        await store.TriggersFired(new[] { trigger });

        var fired = await Storage.GetFiredTriggerAsync(trigger.FireInstanceId!);

        Assert.NotNull(fired);
        Assert.Equal("group-1", fired.ExecutionGroup);
    }

    [Fact]
    public async Task TriggersFired_returns_correct_trigger_identity_in_bundle()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        var results = await store.TriggersFired(new[] { trigger });

        Assert.Single(results);
        Assert.NotEqual(TriggerFiredResult.NotFired, results[0]);
    }

    [Fact]
    public async Task TriggersFired_returns_correct_job_detail()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        job.Description = "Stored job description";

        await Storage.StoreJobAsync(job, replaceExisting: false);

        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        await Storage.StoreTriggerAsync(storedTrigger, replaceExisting: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        var results = await store.TriggersFired(new[] { trigger });

        Assert.Single(results);
        Assert.NotEqual(TriggerFiredResult.NotFired, results[0]);
    }

    [Fact]
    public async Task TriggersFired_sets_recovering_for_recovery_trigger()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", requestsRecovery: true);
        var storedTrigger = CreateAcquiredTrigger("recover-trigger", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        storedTrigger.TriggerGroup = SchedulerConstants.DefaultRecoveryGroup;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(storedTrigger, replaceExisting: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        var results = await store.TriggersFired(new[] { trigger });

        Assert.Single(results);
        Assert.NotEqual(TriggerFiredResult.NotFired, results[0]);
    }

    [Fact]
    public async Task TriggersFired_sets_fire_times_in_persisted_fired_trigger()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var scheduledTime = DateTimeOffset.UtcNow.AddSeconds(-2);
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", scheduledTime);

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = await AcquireForTestAsync(storedTrigger);

        await store.TriggersFired(new[] { trigger });

        var fired = await Storage.GetFiredTriggerAsync(trigger.FireInstanceId!);

        Assert.NotNull(fired);
        Assert.Equal(scheduledTime, fired.ScheduledTimeUtc);
        Assert.True(fired.FiredTimeUtc <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task TriggersFired_processes_multiple_triggers()
    {
        var store = CreateStore("instance-1");

        var job1 = CreateJob("job-1");
        var job2 = CreateJob("job-2");

        var storedTrigger1 = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-2));
        var storedTrigger2 = CreateAcquiredTrigger("trigger-2", "job-2", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job1, storedTrigger1, addToIndex: false);
        await StoreJobAndTriggerAsync(job2, storedTrigger2, addToIndex: false);

        var trigger1 = await AcquireForTestAsync(storedTrigger1);
        var trigger2 = await AcquireForTestAsync(storedTrigger2);

        var results = await store.TriggersFired(new[] { trigger1, trigger2 });

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.NotEqual(TriggerFiredResult.NotFired, result));
    }

    [Fact]
    public async Task TriggersFired_returns_result_for_each_input_trigger()
    {
        var store = CreateStore("instance-1");

        var job1 = CreateJob("job-1");
        var job2 = CreateJob("job-2");

        var storedTrigger1 = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-2));
        var storedTrigger2 = CreateWaitingTrigger("trigger-2", "job-2", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job1, storedTrigger1, addToIndex: false);
        await StoreJobAndTriggerAsync(job2, storedTrigger2, addToIndex: false);

        var trigger1 = await AcquireForTestAsync(storedTrigger1);
        var trigger2 = await AcquireForTestAsync(storedTrigger2);

        var results = await store.TriggersFired(new[] { trigger1, trigger2 });

        Assert.Equal(2, results.Count);
        Assert.NotEqual(TriggerFiredResult.NotFired, results[0]);
        Assert.Equal(TriggerFiredResult.NotFired, results[1]);
    }

    [Fact]
    public async Task TriggersFired_does_not_abort_valid_trigger_when_previous_trigger_is_NotFired()
    {
        var store = CreateStore("instance-1");

        var validJob = CreateJob("valid-job");
        var storedValidTrigger = CreateAcquiredTrigger("valid-trigger", "valid-job", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(validJob, storedValidTrigger, addToIndex: false);

        var missingTrigger = CreateQuartzSimpleTrigger("missing-trigger", "tests", new JobKey("missing-job", "tests"), DateTimeOffset.UtcNow);

        var validTrigger = await AcquireForTestAsync(storedValidTrigger);

        var results = await store.TriggersFired(new[] { missingTrigger, validTrigger });

        Assert.Equal(2, results.Count);
        Assert.Equal(TriggerFiredResult.NotFired, results[0]);
        Assert.NotEqual(TriggerFiredResult.NotFired, results[1]);

        var fired = await Storage.GetFiredTriggerAsync(validTrigger.FireInstanceId!);

        Assert.NotNull(fired);
        Assert.Equal(FiredTriggerStates.Executing, fired.State);
    }
}

