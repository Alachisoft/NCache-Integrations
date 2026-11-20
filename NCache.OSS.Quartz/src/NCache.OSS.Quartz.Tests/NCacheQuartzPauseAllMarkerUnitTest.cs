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

public sealed class NCacheQuartzPauseAllMarkerUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task N6_ResumeTriggerGroups_AnyGroup_after_PauseAll_leaves_nothing_paused()
    {
        var store = CreateStore("instance-1");
        var now = DateTimeOffset.UtcNow;
        var job = ScenarioKit.Job("j", "g");
        var t = ScenarioKit.Repeating("t", "g", job.Key, now.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, t);

        await store.PauseAll();
        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(t.Key));

        var resumed = await store.ResumeTriggerGroups(GroupMatcher<TriggerKey>.AnyGroup());

        Assert.Equal(new[] { "g" }, resumed);
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(t.Key));
        Assert.Empty((await store.QueryTriggerGroups(new TriggerGroupQuery { Paused = true })).Items);

        await ScenarioKit.FireOneAsync(store);
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(t.Key));

        var job2 = ScenarioKit.Job("j2", "g2");
        var t2 = ScenarioKit.Repeating("t2", "g2", job2.Key, now.AddHours(1), TimeSpan.FromHours(1));
        await store.ScheduleJob(job2, t2);
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(t2.Key));
    }

    [Fact]
    public async Task C14_after_PauseAll_resuming_one_group_leaves_the_other_groups_and_new_groups_paused()
    {
        var store = CreateStore("instance-1");
        var now = DateTimeOffset.UtcNow;
        var a = ScenarioKit.Job("a", "g");
        var b = ScenarioKit.Job("b", "g2");
        await store.ScheduleJob(a, ScenarioKit.Repeating("ta", "ga", a.Key, now.AddHours(1), TimeSpan.FromHours(1)));
        await store.ScheduleJob(b, ScenarioKit.Repeating("tb", "gb", b.Key, now.AddHours(1), TimeSpan.FromHours(1)));

        await store.PauseAll();
        await store.ResumeTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals("ga"));

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(new TriggerKey("ta", "ga")));
        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(new TriggerKey("tb", "gb")));

        var c = ScenarioKit.Job("c", "g3");
        var tc = ScenarioKit.Repeating("tc", "gc", c.Key, now.AddHours(1), TimeSpan.FromHours(1));
        await store.ScheduleJob(c, tc);
        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(tc.Key));
    }
}

