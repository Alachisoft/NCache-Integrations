using System.Collections.Concurrent;
using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Mapping;
using NCache.OSS.Quartz.Model;
using Quartz;
using Quartz.Extensibility;
using Quartz.Impl.Calendar;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzContinuationUnitTest : NCacheQuartzScenarioTestBase
{
    [Fact]
    public async Task a_continuation_is_stored_Awaiting_and_is_never_acquired()
    {
        var store = await NewStoreAsync();
        var (parentJob, parent) = await ScheduleParentAsync(store, oneShot: false);
        var child = await AddContinuationAsync(store, "child", parent.Key);

        Assert.Equal(TriggerState.Awaiting, await store.GetTriggerState(child.Key));
        Assert.Equal(TriggerStates.Awaiting, (await Storage.GetTriggerAsync("tests", "child"))!.TriggerState);

        var acquired = await AcquireDueAsync(store, maxCount: 5);
        Assert.Equal(parent.Key, Assert.Single(acquired).Key);

        var listed = Assert.Single((await store.QueryTriggers(new TriggerQuery { State = TriggerState.Awaiting })).Items);
        Assert.Equal(child.Key, listed.Key);
        Assert.Equal(parent.Key, listed.ContinuesAfter);
        Assert.Equal(ContinuationCondition.OnSuccess, listed.ContinuationCondition);

        var rebuilt = (await store.GetTrigger(child.Key))!;
        Assert.Equal(parent.Key, rebuilt.Continuation.Parent);
        Assert.Equal(ContinuationCondition.OnSuccess, rebuilt.Continuation.When);
        Assert.Equal(new JobKey("child-job", "tests"), rebuilt.JobKey);
        Assert.True(await store.Exists(parentJob.Key));
    }

    [Fact]
    public async Task a_continuation_whose_parent_is_not_stored_is_refused_and_nothing_is_stored()
    {
        var store = await NewStoreAsync();
        var missing = new TriggerKey("nope", "tests");
        var job = Job("child-job", durable: false);
        var child = After("child", "tests", job.Key, missing);

        var ex = await Assert.ThrowsAsync<ObjectDoesNotExistException>(() => store.ScheduleJob(job, child).AsTask());
        Assert.Equal(child.Key, ex.TriggerKey);
        Assert.Equal(missing, ex.MissingTriggerKey);
        Assert.False(await store.Exists(job.Key));
        Assert.False(await store.Exists(child.Key));

        await store.AddJob(job, new AddJobOptions { Replace = true });
        await Assert.ThrowsAsync<ObjectDoesNotExistException>(() => store.AddTrigger(child).AsTask());
        Assert.False(await store.Exists(child.Key));

        var other = Job("other-job");
        var batchJob = Job("batch-job");
        var batchChild = After("batch-child", "tests", batchJob.Key, missing);
        await Assert.ThrowsAsync<ObjectDoesNotExistException>(() => store.ScheduleJobs(
            new Dictionary<IJobDetail, IReadOnlyCollection<IOperableTrigger>>
            {
                [other] = new[] { OneShot("ordinary", "tests", other.Key, DateTimeOffset.UtcNow.AddHours(1)) },
                [batchJob] = new[] { batchChild }
            }).AsTask());

        Assert.False(await store.Exists(other.Key));
        Assert.False(await store.Exists(batchJob.Key));
        Assert.False(await store.Exists(new TriggerKey("ordinary", "tests")));
        Assert.False(await store.Exists(batchChild.Key));
    }

    [Fact]
    public async Task a_batch_may_carry_the_parent_in_either_order()
    {
        var store = await NewStoreAsync();
        var parentJob = Job("parent-job");
        var childJob = Job("child-job");
        var parent = OneShot("parent", "tests", parentJob.Key, DateTimeOffset.UtcNow.AddHours(1));
        var child = After("child", "tests", childJob.Key, parent.Key);

        await store.ScheduleJobs(new Dictionary<IJobDetail, IReadOnlyCollection<IOperableTrigger>>
        {
            [childJob] = new[] { child },
            [parentJob] = new[] { parent }
        });

        Assert.Equal(TriggerState.Awaiting, await store.GetTriggerState(child.Key));
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(parent.Key));
    }

    [Fact]
    public async Task a_trigger_replaced_by_one_that_waits_for_itself_is_refused()
    {
        var store = await NewStoreAsync();
        var (job, parent) = await ScheduleParentAsync(store, oneShot: false);

        await Assert.ThrowsAsync<ObjectDoesNotExistException>(() => store.ReplaceTrigger(parent.Key, After("parent", "tests", job.Key, parent.Key)).AsTask());

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(parent.Key));
        Assert.True((await store.GetTrigger(parent.Key))!.Continuation.IsNone);
    }

    [Fact]
    public async Task a_successful_parent_releases_an_OnSuccess_continuation_and_discards_an_OnFailure_one()
    {
        var signaler = new RecordingSignaler();
        var store = await NewStoreAsync(signaler: signaler);
        var (_, parent) = await ScheduleParentAsync(store, oneShot: true);
        var onSuccess = await AddContinuationAsync(store, "on-success", parent.Key, ContinuationCondition.OnSuccess);
        var onFailure = await AddContinuationAsync(store, "on-failure", parent.Key, ContinuationCondition.OnFailure, durableJob: false);

        var bundle = await FireOneAsync(store);
        Assert.Equal(parent.Key, bundle.Trigger.Key);

        await CompleteAsync(store, bundle, ExecutionOutcome.Succeeded, SchedulerInstruction.DeleteTrigger);

        Assert.False(await store.Exists(parent.Key));
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(onSuccess.Key));
        var released = (await store.GetTrigger(onSuccess.Key))!;
        Assert.True(released.Continuation.IsNone);
        Assert.InRange(released.NextFireTimeUtc!.Value, DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Null(Assert.Single((await store.QueryTriggers(new TriggerQuery { Name = NameMatcher<TriggerKey>.NameEquals("on-success") })).Items).ContinuesAfter);

        Assert.Equal(onSuccess.Key, Assert.Single(await AcquireDueAsync(store)).Key);

        Assert.False(await store.Exists(onFailure.Key));
        Assert.False(await store.Exists(onFailure.JobKey));
        Assert.Contains(onFailure.Key, signaler.Finalized);
        Assert.DoesNotContain(onSuccess.Key, signaler.Finalized);
        Assert.NotEmpty(signaler.SchedulingChanges);
    }

    [Fact]
    public async Task a_failed_parent_releases_OnFailure_and_OnAnyOutcome_waiters_and_discards_OnSuccess()
    {
        var store = await NewStoreAsync();
        var (_, parent) = await ScheduleParentAsync(store, oneShot: false);
        var onSuccess = await AddContinuationAsync(store, "on-success", parent.Key, ContinuationCondition.OnSuccess);
        var onFailure = await AddContinuationAsync(store, "on-failure", parent.Key, ContinuationCondition.OnFailure);
        var onAny = await AddContinuationAsync(store, "on-any", parent.Key, ContinuationCondition.OnAnyOutcome);

        var bundle = await FireOneAsync(store);
        await CompleteAsync(store, bundle, ExecutionOutcome.Failed, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(parent.Key));
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(onFailure.Key));
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(onAny.Key));
        Assert.Equal(TriggerState.None, await store.GetTriggerState(onSuccess.Key));
    }

    [Theory]
    [InlineData(ExecutionOutcome.Cancelled, ContinuationCondition.OnCancellation)]
    [InlineData(ExecutionOutcome.Vetoed, ContinuationCondition.OnVeto)]
    public async Task cancellation_and_veto_release_only_the_condition_that_names_them(ExecutionOutcome outcome, ContinuationCondition named)
    {
        var store = await NewStoreAsync();
        var (_, parent) = await ScheduleParentAsync(store, oneShot: false);
        var matching = await AddContinuationAsync(store, "matching", parent.Key, named);
        var other = await AddContinuationAsync(store, "other", parent.Key, ContinuationCondition.OnSuccess | ContinuationCondition.OnFailure);

        var bundle = await FireOneAsync(store);
        await CompleteAsync(store, bundle, outcome, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(matching.Key));
        Assert.Equal(TriggerState.None, await store.GetTriggerState(other.Key));
    }

    [Fact]
    public async Task a_retried_failure_settles_nothing_until_the_occurrence_ends()
    {
        var store = await NewStoreAsync();
        var (_, parent) = await ScheduleParentAsync(store, oneShot: false);
        var onSuccess = await AddContinuationAsync(store, "on-success", parent.Key, ContinuationCondition.OnSuccess);

        var first = await FireOneAsync(store);

        first.Trigger.RetryAttempt = 1;
        first.Trigger.NextFireTimeUtc = DateTimeOffset.UtcNow.AddMilliseconds(-1);
        await CompleteAsync(store, first, ExecutionOutcome.Failed, SchedulerInstruction.RetryTrigger);

        Assert.Equal(TriggerState.Awaiting, await store.GetTriggerState(onSuccess.Key));

        var second = await FireOneAsync(store);
        Assert.Equal(parent.Key, second.Trigger.Key);
        await CompleteAsync(store, second, ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(onSuccess.Key));
    }

    [Fact]
    public async Task a_completion_without_an_outcome_settles_nothing()
    {
        var store = await NewStoreAsync();
        var (_, parent) = await ScheduleParentAsync(store, oneShot: false);
        var child = await AddContinuationAsync(store, "child", parent.Key, ContinuationCondition.OnAnyOutcome);

        var bundle = await FireOneAsync(store);

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.Awaiting, await store.GetTriggerState(child.Key));
    }

    [Fact]
    public async Task the_start_time_of_a_released_continuation_stays_a_floor()
    {
        var store = await NewStoreAsync();
        var (_, parent) = await ScheduleParentAsync(store, oneShot: false);
        var notBefore = DateTimeOffset.UtcNow.AddHours(1);
        var child = await AddContinuationAsync(store, "child", parent.Key, startAt: notBefore);

        await CompleteAsync(store, await FireOneAsync(store), ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(child.Key));
        Assert.Equal(notBefore, (await store.GetTrigger(child.Key))!.NextFireTimeUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Empty(await AcquireDueAsync(store));
    }

    [Fact]
    public async Task a_release_moves_the_fire_time_past_a_calendar_exclusion()
    {
        var store = await NewStoreAsync();
        var holiday = new HolidayCalendar();
        holiday.AddExcludedDay(DateOnly.FromDateTime(DateTime.UtcNow));
        await store.AddCalendar("skip-today", holiday);

        var (_, parent) = await ScheduleParentAsync(store, oneShot: false);
        var child = await AddContinuationAsync(store, "child", parent.Key, calendar: "skip-today");

        await CompleteAsync(store, await FireOneAsync(store), ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);

        var released = (await store.GetTrigger(child.Key))!.NextFireTimeUtc!.Value;
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(child.Key));
        Assert.Equal(holiday.GetNextIncludedTimeUtc(DateTimeOffset.UtcNow), released);
        Assert.True(released > DateTimeOffset.UtcNow);
        Assert.True(holiday.IsTimeIncluded(released));
    }

    [Fact]
    public async Task a_continuation_whose_end_time_has_passed_is_discarded_instead_of_released()
    {
        var signaler = new RecordingSignaler();
        var store = await NewStoreAsync(signaler: signaler);
        var (_, parent) = await ScheduleParentAsync(store, oneShot: false);
        var child = await AddContinuationAsync(store, "child", parent.Key, startAt: DateTimeOffset.UtcNow.AddHours(-2), endAt: DateTimeOffset.UtcNow.AddHours(-1));

        await CompleteAsync(store, await FireOneAsync(store), ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);

        Assert.False(await store.Exists(child.Key));
        Assert.Contains(child.Key, signaler.Finalized);
    }

    [Fact]
    public async Task discards_cascade_to_the_continuations_waiting_on_the_discarded_one()
    {
        var signaler = new RecordingSignaler();
        var store = await NewStoreAsync(signaler: signaler);
        var (_, parent) = await ScheduleParentAsync(store, oneShot: false);
        var reconcile = await AddContinuationAsync(store, "reconcile", parent.Key, ContinuationCondition.OnSuccess);
        var cleanup = await AddContinuationAsync(store, "cleanup", reconcile.Key, ContinuationCondition.OnAnyOutcome);

        await CompleteAsync(store, await FireOneAsync(store), ExecutionOutcome.Failed, SchedulerInstruction.NoInstruction);

        Assert.False(await store.Exists(reconcile.Key));
        Assert.False(await store.Exists(cleanup.Key));
        Assert.Contains(reconcile.Key, signaler.Finalized);
        Assert.Contains(cleanup.Key, signaler.Finalized);
    }

    [Fact]
    public async Task deleting_a_parent_releases_OnAnyOutcome_waiters_and_parks_the_rest_in_Error()
    {
        var signaler = new RecordingSignaler();
        var store = await NewStoreAsync(signaler: signaler);
        var (_, parent) = await ScheduleParentAsync(store, oneShot: false);
        var onAny = await AddContinuationAsync(store, "on-any", parent.Key, ContinuationCondition.OnAnyOutcome);
        var onSuccess = await AddContinuationAsync(store, "on-success", parent.Key, ContinuationCondition.OnSuccess);

        Assert.True(await store.DeleteTrigger(parent.Key));

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(onAny.Key));
        Assert.Equal(TriggerState.Error, await store.GetTriggerState(onSuccess.Key));
        Assert.Contains(onSuccess.Key, signaler.TriggersInError);

        var parked = Assert.Single((await store.QueryTriggers(new TriggerQuery { State = TriggerState.Error })).Items);
        Assert.Equal(parent.Key, parked.ContinuesAfter);
        Assert.Equal(ContinuationCondition.OnSuccess, parked.ContinuationCondition);

        Assert.False(_cache!.Contains(NCacheKeys.TriggerContinuations(_prefix, SchedulerName, "tests", "parent")));
    }

    [Fact]
    public async Task deleting_the_parents_job_settles_the_same_way()
    {
        var store = await NewStoreAsync();
        var (parentJob, parent) = await ScheduleParentAsync(store, oneShot: false);
        var onAny = await AddContinuationAsync(store, "on-any", parent.Key, ContinuationCondition.OnAnyOutcome);
        var onFailure = await AddContinuationAsync(store, "on-failure", parent.Key, ContinuationCondition.OnFailure);

        Assert.True(await store.DeleteJob(parentJob.Key));

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(onAny.Key));
        Assert.Equal(TriggerState.Error, await store.GetTriggerState(onFailure.Key));
    }

    [Fact]
    public async Task a_parked_continuation_reset_from_error_runs_now_and_forgets_its_parent()
    {
        var store = await NewStoreAsync();
        var (_, parent) = await ScheduleParentAsync(store, oneShot: false);
        var child = await AddContinuationAsync(store, "child", parent.Key, ContinuationCondition.OnSuccess);
        await store.DeleteTrigger(parent.Key);
        Assert.Equal(TriggerState.Error, await store.GetTriggerState(child.Key));

        Assert.True(await store.ResetTriggerFromErrorState(child.Key));

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(child.Key));
        var reset = (await store.GetTrigger(child.Key))!;
        Assert.True(reset.Continuation.IsNone);
        Assert.InRange(reset.NextFireTimeUtc!.Value, DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(child.Key, Assert.Single(await AcquireDueAsync(store)).Key);
    }

    [Fact]
    public async Task pausing_an_awaiting_trigger_is_refused_and_a_group_paused_meanwhile_keeps_the_released_trigger_paused()
    {
        var store = await NewStoreAsync();
        var (_, parent) = await ScheduleParentAsync(store, oneShot: false);
        var child = await AddContinuationAsync(store, "child", parent.Key, group: "later");

        Assert.False(await store.PauseTrigger(child.Key));
        await store.PauseTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals("later"));
        Assert.Equal(TriggerState.Awaiting, await store.GetTriggerState(child.Key));

        await CompleteAsync(store, await FireOneAsync(store), ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(child.Key));
        Assert.True(await store.ResumeTrigger(child.Key));
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(child.Key));
    }

    [Fact]
    public async Task a_sibling_firing_that_errors_the_jobs_triggers_leaves_an_awaiting_trigger_alone()
    {
        var store = await NewStoreAsync();
        var (_, parent) = await ScheduleParentAsync(store, oneShot: false);
        var job = Job("shared-job");
        var sibling = Repeating("sibling", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, sibling);
        var child = After("child", "tests", job.Key, parent.Key);
        await store.AddTrigger(child);

        var fired = await store.TriggersFired(await AcquireDueAsync(store, maxCount: 2));
        var siblingBundle = fired.Select(f => f.TriggerFiredBundle!).Single(b => b.Trigger.Key.Equals(sibling.Key));
        await CompleteAsync(store, siblingBundle, ExecutionOutcome.NotExecuted, SchedulerInstruction.SetAllJobTriggersError);

        Assert.Equal(TriggerState.Error, await store.GetTriggerState(sibling.Key));
        Assert.Equal(TriggerState.Awaiting, await store.GetTriggerState(child.Key));
    }

    [Fact]
    public async Task a_released_continuation_of_a_running_non_concurrent_job_is_blocked_until_that_run_completes()
    {
        var store = await NewStoreAsync();
        var (_, parent) = await ScheduleParentAsync(store, oneShot: false);
        var serial = Job("serial", nonConcurrent: true);
        var running = Repeating("running", "tests", serial.Key, DateTimeOffset.UtcNow.AddSeconds(-2), TimeSpan.FromHours(1));
        await store.ScheduleJob(serial, running);
        var child = After("child", "tests", serial.Key, parent.Key);
        await store.AddTrigger(child);

        var fired = await store.TriggersFired(await AcquireDueAsync(store, maxCount: 2));
        var bundles = fired.Select(f => f.TriggerFiredBundle!).ToList();
        var parentBundle = bundles.Single(b => b.Trigger.Key.Equals(parent.Key));
        var runningBundle = bundles.Single(b => b.Trigger.Key.Equals(running.Key));

        await CompleteAsync(store, parentBundle, ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);
        Assert.Equal(TriggerState.Blocked, await store.GetTriggerState(child.Key));
        Assert.True((await store.GetTrigger(child.Key))!.Continuation.IsNone);

        await CompleteAsync(store, runningBundle, ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(child.Key));
    }

    [Fact]
    public async Task rescheduling_a_parent_keeps_its_continuations()
    {
        var store = await NewStoreAsync();
        var (parentJob, parent) = await ScheduleParentAsync(store, oneShot: false);
        var child = await AddContinuationAsync(store, "child", parent.Key);

        Assert.True(await store.ReplaceTrigger(parent.Key, OneShot("parent", "tests", parentJob.Key, DateTimeOffset.UtcNow.AddSeconds(-1))));
        Assert.Equal(TriggerState.Awaiting, await store.GetTriggerState(child.Key));

        await CompleteAsync(store, await FireOneAsync(store), ExecutionOutcome.Succeeded, SchedulerInstruction.DeleteTrigger);

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(child.Key));
    }

    [Fact]
    public async Task a_continuation_scheduled_on_one_node_is_settled_by_the_node_that_runs_the_parent()
    {
        var nodeA = await NewStoreAsync("node-a");
        var nodeB = await NewStoreAsync("node-b");
        var (_, parent) = await ScheduleParentAsync(nodeA, oneShot: false);
        var child = await AddContinuationAsync(nodeA, "child", parent.Key);

        await CompleteAsync(nodeB, await FireOneAsync(nodeB), ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.Normal, await nodeA.GetTriggerState(child.Key));
        Assert.Equal(child.Key, Assert.Single(await AcquireDueAsync(nodeA)).Key);
    }

    [Fact]
    public async Task the_registry_repair_rebuilds_the_continuation_registry_and_drops_a_stale_one()
    {
        var store = await NewStoreAsync();
        var (_, parent) = await ScheduleParentAsync(store, oneShot: false);
        var child = await AddContinuationAsync(store, "child", parent.Key);

        var registryKey = NCacheKeys.TriggerContinuations(_prefix, SchedulerName, "tests", "parent");
        Assert.True(_cache!.Contains(registryKey));
        _cache.Remove(registryKey);

        var stale = NCacheKeys.TriggerContinuations(_prefix, SchedulerName, "tests", "child");
        _cache.Insert(stale, _cache.Get<byte[]>(NCacheKeys.JobTriggers(_prefix, SchedulerName, "tests", "parent-job")));

        await Storage.RebuildRegistriesAsync();

        Assert.True(_cache.Contains(registryKey));
        Assert.False(_cache.Contains(stale));

        await CompleteAsync(store, await FireOneAsync(store), ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(child.Key));
        Assert.False(_cache.Contains(registryKey));
    }

    [Fact]
    public async Task QueryTriggers_NextFireTimeBefore_selects_triggers_due_strictly_before_the_instant()
    {
        var store = await NewStoreAsync();
        var job = Job("j");
        var now = DateTimeOffset.UtcNow;

        var overdue = Repeating("overdue", "tests", job.Key, now.AddSeconds(-5), TimeSpan.FromHours(1));
        var later = Repeating("later", "tests", job.Key, now.AddHours(1), TimeSpan.FromHours(1));
        var spent = OneShot("spent", "tests", job.Key, now.AddSeconds(-10));
        await store.ScheduleJob(job, overdue);
        await store.AddTrigger(later);
        await store.AddTrigger(spent);

        var bundle = await FireOneAsync(store);
        Assert.Equal(spent.Key, bundle.Trigger.Key);
        await CompleteAsync(store, bundle, ExecutionOutcome.Succeeded, SchedulerInstruction.SetTriggerComplete);
        Assert.Null((await store.GetTrigger(spent.Key))!.NextFireTimeUtc);

        var before = (await store.QueryTriggers(new TriggerQuery { NextFireTimeBefore = now })).Items;
        Assert.Equal(overdue.Key, Assert.Single(before).Key);

        var overdueNormal = (await store.QueryTriggers(new TriggerQuery { State = TriggerState.Normal, NextFireTimeBefore = now, Take = 1 })).Items;
        Assert.Equal(overdue.Key, Assert.Single(overdueNormal).Key);

        var wide = (await store.QueryTriggers(new TriggerQuery { NextFireTimeBefore = now.AddHours(2) })).Items;
        Assert.Equal(new[] { "later", "overdue" }, wide.Select(t => t.Key.Name).ToArray());

        var exact = (await store.GetTrigger(later.Key))!.NextFireTimeUtc!.Value;
        Assert.Empty((await store.QueryTriggers(new TriggerQuery { Name = NameMatcher<TriggerKey>.NameEquals("later"), NextFireTimeBefore = exact })).Items);

        Assert.Equal(3, (await store.QueryTriggers(new TriggerQuery())).Items.Count);
    }

    [Fact]
    public void the_mapper_round_trips_a_continuation_and_reads_an_unreadable_condition_as_OnAnyOutcome()
    {
        var parent = new TriggerKey("import", "nightly");
        var trigger = After("reconcile", "nightly", new JobKey("j", "g"), parent, ContinuationCondition.OnFailure | ContinuationCondition.OnCancellation);

        var row = TriggerMapper.ToModel(trigger, SchedulerName, TriggerStates.Awaiting);
        Assert.Equal("import", row.ContinuesAfterTriggerName);
        Assert.Equal("nightly", row.ContinuesAfterTriggerGroup);
        Assert.Equal((int) (ContinuationCondition.OnFailure | ContinuationCondition.OnCancellation), row.ContinuationCondition);

        var back = TriggerMapper.ToQuartzTrigger(row);
        Assert.Equal(parent, back.Continuation.Parent);
        Assert.Equal(ContinuationCondition.OnFailure | ContinuationCondition.OnCancellation, back.Continuation.When);

        row.ContinuationCondition = 1 << 10;
        Assert.Equal(ContinuationCondition.OnAnyOutcome, TriggerMapper.ToQuartzTrigger(row).Continuation.When);

        row.ContinuesAfterTriggerName = null;
        Assert.True(TriggerMapper.ToQuartzTrigger(row).Continuation.IsNone);

        var ordinary = TriggerMapper.ToModel(OneShot("plain", "g", new JobKey("j", "g"), DateTimeOffset.UtcNow), SchedulerName, TriggerStates.Waiting);
        Assert.Null(ordinary.ContinuesAfterTriggerName);
        Assert.Null(ordinary.ContinuationCondition);
        Assert.False(ordinary.HasContinuationParent);
    }

    private async Task<(IJobDetail Job, IOperableTrigger Trigger)> ScheduleParentAsync(NCacheJobStore store, bool oneShot)
    {
        var job = Job("parent-job");
        var trigger = oneShot
            ? OneShot("parent", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1))
            : Repeating("parent", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));

        await store.ScheduleJob(job, trigger);
        return (job, trigger);
    }

    private async Task<IOperableTrigger> AddContinuationAsync(
        NCacheJobStore store,
        string name,
        TriggerKey parent,
        ContinuationCondition when = ContinuationCondition.OnSuccess,
        string group = "tests",
        bool durableJob = true,
        DateTimeOffset? startAt = null,
        DateTimeOffset? endAt = null,
        string? calendar = null)
    {
        var job = Job($"{name}-job", durable: durableJob);
        var trigger = After(name, group, job.Key, parent, when, startAt, endAt, calendar);
        await store.ScheduleJob(job, trigger);
        return trigger;
    }

    private static IOperableTrigger After(
        string name,
        string group,
        JobKey job,
        TriggerKey parent,
        ContinuationCondition when = ContinuationCondition.OnSuccess,
        DateTimeOffset? startAt = null,
        DateTimeOffset? endAt = null,
        string? calendar = null)
    {
        var builder = TriggerBuilder.Create()
            .WithIdentity(name, group)
            .ForJob(job)
            .StartAfter(parent, when)
            .WithSimpleSchedule(s => s.WithRepeatCount(0));

        if (startAt is { } start)
        {
            builder = builder.StartAt(start);
        }

        if (endAt is { } end)
        {
            builder = builder.EndAt(end);
        }

        if (calendar is not null)
        {
            builder = builder.WithCalendarName(calendar);
        }

        var trigger = (IOperableTrigger) builder.Build();
        trigger.ComputeFirstFireTimeUtc(null);
        return trigger;
    }

    private static Task CompleteAsync(NCacheJobStore store, TriggerFiredBundle bundle, ExecutionOutcome outcome, SchedulerInstruction instruction)
        => store.FiringComplete(new TriggeredJobCompleteContext
        {
            Trigger = bundle.Trigger,
            JobDetail = bundle.JobDetail,
            Instruction = instruction,
            Outcome = outcome
        }).AsTask();

    private sealed class RecordingSignaler : ISchedulerSignaler
    {
        public ConcurrentQueue<TriggerKey> Finalized { get; } = new();
        public ConcurrentQueue<TriggerKey> TriggersInError { get; } = new();
        public ConcurrentQueue<DateTimeOffset?> SchedulingChanges { get; } = new();

        public ValueTask NotifyTriggerListenersMisfired(ITrigger trigger, CancellationToken cancellationToken = default) => default;

        public ValueTask NotifySchedulerListenersFinalized(ITrigger trigger, CancellationToken cancellationToken = default)
        {
            Finalized.Enqueue(trigger.Key);
            return default;
        }

        public ValueTask NotifySchedulerListenersJobDeleted(JobKey jobKey, CancellationToken cancellationToken = default) => default;

        public ValueTask NotifySchedulerListenersTriggerInError(TriggerKey triggerKey, CancellationToken cancellationToken = default)
        {
            TriggersInError.Enqueue(triggerKey);
            return default;
        }

        public ValueTask NotifySchedulerListenersTriggersInError(JobKey jobKey, CancellationToken cancellationToken = default) => default;

        public ValueTask SignalSchedulingChange(DateTimeOffset? candidateNewNextFireTimeUtc, CancellationToken cancellationToken = default)
        {
            SchedulingChanges.Enqueue(candidateNewNextFireTimeUtc);
            return default;
        }

        public ValueTask NotifySchedulerListenersError(SchedulerErrorContext errorContext, CancellationToken cancellationToken = default) => default;
    }
}

