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

public sealed class NCacheQuartzTimeOfDayFormatUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task N11_a_DailyTimeInterval_trigger_stored_under_one_culture_loads_under_another()
    {
        var store = CreateStore("instance-1");
        var job = ScenarioKit.Job("j", "g");
        var trigger = (IOperableTrigger)TriggerBuilder.Create()
            .WithIdentity("t", "g")
            .ForJob(job)
            .StartAt(DateTimeOffset.UtcNow.AddHours(1))
            .WithDailyTimeIntervalSchedule(x => x.WithInterval(1, IntervalUnit.Hour).StartingDailyAt(new TimeOnly(8, 0)).EndingDailyAt(new TimeOnly(17, 30, 15)))
            .Build();
        trigger.ComputeFirstFireTimeUtc(null);

        var dotted = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        dotted.DateTimeFormat.TimeSeparator = ".";
        Assert.Equal("08.00.00", new TimeOnly(8, 0).ToString("HH:mm:ss", dotted));

        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = dotted;
            await store.ScheduleJob(job, trigger);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        var raw = await Storage.GetTriggerAsync("g", "t");
        Assert.Equal("08:00:00", raw!.DailyTimeInterval!.StartTimeOfDay);
        Assert.Equal("17:30:15", raw.DailyTimeInterval.EndTimeOfDay);

        IOperableTrigger? back;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            back = await store.GetTrigger(trigger.Key);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        var daily = Assert.IsAssignableFrom<IDailyTimeIntervalTrigger>(back);
        Assert.Equal(new TimeOnly(8, 0), daily.StartTimeOfDay);
        Assert.Equal(new TimeOnly(17, 30, 15), daily.EndTimeOfDay);
        Assert.Equal(trigger.NextFireTimeUtc, back!.NextFireTimeUtc);
    }

    [Theory]
    [InlineData("08:00:00", 8, 0, 0)]
    [InlineData("17:30:15", 17, 30, 15)]
    [InlineData("00:00:00", 0, 0, 0)]
    public void N11_a_time_of_day_stored_in_the_invariant_form_is_read(string stored, int hour, int minute, int second)
    {
        var trigger = TriggerMapper.ToQuartzTrigger(DailyRow(stored));

        Assert.Equal(new TimeOnly(hour, minute, second), ((IDailyTimeIntervalTrigger)trigger).StartTimeOfDay);
    }

    [Theory]
    [InlineData("eight o'clock")]
    [InlineData("25:00:00")]
    [InlineData("08:00")]
    [InlineData("08.00.00")]
    public void N11_a_time_of_day_that_is_not_the_invariant_hours_minutes_seconds_form_is_refused(string stored)
    {
        Assert.Throws<FormatException>(() => TriggerMapper.ToQuartzTrigger(DailyRow(stored)));
    }

    private static NCacheTriggerData DailyRow(string startTimeOfDay) => new()
    {
        SchedulerName = SchedulerName,
        TriggerName = "t",
        TriggerGroup = "g",
        JobName = "j",
        JobGroup = "g",
        TriggerState = TriggerStates.Waiting,
        TriggerKind = NCacheTriggerKind.DailyTimeInterval,
        StartTimeUtc = DateTimeOffset.UtcNow,
        NextFireTimeUtc = DateTimeOffset.UtcNow.AddHours(1),
        DailyTimeInterval = new NCacheDailyTimeIntervalTriggerData
        {
            RepeatInterval = 1,
            RepeatIntervalUnit = nameof(IntervalUnit.Hour),
            TimeZoneId = TimeZoneInfo.Utc.Id,
            StartTimeOfDay = startTimeOfDay,
            EndTimeOfDay = "23:00:00"
        }
    };
}

