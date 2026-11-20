using NCache.OSS.Quartz.Model;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzFireProgressUnitTest : NCacheQuartzScenarioTestBase
{
    [Fact]
    public async Task A_report_is_stored_on_the_firing_and_listed_and_the_next_report_replaces_it()
    {
        var store = await NewNodeAsync();
        var id = await FireAsync(store);

        var listedBefore = Assert.Single((await store.QueryFireInstances(new FireInstanceQuery())).Items);
        Assert.Null(listedBefore.Progress);
        Assert.Null(listedBefore.ProgressMessage);

        await store.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = 40, Message = "copying" });

        var listed = Assert.Single((await store.QueryFireInstances(new FireInstanceQuery())).Items);
        Assert.Equal(id, listed.FireInstanceId);
        Assert.Equal(40, listed.Progress);
        Assert.Equal("copying", listed.ProgressMessage);
        Assert.Equal(FireInstanceState.Executing, listed.State);

        await store.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = 75, Message = "verifying" });

        listed = Assert.Single((await store.QueryFireInstances(new FireInstanceQuery())).Items);
        Assert.Equal(75, listed.Progress);
        Assert.Equal("verifying", listed.ProgressMessage);
    }

    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(int.MaxValue, 100)]
    public async Task A_percentage_outside_the_range_is_stored_at_its_nearest_bound(int reported, int stored)
    {
        var store = await NewNodeAsync();
        var id = await FireAsync(store);

        await store.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = reported });

        Assert.Equal(stored, (await Storage.GetFiredTriggerAsync(id))!.Progress);
    }

    [Fact]
    public async Task A_report_without_a_message_clears_the_message_of_the_report_before()
    {
        var store = await NewNodeAsync();
        var id = await FireAsync(store);
        await store.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = 10, Message = "starting" });

        await store.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = 20 });

        var record = (await Storage.GetFiredTriggerAsync(id))!;
        Assert.Equal(20, record.Progress);
        Assert.Null(record.ProgressMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task A_blank_message_is_stored_as_none(string message)
    {
        var store = await NewNodeAsync();
        var id = await FireAsync(store);

        await store.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = 5, Message = message });

        var record = (await Storage.GetFiredTriggerAsync(id))!;
        Assert.Equal(5, record.Progress);
        Assert.Null(record.ProgressMessage);
    }

    [Fact]
    public async Task A_message_longer_than_the_limit_is_cut_and_a_surrogate_pair_is_never_split()
    {
        var store = await NewNodeAsync();
        var id = await FireAsync(store);

        await store.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = 1, Message = new string('m', 400) });
        Assert.Equal(new string('m', FireInstanceProgress.MaxMessageLength), (await Storage.GetFiredTriggerAsync(id))!.ProgressMessage);

        await store.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = 2, Message = new string('m', 249) + "\U0001F600tail" });
        Assert.Equal(new string('m', 249), (await Storage.GetFiredTriggerAsync(id))!.ProgressMessage);

        await store.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = 3, Message = new string('m', 248) + "\U0001F600" });
        Assert.Equal(new string('m', 248) + "\U0001F600", (await Storage.GetFiredTriggerAsync(id))!.ProgressMessage);
    }

    [Fact]
    public async Task A_report_for_a_firing_that_does_not_exist_writes_nothing()
    {
        var store = await NewNodeAsync();

        await store.UpdateFireInstanceProgress("no-such-firing", new FireInstanceProgress { Percent = 50, Message = "ghost" });

        Assert.Null(await Storage.GetFiredTriggerAsync("no-such-firing"));
        Assert.Empty(await Storage.GetAllFiredTriggersAsync());
        Assert.Empty((await store.QueryFireInstances(new FireInstanceQuery())).Items);
    }

    [Fact]
    public async Task A_report_for_a_firing_that_is_only_acquired_is_not_written()
    {
        var store = await NewNodeAsync();
        await ScheduleAsync(store);
        var acquired = Assert.Single(await AcquireDueAsync(store));

        await store.UpdateFireInstanceProgress(acquired.FireInstanceId!, new FireInstanceProgress { Percent = 50, Message = "early" });

        var record = (await Storage.GetFiredTriggerAsync(acquired.FireInstanceId!))!;
        Assert.Equal(FiredTriggerStates.Acquired, record.State);
        Assert.Null(record.Progress);
        Assert.Null(record.ProgressMessage);

        var fired = Assert.Single(await store.TriggersFired(new[] { acquired }));
        Assert.NotNull(fired.TriggerFiredBundle);
        Assert.Null((await Storage.GetFiredTriggerAsync(acquired.FireInstanceId!))!.Progress);
    }

    [Fact]
    public async Task A_report_that_arrives_after_the_completion_does_not_bring_the_firing_record_back()
    {
        var store = await NewNodeAsync();
        var job = Job("job", nonConcurrent: true);
        var trigger = Repeating("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2), TimeSpan.FromSeconds(1));
        await store.ScheduleJob(job, trigger);
        var bundle = await FireOneAsync(store);
        var id = bundle.Trigger.FireInstanceId!;
        await store.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = 99, Message = "almost" });

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);
        await store.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = 100, Message = "done" });

        Assert.Null(await Storage.GetFiredTriggerAsync(id));
        Assert.Empty(await Storage.GetAllFiredTriggersAsync());
        Assert.Empty(await Storage.GetFiredTriggersForJobAsync("tests", "job"));
        Assert.Equal(trigger.Key, Assert.Single(await AcquireDueAsync(store)).Key);
    }

    [Fact]
    public async Task Reports_racing_the_completion_never_leave_a_record_behind()
    {
        var store = await NewNodeAsync();
        var job = Job("job", nonConcurrent: true);
        var trigger = Repeating("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2), TimeSpan.FromSeconds(1));
        await store.ScheduleJob(job, trigger);
        var bundle = await FireOneAsync(store);
        var id = bundle.Trigger.FireInstanceId!;

        var reports = Enumerable.Range(1, 20)
            .Select(i => Task.Run(() => store.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = i * 5, Message = $"step {i}" }).AsTask()))
            .ToList();
        var completion = Task.Run(() => store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction).AsTask());

        await Task.WhenAll(reports.Append(completion));

        Assert.Null(await Storage.GetFiredTriggerAsync(id));
        Assert.Empty(await Storage.GetAllFiredTriggersAsync());
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
    }

    [Fact]
    public async Task A_report_changes_nothing_else_on_the_firing_record()
    {
        var store = await NewNodeAsync();
        var job = Job("job", nonConcurrent: true, requestsRecovery: true);
        var trigger = (IOperableTrigger) TriggerBuilder.Create()
            .WithIdentity("t", "tests")
            .ForJob(job.Key)
            .StartAt(DateTimeOffset.UtcNow.AddSeconds(-2))
            .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromHours(1)).RepeatForever())
            .WithOverlapPolicy(OverlapPolicy.CancelPrevious)
            .WithPriority(9)
            .Build();
        trigger.ComputeFirstFireTimeUtc(null);
        await store.ScheduleJob(job, trigger);
        var bundle = await FireOneAsync(store);
        var before = (await Storage.GetFiredTriggerAsync(bundle.Trigger.FireInstanceId!))!;

        await store.UpdateFireInstanceProgress(before.EntryId, new FireInstanceProgress { Percent = 33, Message = "a third" });

        var after = (await Storage.GetFiredTriggerAsync(before.EntryId))!;
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.InstanceName, after.InstanceName);
        Assert.Equal(before.FiredTimeUtc, after.FiredTimeUtc);
        Assert.Equal(before.ScheduledTimeUtc, after.ScheduledTimeUtc);
        Assert.Equal(before.Priority, after.Priority);
        Assert.Equal(before.IsNonConcurrent, after.IsNonConcurrent);
        Assert.Equal(before.RequestsRecovery, after.RequestsRecovery);
        Assert.Equal(before.OverlapPolicy, after.OverlapPolicy);
        Assert.Equal(before.JobName, after.JobName);
        Assert.Equal(33, after.Progress);
        Assert.Single(await Storage.GetFiredTriggersForInstanceAsync("node-1"));
        Assert.Single(await Storage.GetFiredTriggersForJobAsync("tests", "job"));
    }

    [Fact]
    public async Task Each_firing_carries_its_own_progress()
    {
        var store = await NewNodeAsync();
        var first = await FireAsync(store, "job-a", "a");
        var second = await FireAsync(store, "job-b", "b");

        await store.UpdateFireInstanceProgress(first, new FireInstanceProgress { Percent = 10, Message = "one" });
        await store.UpdateFireInstanceProgress(second, new FireInstanceProgress { Percent = 90, Message = "two" });

        var listed = (await store.QueryFireInstances(new FireInstanceQuery())).Items.ToDictionary(f => f.FireInstanceId);
        Assert.Equal(10, listed[first].Progress);
        Assert.Equal("one", listed[first].ProgressMessage);
        Assert.Equal(90, listed[second].Progress);
        Assert.Equal("two", listed[second].ProgressMessage);
    }

    [Fact]
    public async Task Progress_written_on_one_node_is_listed_by_another()
    {
        var nodeA = await NewNodeAsync("node-a");
        var nodeB = await NewNodeAsync("node-b");
        var id = await FireAsync(nodeA);

        await nodeA.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = 60, Message = "over here" });

        var listed = Assert.Single((await nodeB.QueryFireInstances(new FireInstanceQuery { SchedulerInstanceId = "node-a" })).Items);
        Assert.Equal(60, listed.Progress);
        Assert.Equal("over here", listed.ProgressMessage);
        Assert.Empty((await nodeB.QueryFireInstances(new FireInstanceQuery { SchedulerInstanceId = "node-b" })).Items);
    }

    [Fact]
    public async Task The_firing_of_a_node_that_died_loses_its_progress_with_its_record()
    {
        var dead = await NewNodeAsync("node-dead");
        var survivor = await NewNodeAsync("node-b");
        var id = await FireAsync(dead);
        await dead.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = 50, Message = "half" });

        await CheckInAsync("node-dead", DateTimeOffset.UtcNow.AddMinutes(-5));
        await survivor.RecoverStaleSchedulerInstancesAsync();
        await dead.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = 60, Message = "still here" });

        Assert.Null(await Storage.GetFiredTriggerAsync(id));
        Assert.Empty((await survivor.QueryFireInstances(new FireInstanceQuery())).Items);
    }

    [Fact]
    public async Task A_report_whose_write_fails_throws_keeps_the_earlier_report_and_does_not_stop_the_completion()
    {
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewNodeAsync(cache: cache);
        var job = Job("job");
        var trigger = Repeating("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);
        var bundle = await FireOneAsync(store);
        var id = bundle.Trigger.FireInstanceId!;
        await store.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = 10, Message = "first" });

        control.FailWhen = FaultInjectingCache.Call("Insert", k => k.Contains(":fired:", StringComparison.Ordinal));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = 20, Message = "second" }).AsTask());
        control.FailWhen = null;

        var record = (await Storage.GetFiredTriggerAsync(id))!;
        Assert.Equal(10, record.Progress);
        Assert.Equal("first", record.ProgressMessage);
        Assert.Equal(FiredTriggerStates.Executing, record.State);

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.Empty(await Storage.GetAllFiredTriggersAsync());
    }

    [Fact]
    public async Task A_report_whose_read_fails_throws_and_writes_nothing()
    {
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewNodeAsync(cache: cache);
        var id = await FireAsync(store);

        control.FailWhen = FaultInjectingCache.Times(1, FaultInjectingCache.Call("Get", k => k.Contains(":fired:", StringComparison.Ordinal)));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = 20 }).AsTask());
        control.FailWhen = null;

        Assert.Null((await Storage.GetFiredTriggerAsync(id))!.Progress);
    }

    [Fact]
    public async Task A_cancelled_report_writes_nothing()
    {
        var store = await NewNodeAsync();
        var id = await FireAsync(store);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.UpdateFireInstanceProgress(id, new FireInstanceProgress { Percent = 20 }, cancelled.Token).AsTask());

        Assert.Null((await Storage.GetFiredTriggerAsync(id))!.Progress);
    }

    [Fact]
    public async Task Null_arguments_are_refused()
    {
        var store = await NewNodeAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.UpdateFireInstanceProgress(null!, new FireInstanceProgress { Percent = 1 }).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.UpdateFireInstanceProgress("id", null!).AsTask());
    }

    private static async Task ScheduleAsync(NCacheJobStore store, string jobName = "job", string triggerName = "t")
    {
        var job = Job(jobName);
        await store.ScheduleJob(job, Repeating(triggerName, "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2), TimeSpan.FromHours(1)));
    }

    private static async Task<string> FireAsync(NCacheJobStore store, string jobName = "job", string triggerName = "t")
    {
        await ScheduleAsync(store, jobName, triggerName);
        return (await FireOneAsync(store)).Trigger.FireInstanceId!;
    }
}
