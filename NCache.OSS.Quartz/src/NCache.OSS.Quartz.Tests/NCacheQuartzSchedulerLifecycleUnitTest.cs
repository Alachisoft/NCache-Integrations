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
public sealed class NCacheQuartzSchedulerLifecycleUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task SchedulerStarted_stores_scheduler_identity()
    {
        var store = new NCacheJobStore(Storage, Index, Locks);
        var identity = new SchedulerIdentity { SchedulerName = SchedulerName, InstanceId = "g01" };

        await store.Initialize(identity);
        await store.SchedulerStarted();

        var nodes = await store.QueryClusterNodes();
        var current = Assert.Single(nodes);

        Assert.Equal("g01", current.InstanceId);
        Assert.True(current.IsCurrentNode);
    }

    [Fact]
    public async Task Initialize_rejects_null_identity()
    {
        var store = new NCacheJobStore(Storage, Index, Locks);

        await Assert.ThrowsAnyAsync<ArgumentNullException>(async () => await store.Initialize(null!));
    }

    [Fact]
    public async Task Initialize_honors_cancellation()
    {
        var store = new NCacheJobStore(Storage, Index, Locks);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var identity = new SchedulerIdentity { SchedulerName = SchedulerName, InstanceId = "g03" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.Initialize(identity, cts.Token));
    }

    [Fact]
    public async Task SchedulerStarted_repairs_registries()
    {
        var instanceId = $"g04-{Guid.NewGuid():N}";
        var store = CreateStore(instanceId);

        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));

        await StoreJobAndTriggerAsync(job, trigger);

        _cache!.Remove(NCacheKeys.JobsInGroup(_prefix, SchedulerName, job.JobGroup));
        _cache.Remove(NCacheKeys.TriggersInGroup(_prefix, SchedulerName, trigger.TriggerGroup));
        _cache.Remove(NCacheKeys.JobGroups(_prefix, SchedulerName));
        _cache.Remove(NCacheKeys.TriggerGroups(_prefix, SchedulerName));

        await store.SchedulerStarted();

        var jobs = await store.QueryJobs(new JobQuery { Take = 100 });
        var triggers = await store.QueryTriggers(new TriggerQuery { Take = 100 });

        Assert.Contains(jobs.Items, x => x.Key == new JobKey(job.JobName, job.JobGroup));
        Assert.Contains(triggers.Items, x => x.Key == new TriggerKey(trigger.TriggerName, trigger.TriggerGroup));

        await store.Shutdown();
    }

    [Fact]
    public async Task SchedulerStarted_rebuilds_next_fire_time_index()
    {
        var instanceId = $"g05-{Guid.NewGuid():N}";
        var store = CreateStore(instanceId);

        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddSeconds(2));

        await StoreJobAndTriggerAsync(job, trigger);
        await Index.RemoveAsync(trigger);

        var before = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10);
        Assert.DoesNotContain(before, x => x.TriggerName == trigger.TriggerName);

        await store.SchedulerStarted();

        var after = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10);
        Assert.Contains(after, x => x.TriggerName == trigger.TriggerName);

        await store.Shutdown();
    }

    [Fact]
    public async Task SchedulerStarted_checks_in_scheduler_state()
    {
        var instanceId = $"g06-{Guid.NewGuid():N}";
        var store = CreateStore(instanceId);

        await store.SchedulerStarted();

        var nodes = await store.QueryClusterNodes();
        var current = Assert.Single(nodes);

        Assert.Equal(instanceId, current.InstanceId);
        Assert.True(current.IsCurrentNode);
        Assert.Equal(ClusterNodeState.Alive, current.State);
        Assert.NotNull(current.LastCheckInUtc);
        Assert.NotNull(current.CheckInInterval);

        await store.Shutdown();
    }

    private NCacheJobStore CreateFastClusterStore(string instanceId)
    {
        var store = CreateStore(instanceId);
        store.ClusterCheckinInterval = TimeSpan.FromSeconds(1);
        store.ClusterCheckinMisfireThreshold = TimeSpan.FromSeconds(2);
        return store;
    }

    private static Task WaitUntilStaleAsync() => Task.Delay(TimeSpan.FromMilliseconds(4500));

    [Fact]
    public async Task SchedulerStarted_starts_heartbeat()
    {
        var instanceId = $"g07-{Guid.NewGuid():N}";
        var store = CreateFastClusterStore(instanceId);

        await store.SchedulerStarted();

        var initial = Assert.Single(await store.QueryClusterNodes());
        var initialCheckIn = initial.LastCheckInUtc;

        Assert.NotNull(initialCheckIn);
        Assert.Equal(TimeSpan.FromSeconds(1), initial.CheckInInterval);

        await Task.Delay(TimeSpan.FromMilliseconds(2500));

        var refreshed = Assert.Single(await store.QueryClusterNodes());

        Assert.NotNull(refreshed.LastCheckInUtc);
        Assert.True(refreshed.LastCheckInUtc > initialCheckIn);

        await store.Shutdown();
    }

    [Fact]
    public async Task SchedulerStarted_recovers_stale_scheduler_instances()
    {
        var staleInstanceId = $"g08-stale-{Guid.NewGuid():N}";
        var currentInstanceId = $"g08-current-{Guid.NewGuid():N}";

        var staleStore = CreateFastClusterStore(staleInstanceId);
        await staleStore.SchedulerStarted();
        await staleStore.StopHeartbeatAsync();

        await WaitUntilStaleAsync();

        var currentStore = CreateFastClusterStore(currentInstanceId);
        await currentStore.SchedulerStarted();

        var nodes = await currentStore.QueryClusterNodes();

        Assert.DoesNotContain(nodes, x => x.InstanceId == staleInstanceId);
        Assert.Contains(nodes, x => x.InstanceId == currentInstanceId);
        Assert.Null(await Storage.GetSchedulerStateAsync(staleInstanceId));

        await currentStore.Shutdown();
    }

    [Fact]
    public async Task Heartbeat_recovers_a_peer_that_dies_while_this_node_keeps_running()
    {
        var deadInstanceId = $"g08b-dead-{Guid.NewGuid():N}";
        var survivorId = $"g08b-survivor-{Guid.NewGuid():N}";

        var survivor = CreateFastClusterStore(survivorId);
        await survivor.SchedulerStarted();

        var dead = CreateFastClusterStore(deadInstanceId);
        await dead.SchedulerStarted();

        var job = CreateJob("job-1", requestsRecovery: true);
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 0);
        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = Assert.Single(await dead.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 1)));
        Assert.NotNull(Assert.Single(await dead.TriggersFired(new[] { acquired })).TriggerFiredBundle);

        await dead.StopHeartbeatAsync();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        NCacheTriggerData? recovery = null;
        var deadStillRegistered = true;

        while (DateTimeOffset.UtcNow < deadline && (recovery is null || deadStillRegistered))
        {
            await Task.Delay(250);
            recovery = (await Storage.GetTriggersInGroupAsync(SchedulerConstants.DefaultRecoveryGroup))
                .Select(k => Storage.GetTriggerAsync(k.Group, k.Name).GetAwaiter().GetResult())
                .FirstOrDefault();
            deadStillRegistered = (await Storage.GetRegisteredSchedulerInstanceNamesAsync()).Contains(deadInstanceId, StringComparer.Ordinal);
        }

        Assert.NotNull(recovery);
        Assert.Equal("job-1", recovery!.JobName);
        Assert.Equal(TriggerStates.Waiting, recovery.TriggerState);
        Assert.Null(await Storage.GetFiredTriggerAsync(acquired.FireInstanceId!));
        Assert.DoesNotContain(await survivor.QueryClusterNodes(), n => n.InstanceId == deadInstanceId);

        var recovered = Assert.Single(await survivor.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 5)));
        var bundle = Assert.Single(await survivor.TriggersFired(new[] { recovered })).TriggerFiredBundle;
        Assert.NotNull(bundle);
        Assert.True(bundle!.Recovering);
        Assert.Equal("trigger-1", bundle.Trigger.JobDataMap.GetString(SchedulerConstants.FailedJobOriginalTriggerName));

        await survivor.Shutdown();
    }

    [Fact]
    public async Task Shutdown_stops_heartbeat()
    {
        var instanceId = $"g09-{Guid.NewGuid():N}";
        var store = CreateStore(instanceId);
        await store.SchedulerStarted();
        Assert.NotNull(await Storage.GetSchedulerStateAsync(instanceId));
        await store.Shutdown();
        Assert.Null(await Storage.GetSchedulerStateAsync(instanceId));
    }

    [Fact]
    public async Task Shutdown_removes_current_scheduler_state()
    {
        var instanceId = $"g10-{Guid.NewGuid():N}";
        var store = CreateStore(instanceId);
        await store.SchedulerStarted();
        Assert.NotNull(await Storage.GetSchedulerStateAsync(instanceId));
        await store.Shutdown();
        Assert.Null(await Storage.GetSchedulerStateAsync(instanceId));
    }

    [Fact]
    public async Task Shutdown_honors_cancellation()
    {
        var instanceId = $"g11-{Guid.NewGuid():N}";
        var store = CreateStore(instanceId);

        await store.SchedulerStarted();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.Shutdown(cts.Token));

        var nodes = await store.QueryClusterNodes();
        Assert.Contains(nodes, x => x.InstanceId == instanceId);
    }

    [Fact]
    public async Task SchedulerPaused_honors_cancellation()
    {
        var store = CreateStore($"g12-{Guid.NewGuid():N}");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.SchedulerPaused(cts.Token));
    }

    [Fact]
    public async Task SchedulerResumed_honors_cancellation()
    {
        var store = CreateStore($"g13-{Guid.NewGuid():N}");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.SchedulerResumed(cts.Token));
    }

    [Fact]
    public async Task Heartbeat_re_registers_instance_if_its_registry_entry_is_lost()
    {
        var instanceId = $"g14-{Guid.NewGuid():N}";
        var store = CreateFastClusterStore(instanceId);

        await store.SchedulerStarted();

        await Storage.UnregisterSchedulerInstanceAsync(instanceId);
        Assert.DoesNotContain(instanceId, await Storage.GetRegisteredSchedulerInstanceNamesAsync());

        await Task.Delay(TimeSpan.FromMilliseconds(2500));

        Assert.Contains(instanceId, await Storage.GetRegisteredSchedulerInstanceNamesAsync());

        await store.Shutdown();
    }

    [Fact]
    public async Task Shutdown_keeps_instance_registered_while_it_still_owns_firings()
    {
        var instanceId = $"g14b-{Guid.NewGuid():N}";
        var store = CreateFastClusterStore(instanceId);

        await store.SchedulerStarted();

        var job = CreateJob("job-1", requestsRecovery: true);
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 0);
        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = Assert.Single(await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 1)));
        Assert.NotNull(Assert.Single(await store.TriggersFired(new[] { acquired })).TriggerFiredBundle);

        await store.Shutdown();

        Assert.Null(await Storage.GetSchedulerStateAsync(instanceId));
        Assert.Contains(instanceId, await Storage.GetRegisteredSchedulerInstanceNamesAsync());
        Assert.Single(await Storage.GetFiredTriggersForInstanceAsync(instanceId));

        var peer = CreateFastClusterStore($"g14b-peer-{Guid.NewGuid():N}");
        Assert.Contains(await peer.QueryClusterNodes(), n => n.InstanceId == instanceId && n.State == ClusterNodeState.Failed);
        Assert.Equal(1, await peer.RecoverStaleSchedulerInstancesAsync());

        Assert.DoesNotContain(instanceId, await Storage.GetRegisteredSchedulerInstanceNamesAsync());
        Assert.Empty(await Storage.GetFiredTriggersForInstanceAsync(instanceId));
        Assert.Single(await Storage.GetTriggersInGroupAsync(SchedulerConstants.DefaultRecoveryGroup));
    }

    [Fact]
    public async Task SchedulerStarted_recovers_this_instances_own_leftover_firings()
    {
        var instanceId = $"g14c-{Guid.NewGuid():N}";
        var first = CreateFastClusterStore(instanceId);
        await first.SchedulerStarted();

        var recoverJob = CreateJob("job-recover", requestsRecovery: true);
        var plainJob = CreateJob("job-plain", requestsRecovery: false);
        var executing = CreateWaitingTrigger("t-executing", "job-recover", DateTimeOffset.UtcNow.AddSeconds(-2), repeatCount: 0);
        var acquiredOnly = CreateWaitingTrigger("t-acquired", "job-plain", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 5);
        await StoreJobAndTriggerAsync(recoverJob, executing);
        await StoreJobAndTriggerAsync(plainJob, acquiredOnly);

        var acquired = await first.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 2));
        Assert.Equal(2, acquired.Count);

        var executingTrigger = acquired.Single(t => t.Key.Name == "t-executing");
        Assert.NotNull(Assert.Single(await first.TriggersFired(new[] { executingTrigger })).TriggerFiredBundle);

        await first.StopHeartbeatAsync();
        Assert.Equal(TriggerStates.Acquired, (await Storage.GetTriggerAsync("tests", "t-acquired"))!.TriggerState);

        var restarted = CreateFastClusterStore(instanceId);
        await restarted.SchedulerStarted();

        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "t-acquired"))!.TriggerState);
        Assert.Empty(await Storage.GetFiredTriggersForInstanceAsync(instanceId));
        Assert.Single(await Storage.GetTriggersInGroupAsync(SchedulerConstants.DefaultRecoveryGroup));
        Assert.Contains(await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10), c => c.TriggerName == "t-acquired");

        await restarted.Shutdown();
    }

    [Fact]
    public async Task RecoverStaleAcquiredFirings_releases_an_acquisition_nobody_fired()
    {
        var store = CreateStore($"g14d-{Guid.NewGuid():N}");
        store.StaleAcquiredTriggerThreshold = TimeSpan.FromMilliseconds(200);
        await Storage.RegisterSchedulerInstanceAsync(store.InstanceId);

        var job = CreateJob("job-1", nonConcurrent: true);
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 5);
        var sibling = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 5);
        await StoreJobAndTriggerAsync(job, trigger);
        await Storage.StoreTriggerAsync(sibling, replaceExisting: false);
        await Index.UpsertAsync(sibling);

        await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 2));
        Assert.Equal(TriggerStates.Acquired, (await Storage.GetTriggerAsync("tests", "trigger-1"))!.TriggerState);
        Assert.Equal(TriggerStates.Blocked, (await Storage.GetTriggerAsync("tests", "trigger-2"))!.TriggerState);

        await Task.Delay(400);

        Assert.Equal(1, await store.RecoverStaleAcquiredFiringsAsync());

        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "trigger-1"))!.TriggerState);
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "trigger-2"))!.TriggerState);
        Assert.Empty(await Storage.GetFiredTriggersForJobAsync("tests", "job-1"));
        Assert.Equal(0, await store.RecoverStaleAcquiredFiringsAsync());
    }

    [Fact]
    public async Task RecoverStaleAcquiredFirings_ignores_recent_acquisitions_and_executing_firings()
    {
        var store = CreateStore($"g14e-{Guid.NewGuid():N}");
        store.StaleAcquiredTriggerThreshold = TimeSpan.FromMinutes(5);
        await Storage.RegisterSchedulerInstanceAsync(store.InstanceId);

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 5);
        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = Assert.Single(await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 1)));
        Assert.Equal(0, await store.RecoverStaleAcquiredFiringsAsync());
        Assert.Equal(TriggerStates.Acquired, (await Storage.GetTriggerAsync("tests", "trigger-1"))!.TriggerState);

        Assert.NotNull(Assert.Single(await store.TriggersFired(new[] { acquired })).TriggerFiredBundle);
        store.StaleAcquiredTriggerThreshold = TimeSpan.FromMilliseconds(1);
        await Task.Delay(10);
        Assert.Equal(0, await store.RecoverStaleAcquiredFiringsAsync());
        var executing = Assert.Single(await Storage.GetFiredTriggersForInstanceAsync(store.InstanceId));
        Assert.Equal(FiredTriggerStates.Executing, executing.State);
    }

    [Fact]
    public async Task QueryClusterNodes_returns_current_instance()
    {
        var instanceId = $"g15-{Guid.NewGuid():N}";
        var store = CreateStore(instanceId);

        await store.SchedulerStarted();

        var nodes = await store.QueryClusterNodes();

        var current = Assert.Single(nodes, x => x.IsCurrentNode);

        Assert.Equal(instanceId, current.InstanceId);
        Assert.Equal(ClusterNodeState.Alive, current.State);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryClusterNodes_marks_live_other_instance_alive()
    {
        var currentInstanceId = $"g16-current-{Guid.NewGuid():N}";
        var otherInstanceId = $"g16-other-{Guid.NewGuid():N}";

        var currentStore = CreateStore(currentInstanceId);
        var otherStore = CreateStore(otherInstanceId);

        await otherStore.SchedulerStarted();
        await currentStore.SchedulerStarted();

        var nodes = await currentStore.QueryClusterNodes();

        var other = Assert.Single(nodes, x => x.InstanceId == otherInstanceId);

        Assert.False(other.IsCurrentNode);
        Assert.Equal(ClusterNodeState.Alive, other.State);

        await currentStore.Shutdown();
        await otherStore.Shutdown();
    }

    [Fact]
    public async Task QueryClusterNodes_marks_stale_instance_failed()
    {
        var currentInstanceId = $"g17-current-{Guid.NewGuid():N}";
        var staleInstanceId = $"g17-stale-{Guid.NewGuid():N}";

        var currentStore = CreateFastClusterStore(currentInstanceId);
        var staleStore = CreateFastClusterStore(staleInstanceId);

        await staleStore.SchedulerStarted();
        await staleStore.StopHeartbeatAsync();

        await WaitUntilStaleAsync();

        var nodes = await currentStore.QueryClusterNodes();
        var stale = Assert.Single(nodes, x => x.InstanceId == staleInstanceId);

        Assert.False(stale.IsCurrentNode);
        Assert.Equal(ClusterNodeState.Failed, stale.State);
        Assert.NotNull(stale.LastCheckInUtc);
    }

    [Fact]
    public async Task QueryClusterNodes_ignores_missing_scheduler_state()
    {
        var instanceId = $"g18-{Guid.NewGuid():N}";
        var store = CreateStore(instanceId);

        var nodes = await store.QueryClusterNodes();

        var current = Assert.Single(nodes);

        Assert.Equal(instanceId, current.InstanceId);
        Assert.True(current.IsCurrentNode);
        Assert.Equal(ClusterNodeState.Alive, current.State);
    }

    [Fact]
    public async Task QueryClusterNodes_orders_other_instances_deterministically()
    {
        var currentInstanceId = $"g19-current-{Guid.NewGuid():N}";
        var instanceA = $"g19-a-{Guid.NewGuid():N}";
        var instanceB = $"g19-b-{Guid.NewGuid():N}";

        var currentStore = CreateStore(currentInstanceId);
        var storeA = CreateStore(instanceA);
        var storeB = CreateStore(instanceB);

        await storeB.SchedulerStarted();
        await storeA.SchedulerStarted();
        await currentStore.SchedulerStarted();

        var nodes = await currentStore.QueryClusterNodes();

        Assert.Equal(currentInstanceId, nodes[0].InstanceId);

        var others = nodes.Where(x => !x.IsCurrentNode).Select(x => x.InstanceId).ToArray();

        Assert.Equal(2, others.Length);
        Assert.Equal(others.OrderBy(x => x, StringComparer.Ordinal), others);

        await currentStore.Shutdown();
        await storeA.Shutdown();
        await storeB.Shutdown();
    }

    [Fact]
    public async Task RecoverStaleSchedulerInstances_ignores_current_instance()
    {
        var instanceId = $"g20-{Guid.NewGuid():N}";
        var store = CreateStore(instanceId);

        await store.SchedulerStarted();

        var recovered = await store.RecoverStaleSchedulerInstancesAsync();

        Assert.Equal(0, recovered);

        Assert.Contains(await store.QueryClusterNodes(), x => x.InstanceId == instanceId);

        await store.Shutdown();
    }

    [Fact]
    public async Task RecoverStaleSchedulerInstances_ignores_live_instance()
    {
        var currentInstanceId = $"g21-current-{Guid.NewGuid():N}";
        var otherInstanceId = $"g21-other-{Guid.NewGuid():N}";

        var currentStore = CreateStore(currentInstanceId);
        var otherStore = CreateStore(otherInstanceId);

        await otherStore.SchedulerStarted();
        await currentStore.SchedulerStarted();

        var recovered = await currentStore.RecoverStaleSchedulerInstancesAsync();

        Assert.Equal(0, recovered);
        Assert.Contains(await currentStore.QueryClusterNodes(), x => x.InstanceId == otherInstanceId);

        await currentStore.Shutdown();
        await otherStore.Shutdown();
    }

    [Fact]
    public async Task RecoverStaleSchedulerInstances_removes_stale_scheduler_state()
    {
        var currentInstanceId = $"g22-current-{Guid.NewGuid():N}";
        var staleInstanceId = $"g22-stale-{Guid.NewGuid():N}";

        var staleStore = CreateFastClusterStore(staleInstanceId);
        await staleStore.SchedulerStarted();
        await staleStore.StopHeartbeatAsync();

        var currentStore = CreateFastClusterStore(currentInstanceId);

        Assert.Equal(0, await currentStore.RecoverStaleSchedulerInstancesAsync());
        Assert.Contains(await currentStore.QueryClusterNodes(), x => x.InstanceId == staleInstanceId);

        await WaitUntilStaleAsync();

        var recovered = await currentStore.RecoverStaleSchedulerInstancesAsync();

        Assert.Equal(0, recovered);
        Assert.DoesNotContain(await currentStore.QueryClusterNodes(), x => x.InstanceId == staleInstanceId);
        Assert.Null(await Storage.GetSchedulerStateAsync(staleInstanceId));
        Assert.DoesNotContain(staleInstanceId, await Storage.GetRegisteredSchedulerInstanceNamesAsync());
    }

    [Fact]
    public async Task RecoverStaleSchedulerInstances_resets_acquired_trigger_and_releases_automatic_pin_of_dead_node()
    {
        var deadId = $"g22b-dead-{Guid.NewGuid():N}";
        var survivorId = $"g22b-survivor-{Guid.NewGuid():N}";

        var dead = CreateFastClusterStore(deadId);
        await dead.SchedulerStarted();

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 5);
        trigger.PreferredNodeIsAutomatic = true;
        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = Assert.Single(await dead.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 1)));
        Assert.Equal(deadId, (await Storage.GetTriggerAsync("tests", "trigger-1"))!.PreferredNode);

        await dead.StopHeartbeatAsync();
        await WaitUntilStaleAsync();

        var survivor = CreateFastClusterStore(survivorId);
        Assert.Equal(0, await survivor.RecoverStaleSchedulerInstancesAsync());

        var stored = (await Storage.GetTriggerAsync("tests", "trigger-1"))!;
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
        Assert.True(stored.PreferredNodeIsAutomatic);
        Assert.Null(stored.PreferredNode);
        Assert.Null(await Storage.GetFiredTriggerAsync(acquired.FireInstanceId!));

        Assert.Single(await survivor.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 1)));
        Assert.Equal(survivorId, (await Storage.GetTriggerAsync("tests", "trigger-1"))!.PreferredNode);

        var late = await dead.TriggersFired(new[] { acquired });
        Assert.Equal(TriggerFiredResult.NotFired, Assert.Single(late));
        Assert.Equal(TriggerStates.Acquired, (await Storage.GetTriggerAsync("tests", "trigger-1"))!.TriggerState);

        await dead.ReleaseAcquiredTrigger(acquired);
        Assert.Equal(TriggerStates.Acquired, (await Storage.GetTriggerAsync("tests", "trigger-1"))!.TriggerState);
    }
}

