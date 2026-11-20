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

public sealed class NCacheQuartzCustomCalendarUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task N13_a_custom_calendar_without_a_serializer_is_refused_cleanly_without_a_repair()
    {
        var log = new CapturingLoggerProvider();
        var store = await ScenarioKit.CreateStoreAsync(_cache!, _prefix, SchedulerName, "instance-1", log);

        var ex = await Assert.ThrowsAsync<JobPersistenceException>(async () => await store.AddCalendar("weekdays", new WeekdayCalendar()));

        Assert.Contains(typeof(WeekdayCalendar).FullName!, ex.Message);
        Assert.Contains("CalendarSerializer", ex.Message);
        Assert.False(await store.Exists("weekdays"));
        Assert.False(log.Contains("rebuilding registries and index"), string.Join(Environment.NewLine, log.Lines));
    }

    [Fact]
    public async Task N13b_a_custom_calendar_is_storable_when_its_serializer_is_registered_through_the_options()
    {
        var store = CreateStoreFromOptions("instance-1", registry => registry.AddCalendarSerializer(new WeekdayCalendarSerializer()));
        await store.Initialize(new SchedulerIdentity { SchedulerName = SchedulerName, InstanceId = "instance-1" });

        await store.AddCalendar("weekdays", new WeekdayCalendar { Description = "Mon-Fri" });

        Assert.True(await store.Exists("weekdays"));
        var back = Assert.IsType<WeekdayCalendar>(await store.GetCalendar("weekdays"));
        Assert.Equal("Mon-Fri", back.Description);

        var holidays = new HolidayCalendar { TimeZone = TimeZoneInfo.Utc };
        holidays.AddExcludedDay(new DateOnly(2030, 12, 25));
        await store.AddCalendar("holidays", holidays);

        var holidaysBack = Assert.IsType<HolidayCalendar>(await store.GetCalendar("holidays"));
        Assert.False(holidaysBack.IsTimeIncluded(new DateTimeOffset(2030, 12, 25, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task N13b_a_custom_calendar_is_storable_with_the_scheduler_serializer_registry_a_container_supplies()
    {
        var registry = new SystemTextJsonSerializerRegistry();
        registry.AddCalendarSerializer(new WeekdayCalendarSerializer());

        var store = NCacheQuartzBuilderExtensions.CreateJobStore(_cache!, ScenarioKit.Options(_prefix, SchedulerName, "instance-1"), null, null, registry);
        await store.Initialize(new SchedulerIdentity { SchedulerName = SchedulerName, InstanceId = "instance-1" });

        await store.AddCalendar("weekdays", new WeekdayCalendar());

        Assert.IsType<WeekdayCalendar>(await store.GetCalendar("weekdays"));
    }

    [Fact]
    public async Task N13b_a_trigger_bound_to_a_registered_custom_calendar_fires_with_that_calendar()
    {
        var registry = new SystemTextJsonSerializerRegistry();
        registry.AddCalendarSerializer(new WeekdayCalendarSerializer());
        var store = await ScenarioKit.CreateStoreAsync(_cache!, _prefix, SchedulerName, "instance-1", calendarSerializer: new SystemTextJsonObjectSerializer(registry));

        await store.AddCalendar("weekdays", new WeekdayCalendar());
        var job = ScenarioKit.Job("j", "g");
        var t = ScenarioKit.Repeating("t", "g", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        t.CalendarName = "weekdays";
        await store.ScheduleJob(job, t);

        var bundle = await ScenarioKit.FireOneAsync(store);

        Assert.IsType<WeekdayCalendar>(bundle.Calendar);
        Assert.True(WeekdayCalendar.IsWeekday(bundle.NextFireTimeUtc!.Value));
    }

    private NCacheJobStore CreateStoreFromOptions(string instanceId, Action<SystemTextJsonSerializerRegistry>? configure)
    {
        var options = ScenarioKit.Options(_prefix, SchedulerName, instanceId);
        options.ConfigureSerializer = configure;
        return NCacheQuartzBuilderExtensions.CreateJobStore(_cache!, options, null);
    }
}

public sealed class WeekdayCalendar : BaseCalendar
{
    public static bool IsWeekday(DateTimeOffset timeUtc) => timeUtc.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    public override bool IsTimeIncluded(DateTimeOffset timeStampUtc) => IsWeekday(timeStampUtc) && base.IsTimeIncluded(timeStampUtc);

    public override DateTimeOffset GetNextIncludedTimeUtc(DateTimeOffset timeUtc)
    {
        var next = base.GetNextIncludedTimeUtc(timeUtc);

        while (!IsWeekday(next))
        {
            next = new DateTimeOffset(next.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
        }

        return next;
    }

    public override ICalendar Clone() => new WeekdayCalendar { Description = Description, TimeZone = TimeZone };
}

public sealed class WeekdayCalendarSerializer : CalendarSerializer<WeekdayCalendar>
{
    public override string CalendarTypeName => "WeekdayCalendar";

    protected override WeekdayCalendar Create(JsonElement jsonElement, JsonSerializerOptions options) => new();

    protected override void SerializeFields(Utf8JsonWriter writer, WeekdayCalendar calendar, JsonSerializerOptions options)
    {
    }

    protected override void DeserializeFields(WeekdayCalendar calendar, JsonElement jsonElement, JsonSerializerOptions options)
    {
    }
}

