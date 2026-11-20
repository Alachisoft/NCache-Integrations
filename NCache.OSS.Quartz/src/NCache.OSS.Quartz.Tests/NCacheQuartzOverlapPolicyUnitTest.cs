using System.Collections.Concurrent;
using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Mapping;
using NCache.OSS.Quartz.Model;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzOverlapPolicyUnitTest : NCacheQuartzScenarioTestBase
{
    [Theory]
    [InlineData(OverlapPolicy.Default)]
    [InlineData(OverlapPolicy.AllowAll)]
    public async Task Default_and_AllowAll_fire_beside_a_running_firing(OverlapPolicy policy)
    {
        var signaler = new RecordingSignaler();
        var store = await NewNodeAsync(signaler: signaler);
        var trigger = await ScheduleAsync(store, policy);

        var first = await FireOneAsync(store);
        var (_, second) = await AcquireAndFireAsync(store);

        Assert.NotNull(second.TriggerFiredBundle);
        Assert.False(second.IsDeclined);
        Assert.Null(second.TriggerFiredBundle!.SupersededFireInstanceIds);
        Assert.Equal(2, (await Storage.GetAllFiredTriggersAsync()).Count(f => f.State == FiredTriggerStates.Executing));
        Assert.Empty(signaler.Skipped);
        Assert.NotEqual(first.Trigger.FireInstanceId, second.TriggerFiredBundle.Trigger.FireInstanceId);
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", trigger.Key.Name))!.TriggerState);
    }

    [Fact]
    public async Task Skip_drops_the_firing_that_comes_due_while_one_runs_and_moves_on()
    {
        var signaler = new RecordingSignaler();
        var store = await NewNodeAsync(signaler: signaler);
        var trigger = await ScheduleAsync(store, OverlapPolicy.Skip);

        var first = await FireOneAsync(store);
        var dropped = (await Storage.GetTriggerAsync("tests", "t"))!.NextFireTimeUtc!.Value;

        var (acquired, result) = await AcquireAndFireAsync(store);

        Assert.True(result.IsDeclined);
        Assert.Null(result.TriggerFiredBundle);
        Assert.Null(result.Exception);

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal(TriggerStates.Waiting, row.TriggerState);
        Assert.Equal(dropped.AddSeconds(1), row.NextFireTimeUtc!.Value);
        Assert.Equal(2, row.Simple!.TimesTriggered);

        var records = await Storage.GetAllFiredTriggersAsync();
        Assert.Equal(first.Trigger.FireInstanceId, Assert.Single(records).EntryId);
        Assert.Null(await Storage.GetFiredTriggerAsync(acquired.FireInstanceId!));

        var skipped = Assert.Single(signaler.Skipped);
        Assert.Equal(trigger.Key, skipped.Key);
        Assert.Equal(dropped, skipped.NextFireTimeUtc);
        Assert.Empty(signaler.Misfired);
        Assert.Empty(signaler.Finalized);
        Assert.NotEmpty(signaler.SchedulingChanges);
    }

    [Fact]
    public async Task Skip_fires_normally_when_nothing_of_the_trigger_is_running()
    {
        var signaler = new RecordingSignaler();
        var store = await NewNodeAsync(signaler: signaler);
        await ScheduleAsync(store, OverlapPolicy.Skip);

        var first = await FireOneAsync(store);
        await CompleteAsync(store, first, ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);

        var (_, second) = await AcquireAndFireAsync(store);

        Assert.NotNull(second.TriggerFiredBundle);
        Assert.Empty(signaler.Skipped);
    }

    [Fact]
    public async Task Skip_keeps_dropping_every_occurrence_until_the_running_firing_ends()
    {
        var signaler = new RecordingSignaler();
        var store = await NewNodeAsync(signaler: signaler);
        await ScheduleAsync(store, OverlapPolicy.Skip);

        var first = await FireOneAsync(store);

        for (var i = 0; i < 3; i++)
        {
            var (_, result) = await AcquireAndFireAsync(store);
            Assert.True(result.IsDeclined);
        }

        Assert.Equal(3, signaler.Skipped.Count);
        Assert.Single(await Storage.GetAllFiredTriggersAsync());

        await CompleteAsync(store, first, ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(new TriggerKey("t", "tests")));
    }

    [Fact]
    public async Task Skip_that_drops_the_last_occurrence_leaves_the_trigger_complete_for_the_running_completion_to_remove()
    {
        var signaler = new RecordingSignaler();
        var store = await NewNodeAsync(signaler: signaler);
        var job = Job("job", durable: false);
        var trigger = Build("t", job.Key, OverlapPolicy.Skip, DateTimeOffset.UtcNow.AddSeconds(-3), TimeSpan.FromSeconds(1), repeatCount: 1);
        await store.ScheduleJob(job, trigger);

        var first = await FireOneAsync(store);
        var (_, result) = await AcquireAndFireAsync(store);

        Assert.True(result.IsDeclined);
        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal(TriggerStates.Complete, row.TriggerState);
        Assert.Null(row.NextFireTimeUtc);
        Assert.Contains(trigger.Key, signaler.Finalized);
        Assert.Empty(await AcquireDueAsync(store));

        await CompleteAsync(store, first, ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);

        Assert.False(await store.Exists(trigger.Key));
        Assert.False(await store.Exists(job.Key));
        Assert.Empty(await Storage.GetAllFiredTriggersAsync());
    }

    [Fact]
    public async Task Skip_never_drops_a_retry_of_an_occurrence_that_already_started()
    {
        var signaler = new RecordingSignaler();
        var store = await NewNodeAsync(signaler: signaler);
        await ScheduleAsync(store, OverlapPolicy.Skip);

        await FireOneAsync(store);

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        row.RetryAttempt = 1;
        await Storage.UpdateTriggerAsync(row);

        var (_, result) = await AcquireAndFireAsync(store);

        Assert.NotNull(result.TriggerFiredBundle);
        Assert.False(result.IsDeclined);
        Assert.Empty(signaler.Skipped);
    }

    [Fact]
    public async Task Skip_counts_a_firing_running_on_another_node()
    {
        var signaler = new RecordingSignaler();
        var nodeA = await NewNodeAsync("node-a");
        var nodeB = await NewNodeAsync("node-b", signaler: signaler);
        await ScheduleAsync(nodeA, OverlapPolicy.Skip);

        await FireOneAsync(nodeA);
        var (_, result) = await AcquireAndFireAsync(nodeB);

        Assert.True(result.IsDeclined);
        Assert.Single(signaler.Skipped);
        Assert.Equal("node-a", Assert.Single(await Storage.GetAllFiredTriggersAsync()).InstanceName);
    }

    [Fact]
    public async Task Skip_ignores_a_running_firing_of_another_trigger_of_the_same_job()
    {
        var signaler = new RecordingSignaler();
        var store = await NewNodeAsync(signaler: signaler);
        var job = Job("job");
        var other = Build("other", job.Key, OverlapPolicy.Default, DateTimeOffset.UtcNow.AddSeconds(-5), TimeSpan.FromHours(1));
        var skipper = Build("t", job.Key, OverlapPolicy.Skip, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, other);

        var running = await FireOneAsync(store);
        Assert.Equal(other.Key, running.Trigger.Key);

        await store.AddTrigger(skipper);
        var (_, result) = await AcquireAndFireAsync(store);

        Assert.NotNull(result.TriggerFiredBundle);
        Assert.Equal(skipper.Key, result.TriggerFiredBundle!.Trigger.Key);
        Assert.Empty(signaler.Skipped);
    }

    [Theory]
    [InlineData(OverlapPolicy.Skip)]
    [InlineData(OverlapPolicy.BufferOne)]
    [InlineData(OverlapPolicy.CancelPrevious)]
    public async Task A_job_that_disallows_concurrent_execution_wins_over_every_policy(OverlapPolicy policy)
    {
        var signaler = new RecordingSignaler();
        var store = await NewNodeAsync(signaler: signaler);
        var job = Job("job", nonConcurrent: true);
        var trigger = Build("t", job.Key, policy, DateTimeOffset.UtcNow.AddSeconds(-3), TimeSpan.FromSeconds(1));
        await store.ScheduleJob(job, trigger);

        var first = await FireOneAsync(store);

        Assert.Equal(TriggerStates.Blocked, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        Assert.Empty(await AcquireDueAsync(store));
        Assert.Empty(signaler.Skipped);
        Assert.Null(first.SupersededFireInstanceIds);

        await CompleteAsync(store, first, ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
    }

    [Fact]
    public async Task BufferOne_blocks_its_own_trigger_while_it_runs_and_fires_the_held_occurrence_when_it_ends()
    {
        var store = await NewNodeAsync();
        var trigger = await ScheduleAsync(store, OverlapPolicy.BufferOne);

        var first = await FireOneAsync(store);
        var held = (await Storage.GetTriggerAsync("tests", "t"))!;

        Assert.Equal(TriggerStates.Blocked, held.TriggerState);
        Assert.Equal(TriggerState.Executing, await store.GetTriggerState(trigger.Key));
        Assert.Empty(await AcquireDueAsync(store, maxCount: 5));

        await CompleteAsync(store, first, ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);

        var released = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal(TriggerStates.Waiting, released.TriggerState);
        Assert.Equal(held.NextFireTimeUtc, released.NextFireTimeUtc);
        Assert.Contains(await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10), x => x.TriggerName == "t");

        var (_, second) = await AcquireAndFireAsync(store);
        Assert.NotNull(second.TriggerFiredBundle);
        Assert.Equal(TriggerStates.Blocked, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
    }

    [Fact]
    public async Task BufferOne_does_not_block_another_trigger_of_the_same_job()
    {
        var store = await NewNodeAsync();
        var job = Job("job");
        var buffered = Build("t", job.Key, OverlapPolicy.BufferOne, DateTimeOffset.UtcNow.AddSeconds(-5), TimeSpan.FromHours(1));
        var sibling = Build("sibling", job.Key, OverlapPolicy.Default, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, buffered);
        await store.AddTrigger(sibling);

        var first = await FireOneAsync(store);
        Assert.Equal(buffered.Key, first.Trigger.Key);

        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "sibling"))!.TriggerState);
        Assert.Equal(sibling.Key, Assert.Single(await AcquireDueAsync(store)).Key);
    }

    [Fact]
    public async Task BufferOne_applies_the_misfire_instruction_to_an_occurrence_held_past_the_threshold()
    {
        var signaler = new RecordingSignaler();
        var store = await NewNodeAsync(signaler: signaler);
        var trigger = await ScheduleAsync(store, OverlapPolicy.BufferOne);
        var first = await FireOneAsync(store);

        var held = (await Storage.GetTriggerAsync("tests", "t"))!;
        held.NextFireTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-30);
        await Storage.UpdateTriggerAsync(held);

        await CompleteAsync(store, first, ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);

        var released = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal(TriggerStates.Waiting, released.TriggerState);
        Assert.True(released.NextFireTimeUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Contains(trigger.Key, signaler.Misfired);
    }

    [Fact]
    public async Task BufferOne_paused_while_running_stays_paused_when_the_firing_ends_and_resumes_normally()
    {
        var store = await NewNodeAsync();
        var trigger = await ScheduleAsync(store, OverlapPolicy.BufferOne);
        var first = await FireOneAsync(store);

        Assert.True(await store.PauseTrigger(trigger.Key));
        Assert.Equal(TriggerStates.PausedAndBlocked, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));

        await CompleteAsync(store, first, ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerStates.Paused, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        Assert.Empty(await AcquireDueAsync(store));

        Assert.True(await store.ResumeTrigger(trigger.Key));
        Assert.Equal(trigger.Key, Assert.Single(await AcquireDueAsync(store)).Key);
    }

    [Fact]
    public async Task BufferOne_paused_and_resumed_while_running_stays_held_until_the_firing_ends()
    {
        var store = await NewNodeAsync();
        var trigger = await ScheduleAsync(store, OverlapPolicy.BufferOne);
        var first = await FireOneAsync(store);

        await store.PauseTrigger(trigger.Key);
        await store.ResumeTrigger(trigger.Key);

        Assert.Equal(TriggerStates.Blocked, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        Assert.Empty(await AcquireDueAsync(store));

        await CompleteAsync(store, first, ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
    }

    [Fact]
    public async Task BufferOne_completion_that_asks_for_a_retry_stores_the_retry_and_lets_go_of_the_trigger()
    {
        var store = await NewNodeAsync();
        await ScheduleAsync(store, OverlapPolicy.BufferOne);
        var first = await FireOneAsync(store);

        first.Trigger.RetryAttempt = 1;
        await CompleteAsync(store, first, ExecutionOutcome.Failed, SchedulerInstruction.RetryTrigger);

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal(TriggerStates.Waiting, row.TriggerState);
        Assert.Equal(1, row.RetryAttempt);
        Assert.Equal(first.Trigger.NextFireTimeUtc, row.NextFireTimeUtc);
        Assert.Empty(await Storage.GetAllFiredTriggersAsync());

        var (_, retry) = await AcquireAndFireAsync(store);
        Assert.NotNull(retry.TriggerFiredBundle);
        Assert.Equal(TriggerStates.Blocked, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
    }

    [Fact]
    public async Task BufferOne_last_firing_completes_by_deleting_the_trigger_with_nothing_left_blocked()
    {
        var store = await NewNodeAsync();
        var job = Job("job", durable: false);
        var trigger = OneShotWithPolicy("t", job.Key, OverlapPolicy.BufferOne);
        await store.ScheduleJob(job, trigger);

        var first = await FireOneAsync(store);
        Assert.Equal(TriggerStates.Complete, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);

        await CompleteAsync(store, first, ExecutionOutcome.Succeeded, SchedulerInstruction.DeleteTrigger);

        Assert.False(await store.Exists(trigger.Key));
        Assert.False(await store.Exists(job.Key));
    }

    [Fact]
    public async Task BufferOne_trigger_held_by_a_firing_on_a_node_that_died_is_released_by_recovery()
    {
        var dead = await NewNodeAsync("node-dead");
        var survivor = await NewNodeAsync("node-b");
        var trigger = await ScheduleAsync(dead, OverlapPolicy.BufferOne);
        await FireOneAsync(dead);

        Assert.Equal(TriggerStates.Blocked, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);

        await CheckInAsync("node-dead", DateTimeOffset.UtcNow.AddMinutes(-5));
        await survivor.RecoverStaleSchedulerInstancesAsync();

        Assert.Empty(await Storage.GetAllFiredTriggersAsync());
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        Assert.Equal(trigger.Key, Assert.Single(await AcquireDueAsync(survivor)).Key);
    }

    [Fact]
    public async Task CancelPrevious_hands_the_scheduler_the_running_firings_of_this_node_to_interrupt()
    {
        var store = await NewNodeAsync();
        await ScheduleAsync(store, OverlapPolicy.CancelPrevious);

        var first = await FireOneAsync(store);
        Assert.Null(first.SupersededFireInstanceIds);

        var (_, second) = await AcquireAndFireAsync(store);

        Assert.NotNull(second.TriggerFiredBundle);
        Assert.Equal(new[] { first.Trigger.FireInstanceId! }, second.TriggerFiredBundle!.SupersededFireInstanceIds);

        var (_, third) = await AcquireAndFireAsync(store);

        Assert.Equal(
            new[] { first.Trigger.FireInstanceId!, second.TriggerFiredBundle.Trigger.FireInstanceId! }.OrderBy(x => x),
            third.TriggerFiredBundle!.SupersededFireInstanceIds!.OrderBy(x => x));
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
    }

    [Fact]
    public async Task CancelPrevious_firing_running_on_another_node_is_waited_for_not_interrupted()
    {
        var log = new CapturingLoggerProvider();
        var nodeA = await NewNodeAsync("node-a");
        var nodeB = await NewNodeAsync("node-b", logger: log.CreateLogger<NCacheJobStore>());
        var trigger = await ScheduleAsync(nodeA, OverlapPolicy.CancelPrevious);

        var first = await FireOneAsync(nodeA);
        var due = (await Storage.GetTriggerAsync("tests", "t"))!.NextFireTimeUtc;

        var (acquired, result) = await AcquireAndFireAsync(nodeB);

        Assert.True(result.IsDeclined);
        Assert.Null(result.TriggerFiredBundle);

        var held = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal(TriggerStates.Blocked, held.TriggerState);
        Assert.Equal(due, held.NextFireTimeUtc);
        Assert.Null(await Storage.GetFiredTriggerAsync(acquired.FireInstanceId!));
        Assert.Equal(first.Trigger.FireInstanceId, Assert.Single(await Storage.GetAllFiredTriggersAsync()).EntryId);
        Assert.True(log.Contains("held until its running firing on node-a ends"));
        Assert.Empty(await AcquireDueAsync(nodeB));
        Assert.Empty(await AcquireDueAsync(nodeA));

        await CompleteAsync(nodeA, first, ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);

        var (_, fired) = await AcquireAndFireAsync(nodeB);
        Assert.NotNull(fired.TriggerFiredBundle);
        Assert.Null(fired.TriggerFiredBundle!.SupersededFireInstanceIds);
        Assert.Equal(trigger.Key, fired.TriggerFiredBundle.Trigger.Key);
    }

    [Fact]
    public async Task CancelPrevious_with_firings_on_this_node_and_another_waits_and_interrupts_nothing()
    {
        var nodeA = await NewNodeAsync("node-a");
        await ScheduleAsync(nodeA, OverlapPolicy.CancelPrevious);
        var local = await FireOneAsync(nodeA);
        await StoreForeignFiringAsync("node-b", OverlapPolicy.CancelPrevious);

        var (_, result) = await AcquireAndFireAsync(nodeA);

        Assert.True(result.IsDeclined);
        Assert.Equal(TriggerStates.Blocked, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);

        var records = await Storage.GetAllFiredTriggersAsync();
        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.EntryId == local.Trigger.FireInstanceId && r.State == FiredTriggerStates.Executing);
    }

    [Fact]
    public async Task CancelPrevious_given_to_a_trigger_while_a_firing_under_another_policy_runs_elsewhere_fires_beside_it()
    {
        var nodeA = await NewNodeAsync("node-a");
        var nodeB = await NewNodeAsync("node-b");
        var job = Job("job");
        await nodeA.ScheduleJob(job, Build("t", job.Key, OverlapPolicy.Default, DateTimeOffset.UtcNow.AddSeconds(-3), TimeSpan.FromSeconds(1)));
        var running = await FireOneAsync(nodeB);

        await nodeA.AddTrigger(
            Build("t", job.Key, OverlapPolicy.CancelPrevious, DateTimeOffset.UtcNow.AddSeconds(-3), TimeSpan.FromSeconds(1)),
            new AddTriggerOptions { Replace = true });

        Assert.True((await Storage.GetTriggerAsync("tests", "t"))!.OverlapPolicyUnsettled);

        var (_, beside) = await AcquireAndFireAsync(nodeA);

        Assert.NotNull(beside.TriggerFiredBundle);
        Assert.Null(beside.TriggerFiredBundle!.SupersededFireInstanceIds);
        Assert.True((await Storage.GetTriggerAsync("tests", "t"))!.OverlapPolicyUnsettled);

        await CompleteAsync(nodeB, running, ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);
        await CompleteAsync(nodeA, beside.TriggerFiredBundle, ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);

        var (_, settled) = await AcquireAndFireAsync(nodeA);

        Assert.NotNull(settled.TriggerFiredBundle);
        Assert.False((await Storage.GetTriggerAsync("tests", "t"))!.OverlapPolicyUnsettled);
    }

    [Fact]
    public async Task CancelPrevious_replaced_by_CancelPrevious_while_running_stays_settled_and_one_with_nothing_running_is_settled()
    {
        var nodeA = await NewNodeAsync("node-a");
        var nodeB = await NewNodeAsync("node-b");
        var job = Job("job");
        var first = Build("t", job.Key, OverlapPolicy.CancelPrevious, DateTimeOffset.UtcNow.AddSeconds(-3), TimeSpan.FromSeconds(1));
        await nodeA.ScheduleJob(job, first);
        Assert.False((await Storage.GetTriggerAsync("tests", "t"))!.OverlapPolicyUnsettled);

        await FireOneAsync(nodeB);
        await nodeA.AddTrigger(
            Build("t", job.Key, OverlapPolicy.CancelPrevious, DateTimeOffset.UtcNow.AddSeconds(-3), TimeSpan.FromSeconds(1)),
            new AddTriggerOptions { Replace = true });

        Assert.False((await Storage.GetTriggerAsync("tests", "t"))!.OverlapPolicyUnsettled);

        var (_, result) = await AcquireAndFireAsync(nodeA);
        Assert.True(result.IsDeclined);

        var idle = Build("idle", job.Key, OverlapPolicy.Default, DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromHours(1));
        await nodeA.AddTrigger(idle);
        await nodeA.ReplaceTrigger(idle.Key, Build("idle", job.Key, OverlapPolicy.CancelPrevious, DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromHours(1)));

        Assert.False((await Storage.GetTriggerAsync("tests", "idle"))!.OverlapPolicyUnsettled);
    }

    [Fact]
    public async Task CancelPrevious_held_in_a_group_paused_meanwhile_is_stored_paused_and_blocked_and_released_to_paused()
    {
        var nodeA = await NewNodeAsync("node-a");
        var nodeB = await NewNodeAsync("node-b");
        var trigger = await ScheduleAsync(nodeA, OverlapPolicy.CancelPrevious);
        var first = await FireOneAsync(nodeA);

        var acquired = Assert.Single(await AcquireDueAsync(nodeB));
        await Storage.AddPausedTriggerGroupAsync("tests");
        var result = Assert.Single(await nodeB.TriggersFired(new[] { acquired }));

        Assert.True(result.IsDeclined);
        Assert.Equal(TriggerStates.PausedAndBlocked, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);

        await CompleteAsync(nodeA, first, ExecutionOutcome.Succeeded, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerStates.Paused, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        Assert.Equal(TriggerState.Paused, await nodeA.GetTriggerState(trigger.Key));
    }

    [Fact]
    public async Task CancelPrevious_trigger_held_behind_a_node_that_died_is_released_by_recovery()
    {
        var dead = await NewNodeAsync("node-dead");
        var survivor = await NewNodeAsync("node-b");
        var trigger = await ScheduleAsync(dead, OverlapPolicy.CancelPrevious);
        await FireOneAsync(dead);

        var (_, result) = await AcquireAndFireAsync(survivor);
        Assert.True(result.IsDeclined);
        Assert.Equal(TriggerStates.Blocked, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);

        await CheckInAsync("node-dead", DateTimeOffset.UtcNow.AddMinutes(-5));
        await survivor.RecoverStaleSchedulerInstancesAsync();

        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);

        var (_, fired) = await AcquireAndFireAsync(survivor);
        Assert.NotNull(fired.TriggerFiredBundle);
        Assert.Equal(trigger.Key, fired.TriggerFiredBundle!.Trigger.Key);
    }

    [Fact]
    public async Task CancelPrevious_does_not_supersede_for_a_retry()
    {
        var store = await NewNodeAsync();
        await ScheduleAsync(store, OverlapPolicy.CancelPrevious);
        await FireOneAsync(store);

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        row.RetryAttempt = 2;
        await Storage.UpdateTriggerAsync(row);

        var (_, retry) = await AcquireAndFireAsync(store);

        Assert.NotNull(retry.TriggerFiredBundle);
        Assert.Null(retry.TriggerFiredBundle!.SupersededFireInstanceIds);
    }

    [Fact]
    public async Task A_skip_whose_write_fails_changes_nothing_and_the_scheduler_release_puts_the_trigger_back()
    {
        var signaler = new RecordingSignaler();
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewNodeAsync(cache: cache, signaler: signaler);
        await ScheduleAsync(store, OverlapPolicy.Skip);

        var first = await FireOneAsync(store);
        var dueBefore = (await Storage.GetTriggerAsync("tests", "t"))!.NextFireTimeUtc;
        var acquired = Assert.Single(await AcquireDueAsync(store));

        control.FailWhen = FaultInjectingCache.Times(1, FaultInjectingCache.Call("InsertBulk"));
        await Assert.ThrowsAsync<JobPersistenceException>(() => store.TriggersFired(new[] { acquired }).AsTask());
        control.FailWhen = null;

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal(TriggerStates.Acquired, row.TriggerState);
        Assert.Equal(dueBefore, row.NextFireTimeUtc);
        Assert.NotNull(await Storage.GetFiredTriggerAsync(acquired.FireInstanceId!));
        Assert.Empty(signaler.Skipped);

        await store.ReleaseAcquiredTrigger(acquired);

        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        Assert.Equal(first.Trigger.FireInstanceId, Assert.Single(await Storage.GetAllFiredTriggersAsync()).EntryId);
    }

    [Fact]
    public async Task A_skip_whose_acquisition_record_cannot_be_removed_is_cleaned_up_by_the_scheduler_release()
    {
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewNodeAsync(cache: cache);
        await ScheduleAsync(store, OverlapPolicy.Skip);

        var first = await FireOneAsync(store);
        var dropped = (await Storage.GetTriggerAsync("tests", "t"))!.NextFireTimeUtc!.Value;
        var acquired = Assert.Single(await AcquireDueAsync(store));

        control.FailWhen = FaultInjectingCache.Times(1, (method, args) =>
            method.Name == "RemoveBulk" && args is { Length: > 0 } && args[0] is IEnumerable<string> keys && keys.Any(k => k.Contains(":fired:", StringComparison.Ordinal)));
        await Assert.ThrowsAsync<JobPersistenceException>(() => store.TriggersFired(new[] { acquired }).AsTask());
        control.FailWhen = null;

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal(TriggerStates.Waiting, row.TriggerState);
        Assert.Equal(dropped.AddSeconds(1), row.NextFireTimeUtc!.Value);
        Assert.NotNull(await Storage.GetFiredTriggerAsync(acquired.FireInstanceId!));

        await store.ReleaseAcquiredTrigger(acquired);

        Assert.Equal(first.Trigger.FireInstanceId, Assert.Single(await Storage.GetAllFiredTriggersAsync()).EntryId);
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        Assert.Equal(dropped.AddSeconds(1), (await Storage.GetTriggerAsync("tests", "t"))!.NextFireTimeUtc!.Value);
    }

    [Fact]
    public async Task A_hold_whose_write_fails_leaves_the_trigger_acquired_for_the_release()
    {
        var nodeA = await NewNodeAsync("node-a");
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var nodeB = await NewNodeAsync("node-b", cache: cache);
        await ScheduleAsync(nodeA, OverlapPolicy.CancelPrevious);
        await FireOneAsync(nodeA);

        var acquired = Assert.Single(await AcquireDueAsync(nodeB));
        control.FailWhen = FaultInjectingCache.Times(1, FaultInjectingCache.Call("InsertBulk"));
        await Assert.ThrowsAsync<JobPersistenceException>(() => nodeB.TriggersFired(new[] { acquired }).AsTask());
        control.FailWhen = null;

        Assert.Equal(TriggerStates.Acquired, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);

        await nodeB.ReleaseAcquiredTrigger(acquired);

        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        Assert.Single(await Storage.GetAllFiredTriggersAsync());
    }

    [Theory]
    [InlineData(OverlapPolicy.Default)]
    [InlineData(OverlapPolicy.Skip)]
    [InlineData(OverlapPolicy.BufferOne)]
    [InlineData(OverlapPolicy.CancelPrevious)]
    [InlineData(OverlapPolicy.AllowAll)]
    public async Task The_policy_is_stored_listed_and_survives_a_reschedule(OverlapPolicy policy)
    {
        var store = await NewNodeAsync();
        var job = Job("job");
        var trigger = Build("t", job.Key, policy, DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);

        Assert.Equal((int) policy, (await Storage.GetTriggerAsync("tests", "t"))!.OverlapPolicy);
        Assert.Equal(policy, (await store.GetTrigger(trigger.Key))!.OverlapPolicy);
        Assert.Equal(policy, Assert.Single((await store.QueryTriggers(new TriggerQuery())).Items).OverlapPolicy);

        await store.ReplaceTrigger(trigger.Key, Build("t", job.Key, policy, DateTimeOffset.UtcNow.AddHours(2), TimeSpan.FromHours(1)));

        Assert.Equal(policy, (await store.GetTrigger(trigger.Key))!.OverlapPolicy);
    }

    [Fact]
    public void The_mapper_reads_a_policy_it_does_not_know_as_Default()
    {
        var trigger = Build("t", new JobKey("j", "g"), OverlapPolicy.BufferOne, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
        var row = TriggerMapper.ToModel(trigger, SchedulerName, TriggerStates.Waiting);

        Assert.Equal((int) OverlapPolicy.BufferOne, row.OverlapPolicy);
        Assert.Equal(OverlapPolicy.BufferOne, TriggerMapper.ToQuartzTrigger(row).OverlapPolicy);

        row.OverlapPolicy = 99;
        Assert.Equal(OverlapPolicy.Default, TriggerMapper.ToQuartzTrigger(row).OverlapPolicy);

        row.OverlapPolicy = -1;
        Assert.Equal(OverlapPolicy.Default, TriggerMapper.ToQuartzTrigger(row).OverlapPolicy);
    }

    [Fact]
    public async Task The_firing_record_carries_the_policy_the_firing_started_under()
    {
        var store = await NewNodeAsync();
        await ScheduleAsync(store, OverlapPolicy.BufferOne);

        var bundle = await FireOneAsync(store);

        Assert.Equal((int) OverlapPolicy.BufferOne, (await Storage.GetFiredTriggerAsync(bundle.Trigger.FireInstanceId!))!.OverlapPolicy);
    }

    private async Task<IOperableTrigger> ScheduleAsync(NCacheJobStore store, OverlapPolicy policy)
    {
        var job = Job("job");
        var trigger = Build("t", job.Key, policy, DateTimeOffset.UtcNow.AddSeconds(-3), TimeSpan.FromSeconds(1));
        await store.ScheduleJob(job, trigger);
        return trigger;
    }

    private static IOperableTrigger Build(string name, JobKey job, OverlapPolicy policy, DateTimeOffset at, TimeSpan every, int? repeatCount = null)
    {
        var trigger = (IOperableTrigger) TriggerBuilder.Create()
            .WithIdentity(name, "tests")
            .ForJob(job)
            .StartAt(at)
            .WithSimpleSchedule(s =>
            {
                s.WithInterval(every);
                if (repeatCount is { } count) s.WithRepeatCount(count); else s.RepeatForever();
            })
            .WithOverlapPolicy(policy)
            .Build();

        trigger.ComputeFirstFireTimeUtc(null);
        return trigger;
    }

    private static IOperableTrigger OneShotWithPolicy(string name, JobKey job, OverlapPolicy policy)
    {
        var trigger = (IOperableTrigger) TriggerBuilder.Create()
            .WithIdentity(name, "tests")
            .ForJob(job)
            .StartAt(DateTimeOffset.UtcNow.AddSeconds(-1))
            .WithSimpleSchedule(s => s.WithRepeatCount(0))
            .WithOverlapPolicy(policy)
            .Build();

        trigger.ComputeFirstFireTimeUtc(null);
        return trigger;
    }

    private static async Task<(IOperableTrigger Acquired, TriggerFiredResult Result)> AcquireAndFireAsync(NCacheJobStore store)
    {
        var acquired = Assert.Single(await AcquireDueAsync(store));
        var result = Assert.Single(await store.TriggersFired(new[] { acquired }));
        return (acquired, result);
    }

    private static Task CompleteAsync(NCacheJobStore store, TriggerFiredBundle bundle, ExecutionOutcome outcome, SchedulerInstruction instruction)
        => store.FiringComplete(new TriggeredJobCompleteContext
        {
            Trigger = bundle.Trigger,
            JobDetail = bundle.JobDetail,
            Instruction = instruction,
            Outcome = outcome
        }).AsTask();

    private async Task StoreForeignFiringAsync(string instanceName, OverlapPolicy policy)
    {
        await CheckInAsync(instanceName, DateTimeOffset.UtcNow);
        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = Guid.NewGuid().ToString("N"),
            TriggerName = "t",
            TriggerGroup = "tests",
            InstanceName = instanceName,
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = DateTimeOffset.UtcNow,
            State = FiredTriggerStates.Executing,
            JobName = "job",
            JobGroup = "tests",
            OverlapPolicy = (int) policy
        });
    }

    private sealed class RecordingSignaler : ISchedulerSignaler
    {
        public ConcurrentQueue<ITrigger> Skipped { get; } = new();
        public ConcurrentQueue<TriggerKey> Misfired { get; } = new();
        public ConcurrentQueue<TriggerKey> Finalized { get; } = new();
        public ConcurrentQueue<DateTimeOffset?> SchedulingChanges { get; } = new();

        public ValueTask NotifyTriggerListenersMisfired(ITrigger trigger, CancellationToken cancellationToken = default)
        {
            Misfired.Enqueue(trigger.Key);
            return default;
        }

        public ValueTask NotifyTriggerListenersSkipped(ITrigger trigger, CancellationToken cancellationToken = default)
        {
            Skipped.Enqueue(trigger);
            return default;
        }

        public ValueTask NotifySchedulerListenersFinalized(ITrigger trigger, CancellationToken cancellationToken = default)
        {
            Finalized.Enqueue(trigger.Key);
            return default;
        }

        public ValueTask NotifySchedulerListenersJobDeleted(JobKey jobKey, CancellationToken cancellationToken = default) => default;

        public ValueTask NotifySchedulerListenersTriggerInError(TriggerKey triggerKey, CancellationToken cancellationToken = default) => default;

        public ValueTask NotifySchedulerListenersTriggersInError(JobKey jobKey, CancellationToken cancellationToken = default) => default;

        public ValueTask SignalSchedulingChange(DateTimeOffset? candidateNewNextFireTimeUtc, CancellationToken cancellationToken = default)
        {
            SchedulingChanges.Enqueue(candidateNewNextFireTimeUtc);
            return default;
        }

        public ValueTask NotifySchedulerListenersError(SchedulerErrorContext errorContext, CancellationToken cancellationToken = default) => default;
    }
}
