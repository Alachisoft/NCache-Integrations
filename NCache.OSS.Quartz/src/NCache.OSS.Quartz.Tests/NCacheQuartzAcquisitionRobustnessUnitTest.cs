using Alachisoft.NCache.Client;
using Microsoft.Extensions.Logging;
using NCache.OSS.Quartz.Index;
using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Locking;
using NCache.OSS.Quartz.Model;
using NCache.OSS.Quartz.Storage;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzAcquisitionRobustnessUnitTest : NCacheQuartzNodeScenarioTestBase
{
    private async Task StorePinnedTriggersAsync(int count, string preferredNode, DateTimeOffset firstFireTime, string jobName = "pinned-job", string? jobClassName = null)
    {
        await Storage.StoreJobAsync(CreateJob(jobName, jobClassName: jobClassName), replaceExisting: false);

        for (var i = 0; i < count; i++)
        {
            var trigger = CreateWaitingTrigger($"pinned-{i:D3}", jobName, firstFireTime.AddMilliseconds(i * 50));
            trigger.PreferredNode = preferredNode;
            await StoreTriggerRowAsync(trigger);
        }
    }

    private async Task StoreOwnTriggerAsync(string name, DateTimeOffset fireTime, string jobName = "own-job")
    {
        if (!await Storage.JobExistsAsync("tests", jobName))
        {
            await Storage.StoreJobAsync(CreateJob(jobName), replaceExisting: false);
        }

        await StoreTriggerRowAsync(CreateWaitingTrigger(name, jobName, fireTime));
    }

    [Fact]
    public async Task N15_node_reaches_its_own_due_trigger_behind_a_full_read_of_triggers_pinned_to_a_live_node()
    {
        var now = DateTimeOffset.UtcNow;
        await CheckInAsync("node-a", now);
        var nodeB = await NewStoreAsync("node-b");

        await StorePinnedTriggersAsync(12, "node-a", now.AddSeconds(-5));
        await StoreOwnTriggerAsync("own", now.AddSeconds(-1));

        var acquired = await AcquireAsync(nodeB);

        var single = Assert.Single(acquired);
        Assert.Equal("own", single.Key.Name);
    }

    [Fact]
    public async Task N15_node_reads_past_a_pinned_head_several_times_larger_than_its_first_read()
    {
        var now = DateTimeOffset.UtcNow;
        await CheckInAsync("node-a", now);
        var nodeB = await NewStoreAsync("node-b");

        await StorePinnedTriggersAsync(40, "node-a", now.AddSeconds(-5));
        await StoreOwnTriggerAsync("own", now.AddMilliseconds(-500));

        var acquired = await AcquireAsync(nodeB);

        Assert.Equal("own", Assert.Single(acquired).Key.Name);
    }

    [Fact]
    public async Task N15_node_reaches_its_trigger_behind_a_head_of_excluded_job_types()
    {
        const string excludedType = "Some.Other.Job, Some.Other.Assembly";
        var now = DateTimeOffset.UtcNow;
        var store = await NewStoreAsync("node-1");

        await Storage.StoreJobAsync(CreateJob("excluded-job", jobClassName: excludedType), replaceExisting: false);

        for (var i = 0; i < 12; i++)
        {
            await StoreTriggerRowAsync(CreateWaitingTrigger($"excluded-{i:D2}", "excluded-job", now.AddSeconds(-5).AddMilliseconds(i * 50)));
        }

        await StoreOwnTriggerAsync("own", now.AddSeconds(-1));

        var acquired = await AcquireAsync(store, excludedJobTypes: new[] { excludedType });

        Assert.Equal("own", Assert.Single(acquired).Key.Name);

        var excluded = await Storage.GetTriggerAsync("tests", "excluded-00");
        Assert.NotNull(excluded);
        Assert.Equal(TriggerStates.Waiting, excluded.TriggerState);
    }

    [Fact]
    public async Task N15_reading_past_the_pinned_head_leaves_the_pinned_triggers_untouched_and_indexed()
    {
        var now = DateTimeOffset.UtcNow;
        await CheckInAsync("node-a", now);
        var nodeB = await NewStoreAsync("node-b");

        await StorePinnedTriggersAsync(12, "node-a", now.AddSeconds(-5));
        await StoreOwnTriggerAsync("own", now.AddSeconds(-1));

        Assert.Single(await AcquireAsync(nodeB));

        for (var i = 0; i < 12; i++)
        {
            var pinned = await Storage.GetTriggerAsync("tests", $"pinned-{i:D3}");

            Assert.NotNull(pinned);
            Assert.Equal(TriggerStates.Waiting, pinned.TriggerState);
            Assert.Equal("node-a", pinned.PreferredNode);
        }

        var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddSeconds(5), 100);
        Assert.Equal(12, candidates.Count(c => c.TriggerName.StartsWith("pinned-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task N15_a_head_made_only_of_foreign_triggers_yields_an_empty_batch()
    {
        var now = DateTimeOffset.UtcNow;
        await CheckInAsync("node-a", now);
        var nodeB = await NewStoreAsync("node-b");

        await StorePinnedTriggersAsync(15, "node-a", now.AddSeconds(-5));

        var acquired = await AcquireAsync(nodeB, maxCount: 3);

        Assert.Empty(acquired);
    }

    [Fact]
    public async Task N15_both_own_triggers_around_a_pinned_head_are_acquired_by_successive_batches()
    {
        var now = DateTimeOffset.UtcNow;
        await CheckInAsync("node-a", now);
        var nodeB = await NewStoreAsync("node-b");

        await StoreOwnTriggerAsync("own-1", now.AddSeconds(-6));
        await StorePinnedTriggersAsync(12, "node-a", now.AddSeconds(-5));
        await StoreOwnTriggerAsync("own-2", now.AddMilliseconds(-500));

        var first = await AcquireAsync(nodeB, maxCount: 2);

        Assert.Contains(first, t => t.Key.Name == "own-1");
        Assert.DoesNotContain(first, t => t.Key.Name.StartsWith("pinned-", StringComparison.Ordinal));

        var acquiredNames = first.Select(t => t.Key.Name).ToList();

        if (!acquiredNames.Contains("own-2"))
        {
            var second = await AcquireAsync(nodeB, maxCount: 2);

            Assert.DoesNotContain(second, t => t.Key.Name.StartsWith("pinned-", StringComparison.Ordinal));
            acquiredNames.AddRange(second.Select(t => t.Key.Name));
        }

        Assert.Contains("own-2", acquiredNames);
    }

    [Fact]
    public async Task N15_cluster_each_node_takes_its_own_triggers_from_a_shared_index()
    {
        var now = DateTimeOffset.UtcNow;
        var nodeA = await NewStoreAsync("node-a");
        var nodeB = await NewStoreAsync("node-b");
        await CheckInAsync("node-a", now);
        await CheckInAsync("node-b", now);

        await StorePinnedTriggersAsync(12, "node-a", now.AddSeconds(-5));
        await StoreOwnTriggerAsync("own", now.AddSeconds(-1));

        var byB = await AcquireAsync(nodeB, maxCount: 5);
        Assert.Equal("own", Assert.Single(byB).Key.Name);

        var byA = await AcquireAsync(nodeA, maxCount: 20);
        Assert.Equal(12, byA.Count);
        Assert.All(byA, t => Assert.StartsWith("pinned-", t.Key.Name, StringComparison.Ordinal));

        var fired = await nodeA.TriggersFired(byA);
        Assert.All(fired, r => Assert.NotNull(r.TriggerFiredBundle));
    }

    [Fact]
    public async Task C10_trigger_pinned_to_a_node_whose_check_in_is_long_stale_is_taken_by_another_node()
    {
        var now = DateTimeOffset.UtcNow;
        await CheckInAsync("node-gone", now.AddMinutes(-5));
        var nodeB = await NewStoreAsync("node-b");

        await StorePinnedTriggersAsync(1, "node-gone", now.AddSeconds(-1));

        var acquired = await AcquireAsync(nodeB);

        Assert.Equal("pinned-000", Assert.Single(acquired).Key.Name);
    }

    [Fact]
    public async Task C10_trigger_pinned_to_a_node_with_no_check_in_record_is_taken_by_another_node()
    {
        var nodeB = await NewStoreAsync("node-b");

        await StorePinnedTriggersAsync(1, "node-never-started", DateTimeOffset.UtcNow.AddSeconds(-1));

        Assert.Single(await AcquireAsync(nodeB));
    }

    [Fact]
    public async Task C10_a_large_head_pinned_to_a_gone_node_is_taken_in_fire_time_order()
    {
        var now = DateTimeOffset.UtcNow;
        await CheckInAsync("node-gone", now.AddMinutes(-5));
        var nodeB = await NewStoreAsync("node-b");

        await StorePinnedTriggersAsync(12, "node-gone", now.AddSeconds(-5));
        await StoreOwnTriggerAsync("own", now.AddSeconds(-1));

        var acquired = await AcquireAsync(nodeB, maxCount: 3);

        Assert.Equal(new[] { "pinned-000", "pinned-001", "pinned-002" }, acquired.Select(t => t.Key.Name).ToArray());
    }

    [Fact]
    public async Task N26a_partial_acquisition_write_releases_the_row_it_left_acquired()
    {
        var (cache, fault) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewStoreAsync("node-1", cache);

        var job = QuartzJob("j");
        var trigger = OneShot("t", "tests", job, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        fault.PartialInsertBulk = FaultInjectingCache.Times(1, FaultInjectingCache.Call("InsertBulk"));

        await Assert.ThrowsAnyAsync<Exception>(async () => await AcquireAsync(store));
        fault.PartialInsertBulk = null;

        var row = await Storage.GetTriggerAsync("tests", "t");
        Assert.NotNull(row);
        Assert.Equal(TriggerStates.Waiting, row.TriggerState);
        Assert.Empty(await Storage.GetFiredTriggersForInstanceAsync("node-1"));
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));

        var other = await NewStoreAsync("node-2");
        var acquired = await AcquireAsync(other);

        Assert.Equal("t", Assert.Single(acquired).Key.Name);
    }

    [Fact]
    public async Task N26a_partial_acquisition_write_also_unblocks_the_non_concurrent_sibling_it_blocked()
    {
        var (cache, fault) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewStoreAsync("node-1", cache);

        var job = QuartzJob("nc", nonConcurrent: true);
        var now = DateTimeOffset.UtcNow;
        await store.ScheduleJob(job, OneShot("t1", "tests", job, now.AddSeconds(-2)));
        await store.AddTrigger(OneShot("t2", "tests", job, now.AddSeconds(-1)));

        fault.PartialInsertBulk = FaultInjectingCache.Times(1, FaultInjectingCache.Call("InsertBulk"));

        await Assert.ThrowsAnyAsync<Exception>(async () => await AcquireAsync(store, maxCount: 2));
        fault.PartialInsertBulk = null;

        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "t1"))!.TriggerState);
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "t2"))!.TriggerState);

        var acquired = await AcquireAsync(store, maxCount: 2);
        Assert.Equal("t1", Assert.Single(acquired).Key.Name);
    }

    [Fact]
    public async Task N26a_start_up_releases_an_orphaned_acquired_row_but_keeps_one_a_live_node_holds()
    {
        var now = DateTimeOffset.UtcNow;
        await Storage.StoreJobAsync(CreateJob("job-1"), replaceExisting: false);

        var orphan = CreateAcquiredTrigger("orphan", "job-1", now.AddSeconds(-2));
        var held = CreateAcquiredTrigger("held", "job-1", now.AddSeconds(-1));
        await StoreTriggerRowAsync(orphan);
        await StoreTriggerRowAsync(held);

        await CheckInAsync("node-x", now);
        var heldFiring = await AcquireForTestAsync(held, "node-x");

        var store = await NewStoreAsync("node-1");

        try
        {
            await store.SchedulerStarted();

            Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "orphan"))!.TriggerState);
            Assert.Equal(TriggerStates.Acquired, (await Storage.GetTriggerAsync("tests", "held"))!.TriggerState);
            Assert.NotNull(await Storage.GetFiredTriggerAsync(heldFiring.FireInstanceId!));

            var acquired = await AcquireAsync(store, maxCount: 5);
            Assert.Equal("orphan", Assert.Single(acquired).Key.Name);
        }
        finally
        {
            await store.Shutdown();
        }
    }

    [Fact]
    public async Task N26b_failure_reading_the_siblings_fails_TriggersFired_before_anything_is_written()
    {
        var (cache, fault) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewStoreAsync("node-1", cache);

        var job = QuartzJob("j", nonConcurrent: true);
        var fireAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var trigger = OneShot("t", "tests", job, fireAt);
        await store.ScheduleJob(job, trigger);
        var acquired = await AcquireAsync(store);

        var registry = NCacheKeys.JobTriggers(_prefix, SchedulerName, "tests", "j");
        fault.FailWhen = FaultInjectingCache.Times(1, FaultInjectingCache.Call("Get", k => k == registry));

        await Assert.ThrowsAsync<JobPersistenceException>(async () => await store.TriggersFired(acquired));
        fault.FailWhen = null;

        var row = await Storage.GetTriggerAsync("tests", "t");
        Assert.NotNull(row);
        Assert.Equal(TriggerStates.Acquired, row.TriggerState);
        Assert.Equal(fireAt, row.NextFireTimeUtc);

        foreach (var t in acquired)
        {
            await store.ReleaseAcquiredTrigger(t);
        }

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));

        var again = await AcquireAsync(store);
        var fired = await store.TriggersFired(again);

        Assert.NotNull(Assert.Single(fired).TriggerFiredBundle);
    }

    [Fact]
    public async Task N26b_failed_bulk_write_in_TriggersFired_rolls_the_rows_back_so_the_release_restores_them()
    {
        var (cache, fault) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewStoreAsync("node-1", cache);

        var job = QuartzJob("j", nonConcurrent: true);
        var fireAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await store.ScheduleJob(job, OneShot("t", "tests", job, fireAt));
        await store.AddTrigger(Repeating("sibling", "tests", job, DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromHours(1)));
        var acquired = await AcquireAsync(store);
        Assert.Equal("t", Assert.Single(acquired).Key.Name);

        fault.FailWhen = FaultInjectingCache.Times(1, FaultInjectingCache.Call("InsertBulk"));

        await Assert.ThrowsAsync<JobPersistenceException>(async () => await store.TriggersFired(acquired));
        fault.FailWhen = null;

        var row = await Storage.GetTriggerAsync("tests", "t");
        Assert.Equal(TriggerStates.Acquired, row!.TriggerState);
        Assert.Equal(fireAt, row.NextFireTimeUtc);
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "sibling"))!.TriggerState);

        foreach (var t in acquired)
        {
            await store.ReleaseAcquiredTrigger(t);
        }

        var again = await AcquireAsync(store);
        var fired = await store.TriggersFired(again);

        Assert.NotNull(Assert.Single(fired).TriggerFiredBundle);
        Assert.Equal(TriggerStates.Blocked, (await Storage.GetTriggerAsync("tests", "sibling"))!.TriggerState);
    }
}

