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
public sealed class NCacheQuartzNonConcurrentJobUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task BlockSiblingTriggers_blocks_waiting_sibling()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var executingTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(executingTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);
        await Index.UpsertAsync(siblingTrigger);

        var trigger = await AcquireForTestAsync(executingTrigger);

        var results = await store.TriggersFired(new[] { trigger });

        Assert.Single(results);
        Assert.NotEqual(TriggerFiredResult.NotFired, results[0]);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Blocked, stored.TriggerState);
    }

    [Fact]
    public async Task BlockSiblingTriggers_changes_paused_sibling_to_paused_and_blocked()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var executingTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));
        siblingTrigger.TriggerState = TriggerStates.Paused;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(executingTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var trigger = await AcquireForTestAsync(executingTrigger);

        var results = await store.TriggersFired(new[] { trigger });

        Assert.Single(results);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.PausedAndBlocked, stored.TriggerState);
    }

    [Fact]
    public async Task BlockSiblingTriggers_ignores_acquired_sibling()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var executingTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var siblingTrigger = CreateAcquiredTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(executingTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var trigger = await AcquireForTestAsync(executingTrigger);

        await store.TriggersFired(new[] { trigger });

        var stored = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Acquired, stored.TriggerState);
    }

    [Fact]
    public async Task BlockSiblingTriggers_ignores_complete_sibling()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var executingTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));
        siblingTrigger.TriggerState = TriggerStates.Complete;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(executingTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var trigger = await AcquireForTestAsync(executingTrigger);

        await store.TriggersFired(new[] { trigger });

        var stored = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Complete, stored.TriggerState);
    }

    [Fact]
    public async Task BlockSiblingTriggers_ignores_error_sibling()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var executingTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));
        siblingTrigger.TriggerState = TriggerStates.Error;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(executingTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var trigger = await AcquireForTestAsync(executingTrigger);

        await store.TriggersFired(new[] { trigger });

        var stored = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Error, stored.TriggerState);
    }

    [Fact]
    public async Task BlockSiblingTriggers_ignores_trigger_from_different_job()
    {
        var store = CreateStore("instance-1");

        var job1 = CreateJob("job-1", nonConcurrent: true);
        var job2 = CreateJob("job-2");
        var executingTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var differentJobTrigger = CreateWaitingTrigger("trigger-2", "job-2", DateTimeOffset.UtcNow.AddMinutes(1));

        await Storage.StoreJobAsync(job1, replaceExisting: false);
        await Storage.StoreJobAsync(job2, replaceExisting: false);
        await Storage.StoreTriggerAsync(executingTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(differentJobTrigger, replaceExisting: false);

        var trigger = await AcquireForTestAsync(executingTrigger);

        await store.TriggersFired(new[] { trigger });

        var stored = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
    }

    [Fact]
    public async Task TriggersFired_blocks_the_executing_trigger_of_a_non_concurrent_job_and_keeps_it_out_of_the_index()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var executingTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 5);

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(executingTrigger, replaceExisting: false);

        var trigger = await AcquireForTestAsync(executingTrigger);

        var results = await store.TriggersFired(new[] { trigger });

        Assert.NotEqual(TriggerFiredResult.NotFired, Assert.Single(results));

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Blocked, stored.TriggerState);
        Assert.NotNull(stored.NextFireTimeUtc);

        var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddHours(1), 10);
        Assert.DoesNotContain(candidates, x => x.TriggerName == "trigger-1");
    }

    [Fact]
    public async Task TriggersFired_of_a_concurrent_job_leaves_the_executing_trigger_waiting()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: false);
        var executingTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 5);

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(executingTrigger, replaceExisting: false);

        var trigger = await AcquireForTestAsync(executingTrigger);

        await store.TriggersFired(new[] { trigger });

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);

        var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddHours(1), 10);
        Assert.Contains(candidates, x => x.TriggerName == "trigger-1");
    }

    [Fact]
    public async Task BlockSiblingTriggers_updates_index_for_blocked_sibling()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var executingTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(executingTrigger, replaceExisting: false);
        await Index.UpsertAsync(siblingTrigger);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var trigger = await AcquireForTestAsync(executingTrigger);

        await store.TriggersFired(new[] { trigger });

        var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(2), 10);

        Assert.DoesNotContain(candidates, x => x.TriggerGroup == "tests" && x.TriggerName == "trigger-2");
    }

    [Fact]
    public async Task UnblockSiblingTriggers_changes_blocked_sibling_to_waiting()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var completedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));
        siblingTrigger.TriggerState = TriggerStates.Blocked;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
    }

    [Fact]
    public async Task UnblockSiblingTriggers_changes_paused_and_blocked_sibling_to_paused()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var completedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));
        siblingTrigger.TriggerState = TriggerStates.PausedAndBlocked;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Paused, stored.TriggerState);
    }

    [Fact]
    public async Task UnblockSiblingTriggers_ignores_waiting_sibling()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var completedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
    }

    [Fact]
    public async Task UnblockSiblingTriggers_ignores_complete_sibling()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var completedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));
        siblingTrigger.TriggerState = TriggerStates.Complete;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Complete, stored.TriggerState);
    }

    [Fact]
    public async Task UnblockSiblingTriggers_ignores_error_sibling()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var completedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));
        siblingTrigger.TriggerState = TriggerStates.Error;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Error, stored.TriggerState);
    }

    [Fact]
    public async Task UnblockSiblingTriggers_ignores_completed_trigger_itself()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var completedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));
        siblingTrigger.TriggerState = TriggerStates.Blocked;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.SetTriggerComplete);

        var storedCompleted = await Storage.GetTriggerAsync("tests", "trigger-1");
        var storedSibling = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(storedCompleted);
        Assert.NotNull(storedSibling);
        Assert.Equal(TriggerStates.Complete, storedCompleted.TriggerState);
        Assert.Equal(TriggerStates.Waiting, storedSibling.TriggerState);
    }

    [Fact]
    public async Task UnblockSiblingTriggers_applies_misfire_to_unblocked_sibling()
    {
        var store = CreateStore("instance-1");
        store.MisfireThreshold = TimeSpan.FromSeconds(1);

        var job = CreateJob("job-1", nonConcurrent: true);
        var completedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-2));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(-10));
        siblingTrigger.TriggerState = TriggerStates.Blocked;
        siblingTrigger.MisfireInstruction = 0;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored);
        Assert.True(stored.NextFireTimeUtc > DateTimeOffset.UtcNow.AddSeconds(-2));
    }

    [Fact]
    public async Task UnblockSiblingTriggers_updates_index_for_unblocked_sibling()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var completedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));
        siblingTrigger.TriggerState = TriggerStates.Blocked;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(2), 10);

        Assert.Contains(candidates, x => x.TriggerGroup == "tests" && x.TriggerName == "trigger-2");
    }

    [Fact]
    public async Task UnblockSiblingTriggers_preserves_paused_state_after_unblocking()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var completedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));
        siblingTrigger.TriggerState = TriggerStates.PausedAndBlocked;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Paused, stored.TriggerState);
    }

    [Fact]
    public async Task NonConcurrent_job_with_only_one_trigger_is_unblocked_when_its_execution_completes()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var trigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 5);

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(trigger, replaceExisting: false);

        var quartzTrigger = await AcquireForTestAsync(trigger);

        var results = await store.TriggersFired(new[] { quartzTrigger });

        Assert.Single(results);
        Assert.NotEqual(TriggerFiredResult.NotFired, results[0]);
        Assert.Equal(TriggerStates.Blocked, (await Storage.GetTriggerAsync("tests", "trigger-1"))!.TriggerState);

        await store.TriggeredJobComplete(quartzTrigger, Mapping.JobMapper.ToQuartzJob(job), SchedulerInstruction.NoInstruction);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
        Assert.Null(await Storage.GetFiredTriggerAsync(quartzTrigger.FireInstanceId!));

        var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddHours(1), 10);
        Assert.Contains(candidates, x => x.TriggerName == "trigger-1");
    }

    [Fact]
    public async Task NonConcurrent_trigger_blocked_during_execution_is_never_acquired_by_another_node()
    {
        var nodeA = CreateStore("instance-a");
        var nodeB = CreateStore("instance-b");

        var job = CreateJob("job-1", nonConcurrent: true);
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: -1);
        trigger.Simple!.RepeatInterval = TimeSpan.FromMilliseconds(1);

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = Assert.Single(await nodeA.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddSeconds(1), 5)));
        var fired = Assert.Single(await nodeA.TriggersFired(new[] { acquired }));
        Assert.NotNull(fired.TriggerFiredBundle);

        await Task.Delay(50);
        Assert.Empty(await nodeB.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddSeconds(1), 5)));
        Assert.Empty(await nodeA.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddSeconds(1), 5)));

        await nodeA.TriggeredJobComplete(acquired, fired.TriggerFiredBundle!.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.Single(await nodeB.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddSeconds(1), 5)));
    }
}

