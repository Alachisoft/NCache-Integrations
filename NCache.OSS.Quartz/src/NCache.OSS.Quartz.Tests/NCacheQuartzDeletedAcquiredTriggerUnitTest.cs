using System.Globalization;
using System.Text.Json;
using Alachisoft.NCache.Client;
using NCache.OSS.Quartz.Extensions;
using NCache.OSS.Quartz.Index;
using NCache.OSS.Quartz.Locking;
using NCache.OSS.Quartz.Mapping;
using NCache.OSS.Quartz.Model;
using NCache.OSS.Quartz.Serialization;
using NCache.OSS.Quartz.Storage;
using Quartz;
using Quartz.Extensibility;
using Quartz.Impl;
using Quartz.Impl.Calendar;
using Quartz.Serialization.SystemTextJson;
using Quartz.Serialization.SystemTextJson.Calendars;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzDeletedAcquiredTriggerUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task N5_deleting_an_acquired_trigger_does_not_leave_its_non_concurrent_sibling_blocked()
    {
        var store = CreateStore("instance-1");
        var (acquiredKey, siblingKey, acquired) = await AcquireOneOfTwoNonConcurrentAsync(store);

        Assert.Equal(TriggerState.Blocked, await store.GetTriggerState(siblingKey));

        Assert.True(await store.DeleteTrigger(acquiredKey));
        var results = await store.TriggersFired(acquired);
        Assert.Null(Assert.Single(results).TriggerFiredBundle);
        await store.ReleaseAcquiredTrigger(acquired[0]);

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(siblingKey));
        Assert.Equal(siblingKey, Assert.Single(await store.AcquireNextTriggers(ScenarioKit.DueNow(5))).Key);
    }

    [Fact]
    public async Task N5_a_sibling_paused_while_blocked_goes_back_to_paused_when_the_deleted_acquisition_is_released()
    {
        var store = CreateStore("instance-1");
        var (acquiredKey, siblingKey, acquired) = await AcquireOneOfTwoNonConcurrentAsync(store);

        Assert.True(await store.PauseTrigger(siblingKey));
        Assert.Equal(TriggerStates.PausedAndBlocked, (await Storage.GetTriggerAsync(siblingKey.Group, siblingKey.Name))!.TriggerState);

        await store.DeleteTrigger(acquiredKey);
        await store.TriggersFired(acquired);
        await store.ReleaseAcquiredTrigger(acquired[0]);

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(siblingKey));
        Assert.Empty(await store.AcquireNextTriggers(ScenarioKit.DueNow(5)));
    }

    private static async Task<(TriggerKey Acquired, TriggerKey Sibling, List<IOperableTrigger> Batch)> AcquireOneOfTwoNonConcurrentAsync(NCacheJobStore store)
    {
        var job = ScenarioKit.Job("j", "g", nonConcurrent: true);
        var now = DateTimeOffset.UtcNow;
        await store.ScheduleJob(job, ScenarioKit.Repeating("t1", "g", job.Key, now.AddSeconds(-2), TimeSpan.FromHours(1)));
        await store.AddTrigger(ScenarioKit.Repeating("t2", "g", job.Key, now.AddSeconds(-1), TimeSpan.FromHours(1)));

        var acquired = await store.AcquireNextTriggers(ScenarioKit.DueNow(2));
        var acquiredKey = Assert.Single(acquired).Key;
        var siblingKey = new TriggerKey(acquiredKey.Name == "t1" ? "t2" : "t1", "g");

        return (acquiredKey, siblingKey, acquired);
    }
}

