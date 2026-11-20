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

public sealed class NCacheQuartzCalendarUpdateTriggersUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task N8_AddCalendar_with_UpdateTriggers_does_not_bring_an_ERROR_trigger_back()
    {
        var store = CreateStore("instance-1");
        await store.AddCalendar("cal", new HolidayCalendar());
        var job = ScenarioKit.Job("j", "g");
        var t = ScenarioKit.Repeating("t", "g", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        t.CalendarName = "cal";
        await store.ScheduleJob(job, t);

        var bundle = await ScenarioKit.FireOneAsync(store);
        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.SetTriggerError);
        Assert.Equal(TriggerState.Error, await store.GetTriggerState(t.Key));

        await store.AddCalendar("cal", new HolidayCalendar(), new AddCalendarOptions { Replace = true, UpdateTriggers = true });

        Assert.Equal(TriggerState.Error, await store.GetTriggerState(t.Key));
        Assert.Empty(await store.AcquireNextTriggers(ScenarioKit.DueNow(5)));
    }

    [Fact]
    public async Task C13_AddCalendar_with_UpdateTriggers_moves_a_waiting_trigger_off_a_newly_excluded_day()
    {
        var store = CreateStore("instance-1");
        var (tomorrow, t) = await ScheduleTomorrowWithCalendarAsync(store, repeating: true);
        var before = (await store.GetTrigger(t.Key))!.NextFireTimeUtc;
        Assert.Equal(tomorrow, before);

        await store.AddCalendar("cal", ExcludingDay(tomorrow), new AddCalendarOptions { Replace = true, UpdateTriggers = true });

        var after = (await store.GetTrigger(t.Key))!.NextFireTimeUtc;
        Assert.NotNull(after);
        Assert.True(after!.Value.UtcDateTime.Date > tomorrow.UtcDateTime.Date);
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(t.Key));
    }

    [Fact]
    public async Task N8_AddCalendar_with_UpdateTriggers_moves_a_paused_trigger_but_keeps_it_paused()
    {
        var store = CreateStore("instance-1");
        var (tomorrow, t) = await ScheduleTomorrowWithCalendarAsync(store, repeating: true);
        await store.PauseTrigger(t.Key);

        await store.AddCalendar("cal", ExcludingDay(tomorrow), new AddCalendarOptions { Replace = true, UpdateTriggers = true });

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(t.Key));
        Assert.True((await store.GetTrigger(t.Key))!.NextFireTimeUtc!.Value.UtcDateTime.Date > tomorrow.UtcDateTime.Date);
    }

    [Fact]
    public async Task N8_a_waiting_trigger_the_new_calendar_leaves_nothing_to_fire_is_complete()
    {
        var store = CreateStore("instance-1");
        var (tomorrow, t) = await ScheduleTomorrowWithCalendarAsync(store, repeating: false);

        await store.AddCalendar("cal", ExcludingDay(tomorrow), new AddCalendarOptions { Replace = true, UpdateTriggers = true });

        Assert.Null((await store.GetTrigger(t.Key))!.NextFireTimeUtc);
        Assert.Equal(TriggerState.Complete, await store.GetTriggerState(t.Key));
    }

    [Fact]
    public async Task N8_AddCalendar_without_UpdateTriggers_leaves_the_triggers_as_they_were()
    {
        var store = CreateStore("instance-1");
        var (tomorrow, t) = await ScheduleTomorrowWithCalendarAsync(store, repeating: true);

        await store.AddCalendar("cal", ExcludingDay(tomorrow), new AddCalendarOptions { Replace = true, UpdateTriggers = false });

        Assert.Equal(tomorrow, (await store.GetTrigger(t.Key))!.NextFireTimeUtc);
    }

    private static async Task<(DateTimeOffset Tomorrow, IOperableTrigger Trigger)> ScheduleTomorrowWithCalendarAsync(NCacheJobStore store, bool repeating)
    {
        await store.AddCalendar("cal", new HolidayCalendar { TimeZone = TimeZoneInfo.Utc });
        var job = ScenarioKit.Job("j", "g");
        var tomorrow = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1).AddHours(10), TimeSpan.Zero);
        var t = repeating
            ? ScenarioKit.Repeating("t", "g", job.Key, tomorrow, TimeSpan.FromHours(1))
            : ScenarioKit.OneShot("t", "g", job.Key, tomorrow);
        t.CalendarName = "cal";
        await store.ScheduleJob(job, t);

        return (tomorrow, t);
    }

    private static HolidayCalendar ExcludingDay(DateTimeOffset day)
    {
        var calendar = new HolidayCalendar { TimeZone = TimeZoneInfo.Utc };
        calendar.AddExcludedDay(DateOnly.FromDateTime(day.UtcDateTime));
        return calendar;
    }
}

