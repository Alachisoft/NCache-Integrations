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

public sealed class NCacheQuartzBatchWindowUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task N1_a_batch_does_not_take_a_trigger_due_well_after_its_first_one()
    {
        var store = CreateStore("instance-1");
        var now = DateTimeOffset.UtcNow;
        var a = ScenarioKit.Job("a", "g");
        var b = ScenarioKit.Job("b", "g");
        await store.ScheduleJob(a, ScenarioKit.OneShot("due-now", "g", a.Key, now.AddSeconds(-1)));
        await store.ScheduleJob(b, ScenarioKit.OneShot("due-in-25s", "g", b.Key, now.AddSeconds(25)));

        var acquired = await store.AcquireNextTriggers(CreateRequest(now.AddSeconds(30), 10));

        Assert.Equal("due-now", Assert.Single(acquired).Key.Name);
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(new TriggerKey("due-in-25s", "g")));

        var later = await store.AcquireNextTriggers(CreateRequest(now.AddSeconds(30), 10));
        Assert.Equal("due-in-25s", Assert.Single(later).Key.Name);
    }

    [Fact]
    public async Task C6_triggers_due_at_the_same_time_are_still_acquired_in_one_batch()
    {
        var store = CreateStore("instance-1");
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 3; i++)
        {
            var job = ScenarioKit.Job($"j{i}", "g");
            await store.ScheduleJob(job, ScenarioKit.OneShot($"t{i}", "g", job.Key, now.AddSeconds(-1)));
        }

        var acquired = await store.AcquireNextTriggers(CreateRequest(now.AddSeconds(30), 10));

        Assert.Equal(new[] { "t0", "t1", "t2" }, acquired.Select(t => t.Key.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(1, false)]
    public async Task N1_the_time_window_extends_the_batch_beyond_its_first_trigger(int windowSeconds, bool secondIncluded)
    {
        var store = CreateStore("instance-1");
        var now = DateTimeOffset.UtcNow;
        var a = ScenarioKit.Job("a", "g");
        var b = ScenarioKit.Job("b", "g");
        await store.ScheduleJob(a, ScenarioKit.OneShot("first", "g", a.Key, now.AddSeconds(-1)));
        await store.ScheduleJob(b, ScenarioKit.OneShot("second", "g", b.Key, now.AddSeconds(3)));

        var acquired = await store.AcquireNextTriggers(CreateRequest(now.AddSeconds(30), 10, TimeSpan.FromSeconds(windowSeconds)));

        Assert.Equal("first", acquired[0].Key.Name);
        Assert.Equal(secondIncluded, acquired.Any(t => t.Key.Name == "second"));
    }

    [Fact]
    public async Task N1_a_batch_led_by_a_future_trigger_ends_at_that_trigger_fire_time()
    {
        var store = CreateStore("instance-1");
        var at = DateTimeOffset.UtcNow.AddSeconds(2);

        foreach (var (name, fireAt) in new[] { ("a", at), ("b", at), ("c", at.AddSeconds(1.5)) })
        {
            var job = ScenarioKit.Job(name, "g");
            await store.ScheduleJob(job, ScenarioKit.OneShot(name, "g", job.Key, fireAt));
        }

        var acquired = await store.AcquireNextTriggers(CreateRequest(at.AddSeconds(20), 10));

        Assert.Equal(new[] { "a", "b" }, acquired.Select(t => t.Key.Name).OrderBy(n => n, StringComparer.Ordinal));
    }
}

