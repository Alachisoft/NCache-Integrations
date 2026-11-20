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

public sealed class NCacheQuartzTriggerReplacementUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task N7_replacing_a_trigger_through_AddTrigger_or_ScheduleJobs_keeps_its_previous_fire_time()
    {
        var store = CreateStore("instance-1");
        var now = DateTimeOffset.UtcNow;
        var job = ScenarioKit.Job("j", "g");
        await store.ScheduleJob(job, ScenarioKit.Repeating("viaAddTrigger", "g", job.Key, now.AddSeconds(-2), TimeSpan.FromHours(1)));
        await store.AddTrigger(ScenarioKit.Repeating("viaScheduleJobs", "g", job.Key, now.AddSeconds(-1), TimeSpan.FromHours(1)));
        await FireAndCompleteAsync(store, 2);

        var before1 = (await store.GetTrigger(new TriggerKey("viaAddTrigger", "g")))!.PreviousFireTimeUtc;
        var before2 = (await store.GetTrigger(new TriggerKey("viaScheduleJobs", "g")))!.PreviousFireTimeUtc;
        Assert.NotNull(before1);
        Assert.NotNull(before2);

        await store.AddTrigger(ScenarioKit.Repeating("viaAddTrigger", "g", job.Key, now.AddHours(1), TimeSpan.FromHours(1)), new AddTriggerOptions { Replace = true });
        await store.ScheduleJobs(
            new Dictionary<IJobDetail, IReadOnlyCollection<IOperableTrigger>>
            {
                [job] = new[] { ScenarioKit.Repeating("viaScheduleJobs", "g", job.Key, now.AddHours(1), TimeSpan.FromHours(1)) }
            },
            new ScheduleJobOptions { Replace = true });

        var after1 = await store.GetTrigger(new TriggerKey("viaAddTrigger", "g"));
        var after2 = await store.GetTrigger(new TriggerKey("viaScheduleJobs", "g"));

        Assert.Equal(before1, after1!.PreviousFireTimeUtc);
        Assert.Equal(before2, after2!.PreviousFireTimeUtc);

        Assert.True(after1.NextFireTimeUtc > now.AddMinutes(59));
        Assert.True(after2.NextFireTimeUtc > now.AddMinutes(59));
    }

    [Fact]
    public async Task N7_a_replacement_carrying_its_own_previous_fire_time_keeps_that_one()
    {
        var store = CreateStore("instance-1");
        var now = DateTimeOffset.UtcNow;
        var job = ScenarioKit.Job("j", "g");
        await store.ScheduleJob(job, ScenarioKit.Repeating("t", "g", job.Key, now.AddSeconds(-1), TimeSpan.FromHours(1)));
        await FireAndCompleteAsync(store, 1);

        var own = now.AddDays(-3);
        var replacement = ScenarioKit.Repeating("t", "g", job.Key, now.AddHours(1), TimeSpan.FromHours(1));
        replacement.PreviousFireTimeUtc = own;

        await store.AddTrigger(replacement, new AddTriggerOptions { Replace = true });

        Assert.Equal(own, (await store.GetTrigger(replacement.Key))!.PreviousFireTimeUtc);
    }

    [Fact]
    public async Task N7_replacing_a_trigger_that_never_fired_leaves_its_previous_fire_time_empty()
    {
        var store = CreateStore("instance-1");
        var now = DateTimeOffset.UtcNow;
        var job = ScenarioKit.Job("j", "g");
        await store.ScheduleJob(job, ScenarioKit.Repeating("t", "g", job.Key, now.AddHours(1), TimeSpan.FromHours(1)));

        await store.ScheduleJobs(
            new Dictionary<IJobDetail, IReadOnlyCollection<IOperableTrigger>>
            {
                [job] = new[] { ScenarioKit.Repeating("t", "g", job.Key, now.AddHours(2), TimeSpan.FromHours(1)) }
            },
            new ScheduleJobOptions { Replace = true });

        Assert.Null((await store.GetTrigger(new TriggerKey("t", "g")))!.PreviousFireTimeUtc);
    }

    private static async Task FireAndCompleteAsync(NCacheJobStore store, int count)
    {
        var acquired = await store.AcquireNextTriggers(ScenarioKit.DueNow(count));
        Assert.Equal(count, acquired.Count);

        foreach (var fired in await store.TriggersFired(acquired))
        {
            var bundle = fired.TriggerFiredBundle!;
            await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);
        }
    }
}

