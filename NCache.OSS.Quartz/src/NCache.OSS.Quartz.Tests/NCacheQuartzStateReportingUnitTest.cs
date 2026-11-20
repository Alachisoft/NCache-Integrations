using NCache.OSS.Quartz.Model;
using Quartz;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzStateReportingUnitTest : NCacheQuartzNodeScenarioTestBase
{
    [Fact]
    public async Task N20_trigger_set_to_error_reports_Error_while_another_run_of_it_is_executing()
    {
        var store = await NewStoreAsync("node-1");
        await CheckInAsync("node-1", DateTimeOffset.UtcNow);

        var job = QuartzJob("j");
        var trigger = Repeating("t", "tests", job, DateTimeOffset.UtcNow.AddSeconds(-2), TimeSpan.FromSeconds(1));
        await store.ScheduleJob(job, trigger);

        var first = await FireOneAsync(store);
        var second = await FireOneAsync(store);

        await store.TriggeredJobComplete(first.Trigger, first.JobDetail, SchedulerInstruction.SetTriggerError);

        Assert.Equal(TriggerStates.Error, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        Assert.Equal(TriggerState.Error, await store.GetTriggerState(trigger.Key));

        await store.TriggeredJobComplete(second.Trigger, second.JobDetail, SchedulerInstruction.NoInstruction);
        Assert.Equal(TriggerState.Error, await store.GetTriggerState(trigger.Key));
    }

    [Fact]
    public async Task N20_blocked_trigger_of_a_running_non_concurrent_job_reports_Executing_then_Normal()
    {
        var store = await NewStoreAsync("node-1");
        await CheckInAsync("node-1", DateTimeOffset.UtcNow);

        var job = QuartzJob("nc", nonConcurrent: true);
        var trigger = Repeating("t", "tests", job, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);

        var bundle = await FireOneAsync(store);

        Assert.Equal(TriggerStates.Blocked, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        Assert.Equal(TriggerState.Executing, await store.GetTriggerState(trigger.Key));

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));
    }

    [Fact]
    public async Task N20_last_run_of_a_one_shot_reports_Executing_until_it_completes_then_None()
    {
        var store = await NewStoreAsync("node-1");
        await CheckInAsync("node-1", DateTimeOffset.UtcNow);

        var job = QuartzJob("j");
        var trigger = OneShot("t", "tests", job, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        var bundle = await FireOneAsync(store);

        Assert.Equal(TriggerStates.Complete, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        Assert.Equal(TriggerState.Executing, await store.GetTriggerState(trigger.Key));

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.DeleteTrigger);

        Assert.Equal(TriggerState.None, await store.GetTriggerState(trigger.Key));
    }

    [Fact]
    public async Task N20_trigger_paused_during_its_run_reports_Paused_not_Executing()
    {
        var store = await NewStoreAsync("node-1");
        await CheckInAsync("node-1", DateTimeOffset.UtcNow);

        var job = QuartzJob("j");
        var trigger = Repeating("t", "tests", job, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);

        await FireOneAsync(store);
        await store.PauseTrigger(trigger.Key);

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
    }

    [Fact]
    public async Task N20_acquired_but_not_started_trigger_reports_Normal()
    {
        var store = await NewStoreAsync("node-1");
        await CheckInAsync("node-1", DateTimeOffset.UtcNow);

        var job = QuartzJob("j");
        var trigger = OneShot("t", "tests", job, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        Assert.Single(await AcquireAsync(store));

        Assert.Equal(TriggerStates.Acquired, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));
    }

    [Fact]
    public async Task N20_missing_trigger_reports_None()
    {
        var store = await NewStoreAsync("node-1");

        Assert.Equal(TriggerState.None, await store.GetTriggerState(new TriggerKey("nope", "tests")));
    }

    [Fact]
    public async Task N21_acquired_firing_has_no_job_key_and_never_matches_a_job_filter()
    {
        var store = await NewStoreAsync("node-1");
        await CheckInAsync("node-1", DateTimeOffset.UtcNow);

        var job = QuartzJob("j");
        var trigger = OneShot("t", "tests", job, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);
        var acquired = await AcquireAsync(store);
        Assert.Single(acquired);

        var byJob = await store.QueryFireInstances(new FireInstanceQuery { Job = job.Key, State = null });
        Assert.Empty(byJob.Items);

        var all = await store.QueryFireInstances(new FireInstanceQuery { State = null });
        var firing = Assert.Single(all.Items);
        Assert.Equal(FireInstanceState.Acquired, firing.State);
        Assert.Null(firing.JobKey);
        Assert.Equal(trigger.Key, firing.TriggerKey);
        Assert.Equal(acquired[0].FireInstanceId, firing.FireInstanceId);
        Assert.Equal("node-1", firing.SchedulerInstanceId);

        Assert.Empty((await store.QueryFireInstances(new FireInstanceQuery())).Items);
    }

    [Fact]
    public async Task N21_executing_firing_carries_its_job_key_and_matches_only_its_own_job()
    {
        var store = await NewStoreAsync("node-1");
        await CheckInAsync("node-1", DateTimeOffset.UtcNow);

        var job = QuartzJob("j");
        var other = QuartzJob("other");
        var trigger = OneShot("t", "tests", job, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);
        await store.AddJob(other);

        var bundle = await FireOneAsync(store);

        var byJob = await store.QueryFireInstances(new FireInstanceQuery { Job = job.Key });
        var firing = Assert.Single(byJob.Items);
        Assert.Equal(FireInstanceState.Executing, firing.State);
        Assert.Equal(job.Key, firing.JobKey);

        Assert.Empty((await store.QueryFireInstances(new FireInstanceQuery { Job = other.Key, State = null })).Items);
        Assert.Empty((await store.QueryFireInstances(new FireInstanceQuery { Job = job.Key, State = FireInstanceState.Acquired })).Items);

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.DeleteTrigger);

        Assert.Empty((await store.QueryFireInstances(new FireInstanceQuery { Job = job.Key, State = null })).Items);
    }

    [Fact]
    public async Task N21_cluster_job_filter_sees_executing_firings_of_every_node_but_no_acquired_ones()
    {
        var nodeA = await NewStoreAsync("node-a");
        var nodeB = await NewStoreAsync("node-b");
        await CheckInAsync("node-a", DateTimeOffset.UtcNow);
        await CheckInAsync("node-b", DateTimeOffset.UtcNow);

        var job = QuartzJob("j");
        var now = DateTimeOffset.UtcNow;
        await nodeA.ScheduleJob(job, OneShot("t1", "tests", job, now.AddSeconds(-3)));
        await nodeA.AddTrigger(OneShot("t2", "tests", job, now.AddSeconds(-2)));

        await FireOneAsync(nodeA);
        Assert.Single(await AcquireAsync(nodeB));

        var executing = await nodeB.QueryFireInstances(new FireInstanceQuery { Job = job.Key, State = null });
        var firing = Assert.Single(executing.Items);
        Assert.Equal("t1", firing.TriggerKey.Name);
        Assert.Equal("node-a", firing.SchedulerInstanceId);

        var acquiredOnB = await nodeA.QueryFireInstances(new FireInstanceQuery { SchedulerInstanceId = "node-b", State = FireInstanceState.Acquired });
        Assert.Null(Assert.Single(acquiredOnB.Items).JobKey);
    }

    [Theory]
    [InlineData(-3, ClusterNodeState.Alive)]
    [InlineData(-12, ClusterNodeState.Overdue)]
    [InlineData(-16, ClusterNodeState.Overdue)]
    [InlineData(-20, ClusterNodeState.Failed)]
    [InlineData(-300, ClusterNodeState.Failed)]
    public async Task N22_other_node_is_classified_by_how_long_ago_it_checked_in(int secondsAgo, ClusterNodeState expected)
    {
        await CheckInAsync("node-other", DateTimeOffset.UtcNow.AddSeconds(secondsAgo));
        var store = await NewStoreAsync("node-1");

        var nodes = await store.QueryClusterNodes();

        var other = Assert.Single(nodes, n => n.InstanceId == "node-other");
        Assert.Equal(expected, other.State);
        Assert.False(other.IsCurrentNode);
        Assert.Equal(TimeSpan.FromSeconds(10), other.CheckInInterval);
    }

    [Fact]
    public async Task N22_overdue_window_widens_with_the_configured_check_in_misfire_threshold()
    {
        await CheckInAsync("node-other", DateTimeOffset.UtcNow.AddSeconds(-25));
        var store = await NewStoreAsync("node-1");

        Assert.Equal(ClusterNodeState.Failed, Assert.Single(await store.QueryClusterNodes(), n => n.InstanceId == "node-other").State);

        store.ClusterCheckinMisfireThreshold = TimeSpan.FromSeconds(30);

        Assert.Equal(ClusterNodeState.Overdue, Assert.Single(await store.QueryClusterNodes(), n => n.InstanceId == "node-other").State);
    }

    [Fact]
    public async Task N22_current_node_is_reported_from_its_own_record_and_alive_before_its_first_check_in()
    {
        var store = await NewStoreAsync("node-1");

        var before = Assert.Single(await store.QueryClusterNodes());
        Assert.Equal("node-1", before.InstanceId);
        Assert.True(before.IsCurrentNode);
        Assert.Equal(ClusterNodeState.Alive, before.State);
        Assert.Null(before.LastCheckInUtc);

        await CheckInAsync("node-1", DateTimeOffset.UtcNow.AddSeconds(-12));

        var after = Assert.Single(await store.QueryClusterNodes());
        Assert.True(after.IsCurrentNode);
        Assert.Equal(ClusterNodeState.Overdue, after.State);
    }

    [Fact]
    public async Task N22_registered_node_without_a_check_in_record_is_Failed()
    {
        await Storage.RegisterSchedulerInstanceAsync("node-no-record");
        var store = await NewStoreAsync("node-1");

        var node = Assert.Single(await store.QueryClusterNodes(), n => n.InstanceId == "node-no-record");
        Assert.Equal(ClusterNodeState.Failed, node.State);
        Assert.Null(node.LastCheckInUtc);
    }

    [Fact]
    public async Task N22_cluster_two_nodes_see_each_other_and_themselves()
    {
        var nodeA = await NewStoreAsync("node-a");
        var nodeB = await NewStoreAsync("node-b");
        await CheckInAsync("node-a", DateTimeOffset.UtcNow);
        await CheckInAsync("node-b", DateTimeOffset.UtcNow.AddSeconds(-12));

        var seenByA = await nodeA.QueryClusterNodes();
        Assert.Equal(2, seenByA.Count);
        Assert.Equal(ClusterNodeState.Alive, Assert.Single(seenByA, n => n.IsCurrentNode && n.InstanceId == "node-a").State);
        Assert.Equal(ClusterNodeState.Overdue, Assert.Single(seenByA, n => !n.IsCurrentNode && n.InstanceId == "node-b").State);

        var seenByB = await nodeB.QueryClusterNodes();
        Assert.Equal(ClusterNodeState.Overdue, Assert.Single(seenByB, n => n.IsCurrentNode && n.InstanceId == "node-b").State);
        Assert.Equal(ClusterNodeState.Alive, Assert.Single(seenByB, n => !n.IsCurrentNode && n.InstanceId == "node-a").State);
    }

    [Fact]
    public async Task N22_overdue_node_keeps_its_pinned_trigger_and_is_not_recovered()
    {
        await CheckInAsync("node-late", DateTimeOffset.UtcNow.AddSeconds(-12));
        var nodeB = await NewStoreAsync("node-b");

        await Storage.StoreJobAsync(CreateJob("job-1"), replaceExisting: false);
        var pinned = CreateWaitingTrigger("pinned", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        pinned.PreferredNode = "node-late";
        await StoreTriggerRowAsync(pinned);

        Assert.Empty(await AcquireAsync(nodeB));

        Assert.Equal(0, await nodeB.RecoverStaleSchedulerInstancesAsync());
        Assert.Contains("node-late", await Storage.GetRegisteredSchedulerInstanceNamesAsync());
        Assert.NotNull(await Storage.GetSchedulerStateAsync("node-late"));

        nodeB.ClusterCheckinMisfireThreshold = TimeSpan.FromSeconds(1);
        Assert.Equal("pinned", Assert.Single(await AcquireAsync(nodeB)).Key.Name);
    }

    [Fact]
    public async Task N27_unfiltered_job_group_query_lists_only_groups_that_hold_jobs()
    {
        var store = await NewStoreAsync("node-1");
        await store.AddJob(QuartzJob("j", "real"));
        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals("future"));

        var groups = (await store.QueryJobGroups(new JobGroupQuery())).Items;

        var real = Assert.Single(groups);
        Assert.Equal("real", real.Name);
        Assert.False(real.Paused);
    }

    [Fact]
    public async Task C12_paused_job_group_query_still_lists_a_paused_group_that_holds_no_jobs()
    {
        var store = await NewStoreAsync("node-1");
        await store.AddJob(QuartzJob("j", "real"));
        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals("future"));

        var paused = (await store.QueryJobGroups(new JobGroupQuery { Paused = true })).Items;

        var future = Assert.Single(paused);
        Assert.Equal("future", future.Name);
        Assert.True(future.Paused);

        var notPaused = (await store.QueryJobGroups(new JobGroupQuery { Paused = false })).Items;
        Assert.Equal("real", Assert.Single(notPaused).Name);
    }

    [Fact]
    public async Task N27_paused_group_that_holds_jobs_is_listed_unfiltered_and_flagged_paused()
    {
        var store = await NewStoreAsync("node-1");
        await store.AddJob(QuartzJob("j1", "alpha"));
        await store.AddJob(QuartzJob("j2", "beta"));
        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals("beta"));

        var groups = (await store.QueryJobGroups(new JobGroupQuery())).Items;

        Assert.Equal(new[] { "alpha", "beta" }, groups.Select(g => g.Name).ToArray());
        Assert.False(groups[0].Paused);
        Assert.True(groups[1].Paused);
    }

    [Fact]
    public async Task N27_empty_paused_group_appears_unfiltered_once_a_job_is_added_to_it()
    {
        var store = await NewStoreAsync("node-1");
        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals("future"));

        Assert.Empty((await store.QueryJobGroups(new JobGroupQuery())).Items);

        await store.AddJob(QuartzJob("j", "future"));

        var future = Assert.Single((await store.QueryJobGroups(new JobGroupQuery())).Items);
        Assert.Equal("future", future.Name);
        Assert.True(future.Paused);
    }

    [Fact]
    public async Task N27_trigger_groups_follow_the_same_rule_and_never_list_the_pause_all_marker()
    {
        var store = await NewStoreAsync("node-1");
        var job = QuartzJob("j");
        await store.ScheduleJob(job, Repeating("t", "real", job, DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromHours(1)));
        await store.PauseTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals("future"));

        Assert.Equal("real", Assert.Single((await store.QueryTriggerGroups(new TriggerGroupQuery())).Items).Name);
        Assert.Equal("future", Assert.Single((await store.QueryTriggerGroups(new TriggerGroupQuery { Paused = true })).Items).Name);

        await store.PauseAll();

        var all = (await store.QueryTriggerGroups(new TriggerGroupQuery())).Items;
        var real = Assert.Single(all);
        Assert.Equal("real", real.Name);
        Assert.True(real.Paused);

        var paused = (await store.QueryTriggerGroups(new TriggerGroupQuery { Paused = true })).Items;
        Assert.DoesNotContain(paused, g => g.Name == NCacheJobStore.AllGroupsPausedMarker);
        Assert.Contains(paused, g => g.Name == "future");
        Assert.Contains(paused, g => g.Name == "real");
    }
}

