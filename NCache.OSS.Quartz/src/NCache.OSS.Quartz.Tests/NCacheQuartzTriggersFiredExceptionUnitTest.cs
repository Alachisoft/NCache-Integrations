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

public sealed class NCacheQuartzTriggersFiredExceptionUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task N3_a_cache_error_inside_TriggersFired_reaches_Quartz_as_a_JobPersistenceException_and_the_batch_can_be_released()
    {
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var store = await ScenarioKit.CreateStoreAsync(cache, _prefix, SchedulerName, "instance-1");
        var job = ScenarioKit.Job("j", "g");
        var trigger = ScenarioKit.OneShot("t", "g", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);
        var acquired = await store.AcquireNextTriggers(ScenarioKit.DueNow(1));
        Assert.Single(acquired);

        control.FailWhen = FaultInjectingCache.Times(1, (method, args) =>
            method.Name == "GetBulk" && args?[0] is IEnumerable<string> keys && keys.Any(k => k.Contains(":fired:", StringComparison.Ordinal)));

        var ex = await Assert.ThrowsAsync<JobPersistenceException>(async () => await store.TriggersFired(acquired));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.IsAssignableFrom<SchedulerException>(ex);

        var row = await Storage.GetTriggerAsync("g", "t");
        Assert.Equal(TriggerStates.Acquired, row!.TriggerState);

        await store.ReleaseAcquiredTrigger(acquired[0]);
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));
        Assert.Empty(await Storage.GetFiredTriggersForInstanceAsync("instance-1"));

        var again = await store.AcquireNextTriggers(ScenarioKit.DueNow(1));
        Assert.NotNull(Assert.Single(await store.TriggersFired(again)).TriggerFiredBundle);
    }

    [Fact]
    public async Task N3_a_SchedulerException_raised_inside_TriggersFired_is_passed_on_unwrapped()
    {
        var store = CreateStore("instance-1");
        var stored = CreateAcquiredTrigger("t", "j", DateTimeOffset.UtcNow.AddSeconds(-1));
        await StoreJobAndTriggerAsync(CreateJob("j"), stored);

        var trigger = ToQuartzTrigger(stored);
        trigger.FireInstanceId = null;

        var ex = await Assert.ThrowsAsync<JobPersistenceException>(async () => await store.TriggersFired(new[] { trigger }));

        Assert.Contains("has no FireInstanceId", ex.Message);
        Assert.Null(ex.InnerException);
    }
}

