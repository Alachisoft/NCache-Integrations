using NCache.OSS.Quartz.Model;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzStoreTriggerConflictUnitTest : NCacheQuartzScenarioTestBase
{
    private static readonly TriggerKey Key = new("t", "tests");

    [Theory]
    [InlineData(TriggerConflict.Throw)]
    [InlineData(TriggerConflict.Replace)]
    [InlineData(TriggerConflict.Keep)]
    [InlineData(TriggerConflict.KeepEarlier)]
    public async Task Every_rule_creates_the_trigger_when_nothing_is_stored_under_its_key(TriggerConflict rule)
    {
        var store = await NewNodeAsync();
        var job = await AddJobAsync(store);
        var trigger = At(job.Key, DateTimeOffset.UtcNow.AddMinutes(10));

        var result = await store.StoreTrigger(trigger, rule);

        Assert.Equal(ScheduleOutcome.Created, result.Outcome);
        Assert.Equal(trigger.NextFireTimeUtc, result.NextFireTimeUtc);

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal(TriggerStates.Waiting, row.TriggerState);
        Assert.Equal(trigger.NextFireTimeUtc, row.NextFireTimeUtc);
        Assert.Single(await IndexedAsync());
    }

    [Fact]
    public async Task Throw_refuses_an_existing_key_and_leaves_the_stored_trigger_as_it_was()
    {
        var store = await NewNodeAsync();
        var job = await AddJobAsync(store);
        var existing = At(job.Key, DateTimeOffset.UtcNow.AddMinutes(10), "first");
        await store.StoreTrigger(existing, TriggerConflict.Throw);

        await Assert.ThrowsAsync<ObjectAlreadyExistsException>(() =>
            store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddMinutes(1), "second"), TriggerConflict.Throw).AsTask());

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal("first", row.Description);
        Assert.Equal(existing.NextFireTimeUtc, row.NextFireTimeUtc);
        Assert.Single(await IndexedAsync());
    }

    [Fact]
    public async Task Replace_stores_the_new_trigger_over_the_old_one_and_moves_its_index_entry()
    {
        var store = await NewNodeAsync();
        var job = await AddJobAsync(store);
        await store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddHours(3), "first"), TriggerConflict.Throw);

        var replacement = At(job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), "second");
        var result = await store.StoreTrigger(replacement, TriggerConflict.Replace);

        Assert.Equal(ScheduleOutcome.Replaced, result.Outcome);
        Assert.Equal(replacement.NextFireTimeUtc, result.NextFireTimeUtc);

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal("second", row.Description);
        Assert.Equal(replacement.NextFireTimeUtc, row.NextFireTimeUtc);
        Assert.Single(await IndexedAsync());
        Assert.Single(await store.GetTriggersForJob(job.Key));

        var bundle = await FireOneAsync(store);
        Assert.Equal("second", bundle.Trigger.Description);
    }

    [Fact]
    public async Task Keep_leaves_a_pending_trigger_alone_and_reports_when_it_fires()
    {
        var store = await NewNodeAsync();
        var job = await AddJobAsync(store);
        var existing = At(job.Key, DateTimeOffset.UtcNow.AddHours(3), "first");
        await store.StoreTrigger(existing, TriggerConflict.Throw);

        var result = await store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddMinutes(1), "second"), TriggerConflict.Keep);

        Assert.Equal(ScheduleOutcome.Kept, result.Outcome);
        Assert.Equal(existing.NextFireTimeUtc, result.NextFireTimeUtc);
        Assert.Equal("first", (await Storage.GetTriggerAsync("tests", "t"))!.Description);
        Assert.Single(await IndexedAsync());
    }

    [Fact]
    public async Task Keep_keeps_a_trigger_that_is_overdue_but_has_not_fired()
    {
        var store = await NewNodeAsync();
        var job = await AddJobAsync(store);
        var existing = At(job.Key, DateTimeOffset.UtcNow.AddSeconds(-20), "first");
        await store.StoreTrigger(existing, TriggerConflict.Throw);

        var result = await store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddHours(1), "second"), TriggerConflict.Keep);

        Assert.Equal(ScheduleOutcome.Kept, result.Outcome);
        Assert.Equal(existing.NextFireTimeUtc, result.NextFireTimeUtc);
    }

    [Theory]
    [InlineData(TriggerConflict.Keep)]
    [InlineData(TriggerConflict.KeepEarlier)]
    public async Task A_paused_trigger_is_still_pending_and_is_kept_paused(TriggerConflict rule)
    {
        var store = await NewNodeAsync();
        var job = await AddJobAsync(store);
        var existing = At(job.Key, DateTimeOffset.UtcNow.AddMinutes(5), "first");
        await store.StoreTrigger(existing, TriggerConflict.Throw);
        await store.PauseTrigger(Key);

        var result = await store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddHours(1), "second"), rule);

        Assert.Equal(ScheduleOutcome.Kept, result.Outcome);
        Assert.Equal(existing.NextFireTimeUtc, result.NextFireTimeUtc);
        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(Key));
    }

    [Theory]
    [InlineData(TriggerConflict.Keep)]
    [InlineData(TriggerConflict.KeepEarlier)]
    public async Task A_one_shot_whose_firing_has_begun_is_not_kept_and_its_completion_leaves_the_new_trigger(TriggerConflict rule)
    {
        var store = await NewNodeAsync();
        var job = await AddJobAsync(store);
        await store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), "first"), TriggerConflict.Throw);

        var running = await FireOneAsync(store);
        Assert.Null((await Storage.GetTriggerAsync("tests", "t"))!.NextFireTimeUtc);

        var later = At(job.Key, DateTimeOffset.UtcNow.AddHours(2), "second");
        var result = await store.StoreTrigger(later, rule);

        Assert.Equal(ScheduleOutcome.Replaced, result.Outcome);
        Assert.Equal(later.NextFireTimeUtc, result.NextFireTimeUtc);

        await store.FiringComplete(new TriggeredJobCompleteContext
        {
            Trigger = running.Trigger,
            JobDetail = running.JobDetail,
            Instruction = SchedulerInstruction.DeleteTrigger,
            Outcome = ExecutionOutcome.Succeeded
        });

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal("second", row.Description);
        Assert.Equal(TriggerStates.Waiting, row.TriggerState);
        Assert.Equal(later.NextFireTimeUtc, row.NextFireTimeUtc);
        Assert.Empty(await Storage.GetAllFiredTriggersAsync());
    }

    [Fact]
    public async Task KeepEarlier_replaces_only_when_the_new_trigger_fires_strictly_earlier()
    {
        var store = await NewNodeAsync();
        var job = await AddJobAsync(store);
        var at = DateTimeOffset.UtcNow.AddHours(2);
        await store.StoreTrigger(At(job.Key, at, "first"), TriggerConflict.Throw);

        var later = await store.StoreTrigger(At(job.Key, at.AddMinutes(1), "later"), TriggerConflict.KeepEarlier);
        Assert.Equal(ScheduleOutcome.Kept, later.Outcome);
        Assert.Equal("first", (await Storage.GetTriggerAsync("tests", "t"))!.Description);

        var stored = (await Storage.GetTriggerAsync("tests", "t"))!.NextFireTimeUtc!.Value;
        var tie = await store.StoreTrigger(At(job.Key, stored, "tie"), TriggerConflict.KeepEarlier);
        Assert.Equal(ScheduleOutcome.Kept, tie.Outcome);
        Assert.Equal(stored, tie.NextFireTimeUtc);
        Assert.Equal("first", (await Storage.GetTriggerAsync("tests", "t"))!.Description);

        var earlierTrigger = At(job.Key, at.AddMinutes(-1), "earlier");
        var earlier = await store.StoreTrigger(earlierTrigger, TriggerConflict.KeepEarlier);
        Assert.Equal(ScheduleOutcome.Replaced, earlier.Outcome);
        Assert.Equal(earlierTrigger.NextFireTimeUtc, earlier.NextFireTimeUtc);
        Assert.Equal("earlier", (await Storage.GetTriggerAsync("tests", "t"))!.Description);
        Assert.Single(await IndexedAsync());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public async Task A_rule_that_is_not_defined_is_refused_before_anything_is_stored(int rule)
    {
        var store = await NewNodeAsync();
        var job = await AddJobAsync(store);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddMinutes(1)), (TriggerConflict) rule).AsTask());

        Assert.False(await store.Exists(Key));
        Assert.Empty(await IndexedAsync());
    }

    [Fact]
    public async Task A_null_trigger_is_refused()
    {
        var store = await NewNodeAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.StoreTrigger(null!, TriggerConflict.Replace).AsTask());
    }

    [Theory]
    [InlineData(TriggerConflict.Throw)]
    [InlineData(TriggerConflict.Replace)]
    [InlineData(TriggerConflict.Keep)]
    [InlineData(TriggerConflict.KeepEarlier)]
    public async Task A_trigger_for_a_job_that_is_not_stored_is_refused_and_nothing_is_left_behind(TriggerConflict rule)
    {
        var store = await NewNodeAsync();

        await Assert.ThrowsAsync<JobPersistenceException>(() =>
            store.StoreTrigger(At(new JobKey("missing", "tests"), DateTimeOffset.UtcNow.AddMinutes(1)), rule).AsTask());

        Assert.False(await store.Exists(Key));
        Assert.Empty(await IndexedAsync());
        Assert.Empty(await Storage.GetAllTriggerKeysAsync());
    }

    [Fact]
    public async Task Replace_by_a_trigger_for_a_missing_job_keeps_the_stored_trigger()
    {
        var store = await NewNodeAsync();
        var job = await AddJobAsync(store);
        var existing = At(job.Key, DateTimeOffset.UtcNow.AddMinutes(10), "first");
        await store.StoreTrigger(existing, TriggerConflict.Throw);

        await Assert.ThrowsAsync<JobPersistenceException>(() =>
            store.StoreTrigger(At(new JobKey("missing", "tests"), DateTimeOffset.UtcNow.AddMinutes(1), "second"), TriggerConflict.Replace).AsTask());

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal("first", row.Description);
        Assert.Equal("job", row.JobName);
        Assert.Single(await IndexedAsync());
    }

    [Fact]
    public async Task Replace_by_a_trigger_of_another_job_moves_the_trigger_to_that_job()
    {
        var store = await NewNodeAsync();
        var job = await AddJobAsync(store);
        var other = await AddJobAsync(store, "other");
        await store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddMinutes(10)), TriggerConflict.Throw);

        var result = await store.StoreTrigger(At(other.Key, DateTimeOffset.UtcNow.AddSeconds(-1)), TriggerConflict.Replace);

        Assert.Equal(ScheduleOutcome.Replaced, result.Outcome);
        Assert.Empty(await store.GetTriggersForJob(job.Key));
        Assert.Equal(Key, Assert.Single(await store.GetTriggersForJob(other.Key)).Key);

        var bundle = await FireOneAsync(store);
        Assert.Equal(other.Key, bundle.JobDetail.Key);
    }

    [Fact]
    public async Task A_continuation_whose_parent_is_not_stored_is_refused_and_the_stored_trigger_stays()
    {
        var store = await NewNodeAsync();
        var job = await AddJobAsync(store);
        await store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddMinutes(10), "first"), TriggerConflict.Throw);

        var waiting = (IOperableTrigger) TriggerBuilder.Create()
            .WithIdentity(Key)
            .ForJob(job.Key)
            .StartAfter(new TriggerKey("nobody", "tests"))
            .Build();

        await Assert.ThrowsAsync<ObjectDoesNotExistException>(() => store.StoreTrigger(waiting, TriggerConflict.Replace).AsTask());

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal("first", row.Description);
        Assert.Equal(TriggerStates.Waiting, row.TriggerState);
    }

    [Fact]
    public async Task A_trigger_created_in_a_paused_group_is_stored_paused_and_answers_with_the_groups_reason()
    {
        var store = await NewNodeAsync();
        var job = await AddJobAsync(store);
        await store.PauseTriggerGroupsWith(GroupMatcher<TriggerKey>.GroupEquals("tests"), new PauseDetails { Reason = "maintenance" });

        var result = await store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddSeconds(-1)), TriggerConflict.Keep);

        Assert.Equal(ScheduleOutcome.Created, result.Outcome);
        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(Key));
        Assert.Equal("maintenance", (await store.GetTriggerPause(Key))!.Reason);
        Assert.Empty(await AcquireDueAsync(store));
    }

    [Fact]
    public async Task Replace_keeps_a_paused_trigger_paused_with_its_reason_and_its_previous_fire_time()
    {
        var store = await NewNodeAsync();
        var job = await AddJobAsync(store);
        await store.StoreTrigger(Repeating("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2), TimeSpan.FromHours(1)), TriggerConflict.Throw);

        var bundle = await FireOneAsync(store);
        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);
        var previous = (await Storage.GetTriggerAsync("tests", "t"))!.PreviousFireTimeUtc;
        Assert.NotNull(previous);

        await store.PauseTriggerWith(Key, new PauseDetails { Reason = "held", RequestedBy = "ops" });
        var recorded = (await store.GetTriggerPause(Key))!;

        var result = await store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddMinutes(5), "second"), TriggerConflict.Replace);

        Assert.Equal(ScheduleOutcome.Replaced, result.Outcome);

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal("second", row.Description);
        Assert.Equal(TriggerStates.Paused, row.TriggerState);
        Assert.Equal(previous, row.PreviousFireTimeUtc);
        Assert.Equal(recorded, await store.GetTriggerPause(Key));
        Assert.Empty(await IndexedAsync());
    }

    [Fact]
    public async Task Replace_while_a_non_concurrent_job_runs_stores_the_new_trigger_blocked_until_it_ends()
    {
        var store = await NewNodeAsync();
        var job = Job("job", nonConcurrent: true);
        await store.AddJob(job);
        await store.StoreTrigger(Repeating("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2), TimeSpan.FromSeconds(1)), TriggerConflict.Throw);
        var running = await FireOneAsync(store);

        var result = await store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), "second"), TriggerConflict.Replace);

        Assert.Equal(ScheduleOutcome.Replaced, result.Outcome);
        Assert.Equal(TriggerStates.Blocked, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        Assert.Empty(await AcquireDueAsync(store));

        await store.TriggeredJobComplete(running.Trigger, running.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.Equal("second", Assert.Single(await AcquireDueAsync(store)).Description);
    }

    [Fact]
    public async Task Replace_of_a_trigger_the_scheduler_has_acquired_makes_that_acquisition_not_fire()
    {
        var store = await NewNodeAsync();
        var job = await AddJobAsync(store);
        await store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), "first"), TriggerConflict.Throw);
        var acquired = Assert.Single(await AcquireDueAsync(store));

        var later = At(job.Key, DateTimeOffset.UtcNow.AddHours(1), "second");
        var result = await store.StoreTrigger(later, TriggerConflict.Replace);
        Assert.Equal(ScheduleOutcome.Replaced, result.Outcome);

        var fired = Assert.Single(await store.TriggersFired(new[] { acquired }));
        Assert.Null(fired.TriggerFiredBundle);
        Assert.False(fired.IsDeclined);

        await store.ReleaseAcquiredTrigger(acquired);

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal("second", row.Description);
        Assert.Equal(TriggerStates.Waiting, row.TriggerState);
        Assert.Equal(later.NextFireTimeUtc, row.NextFireTimeUtc);
        Assert.Empty(await Storage.GetAllFiredTriggersAsync());
    }

    [Fact]
    public async Task Keep_keeps_a_trigger_the_scheduler_has_acquired()
    {
        var store = await NewNodeAsync();
        var job = await AddJobAsync(store);
        var existing = At(job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), "first");
        await store.StoreTrigger(existing, TriggerConflict.Throw);
        var acquired = Assert.Single(await AcquireDueAsync(store));

        var result = await store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddHours(1), "second"), TriggerConflict.Keep);

        Assert.Equal(ScheduleOutcome.Kept, result.Outcome);
        Assert.Equal(existing.NextFireTimeUtc, result.NextFireTimeUtc);

        var fired = Assert.Single(await store.TriggersFired(new[] { acquired }));
        Assert.Equal("first", fired.TriggerFiredBundle!.Trigger.Description);
    }

    [Fact]
    public async Task Two_nodes_scheduling_the_same_key_with_Keep_store_exactly_one_trigger()
    {
        var nodeA = await NewNodeAsync("node-a");
        var nodeB = await NewNodeAsync("node-b");
        var job = await AddJobAsync(nodeA);
        var start = DateTimeOffset.UtcNow.AddHours(1);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            (i % 2 == 0 ? nodeA : nodeB).StoreTrigger(At(job.Key, start.AddMinutes(i), $"call-{i}"), TriggerConflict.Keep).AsTask()));

        Assert.Single(results, r => r.Outcome == ScheduleOutcome.Created);
        Assert.Equal(9, results.Count(r => r.Outcome == ScheduleOutcome.Kept));

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.All(results, r => Assert.Equal(row.NextFireTimeUtc, r.NextFireTimeUtc));
        Assert.Single(await IndexedAsync());
        Assert.Single(await nodeA.GetTriggersForJob(job.Key));
    }

    [Fact]
    public async Task Two_nodes_scheduling_the_same_key_with_Throw_let_exactly_one_through()
    {
        var nodeA = await NewNodeAsync("node-a");
        var nodeB = await NewNodeAsync("node-b");
        var job = await AddJobAsync(nodeA);
        var start = DateTimeOffset.UtcNow.AddHours(1);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            try
            {
                await (i % 2 == 0 ? nodeA : nodeB).StoreTrigger(At(job.Key, start.AddMinutes(i), $"call-{i}"), TriggerConflict.Throw);
                return (Stored: true, Description: $"call-{i}");
            }
            catch (ObjectAlreadyExistsException)
            {
                return (Stored: false, Description: $"call-{i}");
            }
        }));

        var winner = Assert.Single(attempts, a => a.Stored);
        Assert.Equal(winner.Description, (await Storage.GetTriggerAsync("tests", "t"))!.Description);
        Assert.Single(await IndexedAsync());
    }

    [Fact]
    public async Task Two_nodes_racing_with_KeepEarlier_end_with_the_earliest_trigger()
    {
        var nodeA = await NewNodeAsync("node-a");
        var nodeB = await NewNodeAsync("node-b");
        var job = await AddJobAsync(nodeA);
        var start = DateTimeOffset.UtcNow.AddHours(1);

        var triggers = Enumerable.Range(0, 10).Select(i => At(job.Key, start.AddMinutes(10 - i), $"call-{i}")).ToList();
        await Task.WhenAll(triggers.Select((t, i) => (i % 2 == 0 ? nodeA : nodeB).StoreTrigger(t, TriggerConflict.KeepEarlier).AsTask()));

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal("call-9", row.Description);
        Assert.Equal(triggers[9].NextFireTimeUtc, row.NextFireTimeUtc);
        Assert.Single(await IndexedAsync());
    }

    [Fact]
    public async Task A_replace_whose_write_fails_puts_the_stored_trigger_and_its_index_entry_back()
    {
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewNodeAsync(cache: cache);
        var job = await AddJobAsync(store);
        var existing = At(job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), "first");
        await store.StoreTrigger(existing, TriggerConflict.Throw);

        control.FailWhen = FaultInjectingCache.Times(1, FaultInjectingCache.Call("Insert", k => k.EndsWith(":trigger:tests.t", StringComparison.Ordinal)));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddHours(5), "second"), TriggerConflict.Replace).AsTask());
        control.FailWhen = null;

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal("first", row.Description);
        Assert.Equal(existing.NextFireTimeUtc, row.NextFireTimeUtc);
        Assert.Single(await IndexedAsync());
        Assert.Equal("first", (await FireOneAsync(store)).Trigger.Description);
    }

    [Fact]
    public async Task A_create_whose_write_fails_leaves_no_trigger_and_no_index_entry_and_can_be_repeated()
    {
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewNodeAsync(cache: cache);
        var job = await AddJobAsync(store);

        control.FailWhen = FaultInjectingCache.Times(1, FaultInjectingCache.Call("Add", k => k.EndsWith(":trigger:tests.t", StringComparison.Ordinal)));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), "first"), TriggerConflict.Keep).AsTask());
        control.FailWhen = null;

        Assert.False(await store.Exists(Key));
        Assert.Empty(await IndexedAsync());
        Assert.Empty(await store.GetTriggersForJob(job.Key));

        var result = await store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), "again"), TriggerConflict.Keep);

        Assert.Equal(ScheduleOutcome.Created, result.Outcome);
        Assert.Equal("again", (await FireOneAsync(store)).Trigger.Description);
    }

    [Fact]
    public async Task A_failed_read_of_the_stored_trigger_stores_nothing()
    {
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewNodeAsync(cache: cache);
        var job = await AddJobAsync(store);
        await store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddMinutes(10), "first"), TriggerConflict.Throw);

        control.FailWhen = FaultInjectingCache.Times(1, FaultInjectingCache.Call("Get", k => k.EndsWith(":trigger:tests.t", StringComparison.Ordinal)));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.StoreTrigger(At(job.Key, DateTimeOffset.UtcNow.AddMinutes(1), "second"), TriggerConflict.Replace).AsTask());
        control.FailWhen = null;

        Assert.Equal("first", (await Storage.GetTriggerAsync("tests", "t"))!.Description);
        Assert.Single(await IndexedAsync());
    }

    private static async Task<IJobDetail> AddJobAsync(NCacheJobStore store, string name = "job")
    {
        var job = Job(name);
        await store.AddJob(job);
        return job;
    }

    private static IOperableTrigger At(JobKey job, DateTimeOffset at, string? description = null)
    {
        var trigger = (IOperableTrigger) TriggerBuilder.Create()
            .WithIdentity(Key)
            .ForJob(job)
            .WithDescription(description)
            .StartAt(at)
            .WithSimpleSchedule(s => s.WithRepeatCount(0))
            .Build();

        trigger.ComputeFirstFireTimeUtc(null);
        return trigger;
    }

    private async Task<List<string>> IndexedAsync()
        => (await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddDays(1), 100))
            .Where(c => c.TriggerName == "t")
            .Select(c => c.TriggerName)
            .ToList();
}
