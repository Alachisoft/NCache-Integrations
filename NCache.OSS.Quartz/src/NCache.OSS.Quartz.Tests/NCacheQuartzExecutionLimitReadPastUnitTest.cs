using NCache.OSS.Quartz.Model;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzExecutionLimitReadPastUnitTest : NCacheQuartzScenarioTestBase
{
    [Theory]
    [InlineData(12)]
    [InlineData(40)]
    [InlineData(120)]
    public async Task A_head_made_of_triggers_whose_group_is_at_its_limit_does_not_hide_a_due_trigger_behind_it(int atLimit)
    {
        var store = await NewNodeAsync();
        await StoreBusyHeadAsync(atLimit);
        await StoreRowAsync("free", "free-job", DateTimeOffset.UtcNow.AddSeconds(-1), executionGroup: null);
        await OccupyAsync("busy", "node-2");

        var acquired = await store.AcquireNextTriggers(Request(maxCount: 1));

        Assert.Equal("free", Assert.Single(acquired).Key.Name);
        Assert.All(await BusyRowsAsync(atLimit), row => Assert.Equal(TriggerStates.Waiting, row.TriggerState));
    }

    [Fact]
    public async Task With_every_due_trigger_at_its_limit_nothing_is_acquired_and_nothing_is_changed()
    {
        var store = await NewNodeAsync();
        await StoreBusyHeadAsync(15);
        await OccupyAsync("busy", "node-2");

        var acquired = await store.AcquireNextTriggers(Request(maxCount: 3));

        Assert.Empty(acquired);
        Assert.All(await BusyRowsAsync(15), row => Assert.Equal(TriggerStates.Waiting, row.TriggerState));
        Assert.Equal("occupied-busy", Assert.Single(await Storage.GetAllFiredTriggersAsync()).EntryId);
        Assert.Equal(15, (await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(1), 100)).Count);
    }

    [Fact]
    public async Task Once_the_group_has_a_free_slot_its_oldest_trigger_is_acquired_and_no_more_than_the_limit()
    {
        var store = await NewNodeAsync();
        await StoreBusyHeadAsync(12);
        await OccupyAsync("busy", "node-2");
        Assert.Empty(await store.AcquireNextTriggers(Request(maxCount: 3)));

        await Storage.RemoveFiredTriggerAsync("occupied-busy");
        var acquired = await store.AcquireNextTriggers(Request(maxCount: 3));

        Assert.Equal("busy-00", Assert.Single(acquired).Key.Name);
        Assert.Empty(await store.AcquireNextTriggers(Request(maxCount: 3)));
    }

    [Fact]
    public async Task A_limit_of_one_group_does_not_hold_back_another_limited_group_with_room()
    {
        var store = await NewNodeAsync();
        await StoreBusyHeadAsync(12);
        await StoreRowAsync("other-1", "other-job-1", DateTimeOffset.UtcNow.AddSeconds(-2), executionGroup: "other");
        await StoreRowAsync("other-2", "other-job-2", DateTimeOffset.UtcNow.AddSeconds(-1), executionGroup: "other");
        await OccupyAsync("busy", "node-2");

        var acquired = await store.AcquireNextTriggers(Request(maxCount: 1, otherGroupLimit: 1));

        Assert.Equal("other-1", Assert.Single(acquired).Key.Name);
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "other-2"))!.TriggerState);
    }

    [Fact]
    public async Task A_firing_that_is_only_acquired_on_another_node_counts_against_a_cluster_limit()
    {
        var store = await NewNodeAsync();
        await StoreBusyHeadAsync(3);
        await OccupyAsync("busy", "node-2", FiredTriggerStates.Acquired);

        Assert.Empty(await store.AcquireNextTriggers(Request(maxCount: 3)));
    }

    private static TriggerAcquisitionRequest Request(int maxCount, int? otherGroupLimit = null)
    {
        var limits = ExecutionLimitsBuilder.Create().ForGroup("busy", 1, ExecutionLimitScope.Cluster);

        if (otherGroupLimit is { } limit)
        {
            limits = limits.ForGroup("other", limit, ExecutionLimitScope.Cluster);
        }

        return new TriggerAcquisitionRequest
        {
            NoLaterThan = DateTimeOffset.UtcNow.AddSeconds(5),
            MaxCount = maxCount,
            TimeWindow = TimeSpan.Zero,
            ExecutionLimits = limits.Build()
        };
    }

    private async Task StoreBusyHeadAsync(int count)
    {
        var oldest = DateTimeOffset.UtcNow.AddSeconds(-40);

        for (var i = 0; i < count; i++)
        {
            await StoreRowAsync($"busy-{i:00}", $"busy-job-{i:00}", oldest.AddMilliseconds(i * 100), executionGroup: "busy");
        }
    }

    private async Task StoreRowAsync(string triggerName, string jobName, DateTimeOffset nextFire, string? executionGroup)
    {
        var row = CreateWaitingTrigger(triggerName, jobName, nextFire);
        row.ExecutionGroup = executionGroup;
        await StoreJobAndTriggerAsync(CreateJob(jobName), row);
    }

    private async Task<List<NCacheTriggerData>> BusyRowsAsync(int count)
    {
        var rows = await Storage.GetTriggersAsync(Enumerable.Range(0, count).Select(i => ("tests", $"busy-{i:00}")));
        Assert.Equal(count, rows.Count);
        return rows.Values.ToList();
    }

    private async Task OccupyAsync(string executionGroup, string instanceName, string state = FiredTriggerStates.Executing)
    {
        await CheckInAsync(instanceName, DateTimeOffset.UtcNow);
        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = $"occupied-{executionGroup}",
            TriggerName = "running-elsewhere",
            TriggerGroup = "tests",
            InstanceName = instanceName,
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = DateTimeOffset.UtcNow,
            State = state,
            JobName = "running-job",
            JobGroup = "tests",
            ExecutionGroup = executionGroup
        });
    }
}
