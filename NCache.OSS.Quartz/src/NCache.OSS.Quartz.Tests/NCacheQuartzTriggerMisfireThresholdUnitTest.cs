using NCache.OSS.Quartz.Serialization;
using Quartz;
using Quartz.Extensibility;
using Quartz.Impl.Calendar;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzTriggerMisfireThresholdUnitTest : NCacheQuartzScenarioTestBase
{
    [Fact]
    public async Task A_trigger_misfire_threshold_is_stored_and_read_back()
    {
        var store = await NewNodeAsync();
        var job = Job("job");
        var trigger = Hourly("t", job.Key, DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(5));

        await store.ScheduleJob(job, trigger);

        Assert.Equal(TimeSpan.FromSeconds(5), (await Storage.GetTriggerAsync("tests", "t"))!.MisfireThreshold);
        Assert.Equal(TimeSpan.FromSeconds(5), (await store.GetTrigger(trigger.Key))!.MisfireThreshold);
    }

    [Fact]
    public async Task A_trigger_without_a_threshold_stores_none_and_writes_no_field()
    {
        var store = await NewNodeAsync();
        var job = Job("job");
        var trigger = Hourly("t", job.Key, DateTimeOffset.UtcNow.AddMinutes(5), misfireThreshold: null);

        await store.ScheduleJob(job, trigger);

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Null(row.MisfireThreshold);
        Assert.Null((await store.GetTrigger(trigger.Key))!.MisfireThreshold);
        Assert.DoesNotContain("MisfireThreshold", System.Text.Encoding.UTF8.GetString(NCacheSerializer.Serialize(row)));
    }

    [Fact]
    public async Task A_shorter_trigger_threshold_misfires_before_the_store_threshold_would()
    {
        var store = await NewNodeAsync();
        store.MisfireThreshold = TimeSpan.FromSeconds(60);
        var dueAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        var shortJob = Job("short-job");
        var plainJob = Job("plain-job");
        var shortTrigger = Hourly("short", shortJob.Key, dueAt, TimeSpan.FromSeconds(1));
        var plainTrigger = Hourly("plain", plainJob.Key, dueAt, misfireThreshold: null);
        await store.ScheduleJob(shortJob, shortTrigger);
        await store.ScheduleJob(plainJob, plainTrigger);

        var acquired = await AcquireDueAsync(store, maxCount: 2);

        Assert.Equal(plainTrigger.Key, Assert.Single(acquired).Key);
        Assert.True((await store.GetTrigger(shortTrigger.Key))!.NextFireTimeUtc > DateTimeOffset.UtcNow.AddMinutes(50));
    }

    [Fact]
    public async Task A_longer_trigger_threshold_keeps_a_late_trigger_from_misfiring_under_the_store_threshold()
    {
        var store = await NewNodeAsync();
        store.MisfireThreshold = TimeSpan.FromSeconds(1);
        var dueAt = DateTimeOffset.UtcNow.AddSeconds(-30);
        var longJob = Job("long-job");
        var plainJob = Job("plain-job");
        var longTrigger = Hourly("long", longJob.Key, dueAt, TimeSpan.FromMinutes(10));
        var plainTrigger = Hourly("plain", plainJob.Key, dueAt, misfireThreshold: null);
        await store.ScheduleJob(longJob, longTrigger);
        await store.ScheduleJob(plainJob, plainTrigger);

        var acquired = Assert.Single(await AcquireDueAsync(store, maxCount: 2));

        Assert.Equal(longTrigger.Key, acquired.Key);
        Assert.Equal(longTrigger.NextFireTimeUtc, acquired.NextFireTimeUtc);
        Assert.True((await store.GetTrigger(plainTrigger.Key))!.NextFireTimeUtc > DateTimeOffset.UtcNow.AddMinutes(50));
    }

    [Fact]
    public async Task Resuming_applies_the_trigger_threshold_rather_than_the_store_threshold()
    {
        var store = await NewNodeAsync();
        store.MisfireThreshold = TimeSpan.FromSeconds(60);
        var dueAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        var shortJob = Job("short-job");
        var plainJob = Job("plain-job");
        var shortTrigger = Hourly("short", shortJob.Key, dueAt, TimeSpan.FromSeconds(1));
        var plainTrigger = Hourly("plain", plainJob.Key, dueAt, misfireThreshold: null);
        await store.ScheduleJob(shortJob, shortTrigger);
        await store.ScheduleJob(plainJob, plainTrigger);
        await store.PauseTriggers(new[] { shortTrigger.Key, plainTrigger.Key });

        await store.ResumeTrigger(shortTrigger.Key);
        await store.ResumeTrigger(plainTrigger.Key);

        Assert.True((await store.GetTrigger(shortTrigger.Key))!.NextFireTimeUtc > DateTimeOffset.UtcNow.AddMinutes(50));
        Assert.Equal(plainTrigger.NextFireTimeUtc, (await store.GetTrigger(plainTrigger.Key))!.NextFireTimeUtc);
    }

    [Fact]
    public async Task A_replaced_calendar_moves_the_trigger_by_its_own_threshold()
    {
        var store = await NewNodeAsync();
        store.MisfireThreshold = TimeSpan.FromDays(10);
        var previous = DateTimeOffset.UtcNow.AddDays(-3).AddHours(-1);
        await store.AddCalendar("cal", new HolidayCalendar { TimeZone = TimeZoneInfo.Utc });
        var shortJob = Job("short-job");
        var plainJob = Job("plain-job");
        var shortTrigger = DailyAfter("short", shortJob.Key, previous, TimeSpan.FromMinutes(1));
        var plainTrigger = DailyAfter("plain", plainJob.Key, previous, misfireThreshold: null);
        await store.ScheduleJob(shortJob, shortTrigger);
        await store.ScheduleJob(plainJob, plainTrigger);

        var holidays = new HolidayCalendar { TimeZone = TimeZoneInfo.Utc };
        holidays.AddExcludedDay(DateOnly.FromDateTime(previous.AddDays(1).UtcDateTime));
        await store.AddCalendar("cal", holidays, new AddCalendarOptions { Replace = true, UpdateTriggers = true });

        Assert.Equal(previous.AddDays(3), (await store.GetTrigger(shortTrigger.Key))!.NextFireTimeUtc);
        Assert.Equal(previous.AddDays(2), (await store.GetTrigger(plainTrigger.Key))!.NextFireTimeUtc);
    }

    // Builds an hourly trigger first due at the given time, with an optional misfire threshold of its own.
    private static IOperableTrigger Hourly(string name, JobKey job, DateTimeOffset startAt, TimeSpan? misfireThreshold)
    {
        var builder = TriggerBuilder.Create()
            .WithIdentity(name, "tests")
            .ForJob(job)
            .StartAt(startAt)
            .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromHours(1)).RepeatForever().WithMisfireInstruction(SimpleTriggerMisfireInstruction.NextWithRemainingCount));

        if (misfireThreshold is { } threshold)
        {
            builder = builder.WithMisfireThreshold(threshold);
        }

        var trigger = (IOperableTrigger) builder.Build();
        trigger.ComputeFirstFireTimeUtc(null);
        return trigger;
    }

    // Builds a daily trigger on calendar "cal" that last fired at the given time, with an optional misfire threshold of its own.
    private static IOperableTrigger DailyAfter(string name, JobKey job, DateTimeOffset previousFire, TimeSpan? misfireThreshold)
    {
        var builder = TriggerBuilder.Create()
            .WithIdentity(name, "tests")
            .ForJob(job)
            .StartAt(previousFire)
            .WithCalendarName("cal")
            .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromDays(1)).RepeatForever());

        if (misfireThreshold is { } threshold)
        {
            builder = builder.WithMisfireThreshold(threshold);
        }

        var trigger = (IOperableTrigger) builder.Build();
        trigger.PreviousFireTimeUtc = previousFire;
        trigger.NextFireTimeUtc = previousFire.AddDays(1);
        return trigger;
    }
}
