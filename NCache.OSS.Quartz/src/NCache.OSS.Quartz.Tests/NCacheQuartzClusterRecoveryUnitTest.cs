using NCache.OSS.Quartz.Model;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzClusterRecoveryUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task N4_a_node_that_missed_one_heartbeat_keeps_its_running_non_concurrent_job()
    {
        var nodeA = CreateStore("node-a");
        var job = ScenarioKit.Job("j", "g", nonConcurrent: true);
        var now = DateTimeOffset.UtcNow;
        await nodeA.ScheduleJob(job, ScenarioKit.Repeating("t1", "g", job.Key, now.AddSeconds(-1), TimeSpan.FromHours(1)));
        await nodeA.AddTrigger(ScenarioKit.Repeating("t2", "g", job.Key, now.AddSeconds(1), TimeSpan.FromHours(1)));
        var running = await ScenarioKit.FireOneAsync(nodeA);

        await WriteCheckInAsync("node-a", DateTimeOffset.UtcNow.AddSeconds(-20));

        var nodeB = CreateStore("node-b");
        await nodeB.RecoverStaleSchedulerInstancesAsync();

        Assert.Empty(await nodeB.AcquireNextTriggers(ScenarioKit.DueNow(5)));
        Assert.Equal(TriggerState.Blocked, await nodeB.GetTriggerState(new TriggerKey("t2", "g")));
        Assert.Contains(await Storage.GetFiredTriggersForInstanceAsync("node-a"), f => f.EntryId == running.Trigger.FireInstanceId);

        Assert.NotNull(await Storage.GetSchedulerStateAsync("node-a"));
        Assert.Contains("node-a", await Storage.GetRegisteredSchedulerInstanceNamesAsync());

        await nodeA.TriggeredJobComplete(running.Trigger, running.JobDetail, SchedulerInstruction.NoInstruction);
        Assert.Equal(TriggerState.Normal, await nodeB.GetTriggerState(new TriggerKey("t2", "g")));
    }

    [Fact]
    public async Task N4_during_the_grace_period_a_node_reserved_work_is_released_while_its_non_concurrent_execution_is_kept()
    {
        var nodeA = CreateStore("node-a");
        var now = DateTimeOffset.UtcNow;
        var nc = ScenarioKit.Job("nc", "g", nonConcurrent: true);
        await nodeA.ScheduleJob(nc, ScenarioKit.Repeating("nc-1", "g", nc.Key, now.AddSeconds(-2), TimeSpan.FromHours(1)));
        await nodeA.AddTrigger(ScenarioKit.Repeating("nc-2", "g", nc.Key, now.AddSeconds(-1), TimeSpan.FromHours(1)));
        await ScenarioKit.FireOneAsync(nodeA);

        var other = ScenarioKit.Job("other", "g");
        var reserved = ScenarioKit.Repeating("reserved", "g", other.Key, now.AddSeconds(-1), TimeSpan.FromHours(1));
        await nodeA.ScheduleJob(other, reserved);
        Assert.Equal(reserved.Key, Assert.Single(await nodeA.AcquireNextTriggers(ScenarioKit.DueNow(5))).Key);

        await WriteCheckInAsync("node-a", DateTimeOffset.UtcNow.AddSeconds(-20));

        var nodeB = CreateStore("node-b");
        await nodeB.RecoverStaleSchedulerInstancesAsync();

        var firings = await Storage.GetFiredTriggersForInstanceAsync("node-a");
        Assert.Equal("nc-1", Assert.Single(firings).TriggerName);

        Assert.Equal(reserved.Key, Assert.Single(await nodeB.AcquireNextTriggers(ScenarioKit.DueNow(5))).Key);
        Assert.Equal(TriggerState.Blocked, await nodeB.GetTriggerState(new TriggerKey("nc-2", "g")));
    }

    [Fact]
    public async Task C8_a_node_silent_beyond_the_grace_period_is_recovered_in_full()
    {
        var nodeA = CreateStore("node-a");
        var job = ScenarioKit.Job("j", "g", nonConcurrent: true);
        var now = DateTimeOffset.UtcNow;
        await nodeA.ScheduleJob(job, ScenarioKit.Repeating("t1", "g", job.Key, now.AddSeconds(-1), TimeSpan.FromHours(1)));
        await nodeA.AddTrigger(ScenarioKit.Repeating("t2", "g", job.Key, now.AddSeconds(1), TimeSpan.FromHours(1)));
        await ScenarioKit.FireOneAsync(nodeA);

        await WriteCheckInAsync("node-a", DateTimeOffset.UtcNow.AddSeconds(-40));

        var nodeB = CreateStore("node-b");
        await nodeB.RecoverStaleSchedulerInstancesAsync();

        Assert.Empty(await Storage.GetFiredTriggersForInstanceAsync("node-a"));
        Assert.Null(await Storage.GetSchedulerStateAsync("node-a"));
        Assert.DoesNotContain("node-a", await Storage.GetRegisteredSchedulerInstanceNamesAsync());
        Assert.Equal("t2", Assert.Single(await nodeB.AcquireNextTriggers(ScenarioKit.DueNow(5))).Key.Name);
    }

    [Fact]
    public async Task N4_a_node_that_checked_in_on_time_is_not_recovered()
    {
        var nodeA = CreateStore("node-a");
        var job = ScenarioKit.Job("j", "g");
        await nodeA.ScheduleJob(job, ScenarioKit.Repeating("t", "g", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1)));
        Assert.Single(await nodeA.AcquireNextTriggers(ScenarioKit.DueNow(5)));

        await WriteCheckInAsync("node-a", DateTimeOffset.UtcNow.AddSeconds(-5));

        var nodeB = CreateStore("node-b");
        Assert.Equal(0, await nodeB.RecoverStaleSchedulerInstancesAsync());

        Assert.Single(await Storage.GetFiredTriggersForInstanceAsync("node-a"));
        Assert.Empty(await nodeB.AcquireNextTriggers(ScenarioKit.DueNow(5)));
    }

    [Fact]
    public async Task N12_a_recovered_run_keeps_the_original_trigger_JobDataMap_and_not_the_job_one()
    {
        var nodeA = CreateStore("node-a");
        var job = ScenarioKit.Job("j", "g", requestsRecovery: true, data: ("jobOnly", "from-the-job"));
        var t = ScenarioKit.OneShot("t", "g", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        t.JobDataMap["orderId"] = 42;
        await nodeA.ScheduleJob(job, t);
        var run = await ScenarioKit.FireOneAsync(nodeA);

        await Storage.RegisterSchedulerInstanceAsync("node-a");

        var nodeB = CreateStore("node-b");
        Assert.Equal(1, await nodeB.RecoverStaleSchedulerInstancesAsync());

        var recovery = Assert.Single(await nodeB.GetTriggersForJob(job.Key), x => x.Key.Group == SchedulerConstants.DefaultRecoveryGroup);
        Assert.Equal(42, recovery.JobDataMap["orderId"]);
        Assert.False(recovery.JobDataMap.ContainsKey("jobOnly"));
        Assert.Equal("t", recovery.JobDataMap[SchedulerConstants.FailedJobOriginalTriggerName]);
        Assert.Equal("g", recovery.JobDataMap[SchedulerConstants.FailedJobOriginalTriggerGroup]);
        Assert.True(recovery.JobDataMap.ContainsKey(SchedulerConstants.FailedJobOriginalTriggerFireTime));
        Assert.True(recovery.JobDataMap.ContainsKey(SchedulerConstants.FailedJobOriginalTriggerScheduledFireTime));

        var recovered = await ScenarioKit.FireOneAsync(nodeB);
        Assert.True(recovered.Recovering);
        Assert.Equal(recovery.Key, recovered.Trigger.Key);
        Assert.Equal(42, recovered.Trigger.JobDataMap["orderId"]);
        Assert.Equal(run.Trigger.Key.Name, recovered.Trigger.JobDataMap[SchedulerConstants.FailedJobOriginalTriggerName]);
    }

    [Fact]
    public async Task N12_a_run_whose_trigger_is_gone_is_recovered_with_only_the_recovery_keys()
    {
        var nodeA = CreateStore("node-a");
        var job = ScenarioKit.Job("j", "g", requestsRecovery: true);
        var t = ScenarioKit.Repeating("t", "g", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        t.JobDataMap["orderId"] = 42;
        await nodeA.ScheduleJob(job, t);
        await ScenarioKit.FireOneAsync(nodeA);

        Assert.True(await nodeA.DeleteTrigger(t.Key));
        await Storage.RegisterSchedulerInstanceAsync("node-a");

        var nodeB = CreateStore("node-b");
        Assert.Equal(1, await nodeB.RecoverStaleSchedulerInstancesAsync());

        var recovery = Assert.Single(await nodeB.GetTriggersForJob(job.Key));
        Assert.Equal(SchedulerConstants.DefaultRecoveryGroup, recovery.Key.Group);
        Assert.Equal(
            new[]
            {
                SchedulerConstants.FailedJobOriginalTriggerFireTime,
                SchedulerConstants.FailedJobOriginalTriggerGroup,
                SchedulerConstants.FailedJobOriginalTriggerName,
                SchedulerConstants.FailedJobOriginalTriggerScheduledFireTime
            }.OrderBy(k => k, StringComparer.Ordinal),
            recovery.JobDataMap.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task N14_recovering_a_node_that_died_in_a_one_shot_last_run_deletes_the_trigger_and_its_non_durable_job()
    {
        var nodeA = CreateStore("node-a");
        var job = ScenarioKit.Job("j", "g", durable: false);
        var t = ScenarioKit.OneShot("t", "g", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await nodeA.ScheduleJob(job, t);
        await ScenarioKit.FireOneAsync(nodeA);
        Assert.Equal(TriggerState.Complete, await nodeA.GetTriggerState(t.Key));
        await Storage.RegisterSchedulerInstanceAsync("node-a");

        var nodeB = CreateStore("node-b");
        await nodeB.RecoverStaleSchedulerInstancesAsync();

        Assert.Equal(TriggerState.None, await nodeB.GetTriggerState(t.Key));
        Assert.Null(await nodeB.GetJob(job.Key));
        Assert.Empty((await nodeB.QueryTriggers(new TriggerQuery())).Items);
    }

    [Fact]
    public async Task N14_a_non_durable_job_whose_last_run_is_recovered_is_kept_for_its_recovery_trigger()
    {
        var nodeA = CreateStore("node-a");
        var job = ScenarioKit.Job("j", "g", durable: false, requestsRecovery: true);
        var t = ScenarioKit.OneShot("t", "g", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await nodeA.ScheduleJob(job, t);
        await ScenarioKit.FireOneAsync(nodeA);
        await Storage.RegisterSchedulerInstanceAsync("node-a");

        var nodeB = CreateStore("node-b");
        await nodeB.RecoverStaleSchedulerInstancesAsync();

        Assert.Equal(TriggerState.None, await nodeB.GetTriggerState(t.Key));
        Assert.NotNull(await nodeB.GetJob(job.Key));
        Assert.Equal(SchedulerConstants.DefaultRecoveryGroup, Assert.Single(await nodeB.GetTriggersForJob(job.Key)).Key.Group);

        var recovered = await ScenarioKit.FireOneAsync(nodeB);
        await nodeB.TriggeredJobComplete(recovered.Trigger, recovered.JobDetail, SchedulerInstruction.DeleteTrigger);
        Assert.Null(await nodeB.GetJob(job.Key));
    }

    [Fact]
    public async Task N14_a_COMPLETE_trigger_with_another_run_still_recorded_on_a_live_node_is_kept()
    {
        var trigger = CreateWaitingTrigger("t", "j", DateTimeOffset.UtcNow.AddMinutes(-1));
        trigger.TriggerState = TriggerStates.Complete;
        trigger.NextFireTimeUtc = null;
        await StoreJobAndTriggerAsync(CreateJob("j"), trigger, addToIndex: false);

        await Storage.StoreFiredTriggerAsync(Executing("dead-entry", "node-a"));
        await Storage.StoreFiredTriggerAsync(Executing("live-entry", "node-c"));
        await Storage.RegisterSchedulerInstanceAsync("node-a");
        await WriteCheckInAsync("node-c", DateTimeOffset.UtcNow);

        var nodeB = CreateStore("node-b");
        await nodeB.RecoverStaleSchedulerInstancesAsync();

        Assert.Null(await Storage.GetFiredTriggerAsync("dead-entry"));
        Assert.NotNull(await Storage.GetFiredTriggerAsync("live-entry"));
        Assert.Equal(TriggerStates.Complete, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
    }

    [Fact]
    public async Task N14_a_blocked_one_shot_that_misfires_into_nothing_is_deleted_when_unblocked()
    {
        var (store, running, once) = await BlockOneShotBehindRunAsync(SimpleTriggerMisfireInstruction.NextWithRemainingCount);

        await store.TriggeredJobComplete(running.Trigger, running.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.None, await store.GetTriggerState(once.Key));
        Assert.Null(await Storage.GetTriggerAsync(once.Key.Group, once.Key.Name));
        Assert.NotNull(await store.GetJob(running.JobDetail.Key));
    }

    [Fact]
    public async Task N14_a_blocked_one_shot_whose_misfire_leaves_it_a_fire_time_is_unblocked_and_fires()
    {
        var (store, running, once) = await BlockOneShotBehindRunAsync(SimpleTriggerMisfireInstruction.FireNow);

        await store.TriggeredJobComplete(running.Trigger, running.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(once.Key));
        Assert.Equal(once.Key, Assert.Single(await store.AcquireNextTriggers(ScenarioKit.DueNow(5))).Key);
    }

    private async Task WriteCheckInAsync(string instance, DateTimeOffset lastCheckIn)
    {
        await Storage.WriteSchedulerStateAsync(new NCacheSchedulerState
        {
            SchedulerName = SchedulerName,
            InstanceName = instance,
            LastCheckinTimeUtc = lastCheckIn,
            CheckinInterval = TimeSpan.FromSeconds(10)
        });

        await Storage.RegisterSchedulerInstanceAsync(instance);
    }

    private static NCacheFiredTriggerData Executing(string entryId, string instance) => new()
    {
        SchedulerName = SchedulerName,
        EntryId = entryId,
        TriggerName = "t",
        TriggerGroup = "tests",
        InstanceName = instance,
        FiredTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        ScheduledTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        State = FiredTriggerStates.Executing,
        JobName = "j",
        JobGroup = "tests"
    };

    private async Task<(NCacheJobStore Store, TriggerFiredBundle Running, IOperableTrigger Once)> BlockOneShotBehindRunAsync(SimpleTriggerMisfireInstruction onceMisfire)
    {
        var store = CreateStore("instance-1");
        store.MisfireThreshold = TimeSpan.FromSeconds(1);

        var job = ScenarioKit.Job("nc", "g", nonConcurrent: true);
        var now = DateTimeOffset.UtcNow;
        var main = ScenarioKit.Repeating("main", "g", job.Key, now, TimeSpan.FromHours(1));
        main.MisfireInstructionCode = (int)SimpleTriggerMisfireInstruction.IgnoreMisfires;
        var onceAt = now.AddSeconds(0.5);
        var once = ScenarioKit.OneShot("once", "g", job.Key, onceAt);
        once.MisfireInstructionCode = (int)onceMisfire;

        await store.ScheduleJob(job, main);
        await store.AddTrigger(once);

        var running = await ScenarioKit.FireOneAsync(store);
        Assert.Equal("main", running.Trigger.Key.Name);
        Assert.Equal(TriggerState.Blocked, await store.GetTriggerState(once.Key));

        var misfiredAt = onceAt + TimeSpan.FromSeconds(1.3);

        while (DateTimeOffset.UtcNow < misfiredAt)
        {
            await Task.Delay(50);
        }

        return (store, running, once);
    }
}

