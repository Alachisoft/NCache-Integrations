using Alachisoft.NCache.Client;
using NCache.OSS.Quartz.Index;
using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Locking;
using NCache.OSS.Quartz.Model;
using NCache.OSS.Quartz.Serialization;
using NCache.OSS.Quartz.Storage;
using Quartz;
using Quartz.Extensibility;
using Quartz.Impl.Calendar;
using Quartz.Impl.Triggers;
using Xunit;

namespace NCache.OSS.Quartz.Tests;
public sealed class NCacheQuartzCalendarUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task AddCalendar_stores_new_calendar()
    {
        var store = CreateStore($"j01-{Guid.NewGuid():N}");
        var name = $"calendar-{Guid.NewGuid():N}";
        var calendar = new HolidayCalendar();

        await store.AddCalendar(name, calendar);

        var stored = await Storage.GetCalendarAsync(name);

        Assert.NotNull(stored);
        Assert.Equal(name, stored.CalendarName);

        await store.Shutdown();
    }

    [Fact]
    public async Task AddCalendar_rejects_duplicate_without_replace()
    {
        var store = CreateStore($"j02-{Guid.NewGuid():N}");
        var name = $"calendar-{Guid.NewGuid():N}";

        await store.AddCalendar(name, new HolidayCalendar());

        await Assert.ThrowsAsync<ObjectAlreadyExistsException>(async () => await store.AddCalendar(name, new HolidayCalendar()));

        await store.Shutdown();
    }

    [Fact]
    public async Task AddCalendar_replaces_existing_calendar()
    {
        var store = CreateStore($"j03-{Guid.NewGuid():N}");
        var name = $"calendar-{Guid.NewGuid():N}";

        var first = new HolidayCalendar();
        var second = new HolidayCalendar();

        second.AddExcludedDay(DateOnly.FromDateTime(DateTime.UtcNow.Date));

        await store.AddCalendar(name, first);
        await store.AddCalendar(name, second, new AddCalendarOptions { Replace = true });

        var result = await store.GetCalendar(name);

        var holiday = Assert.IsType<HolidayCalendar>(result);

        Assert.Contains(DateOnly.FromDateTime(DateTime.UtcNow.Date), holiday.DaysExcluded);

        await store.Shutdown();
    }

    [Fact]
    public async Task AddCalendar_registers_calendar_name()
    {
        var store = CreateStore($"j04-{Guid.NewGuid():N}");
        var name = $"calendar-{Guid.NewGuid():N}";

        await store.AddCalendar(name, new HolidayCalendar());

        var result = await store.QueryCalendarNames(new CalendarQuery { Take = 100 });

        Assert.Contains(name, result.Items);

        await store.Shutdown();
    }

    [Fact]
    public async Task AddCalendar_without_update_triggers_does_not_modify_triggers()
    {
        var store = CreateStore($"j05-{Guid.NewGuid():N}");
        var calendarName = $"calendar-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var nextFire = DateTimeOffset.UtcNow.AddMinutes(10);
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, nextFire);
        trigger.CalendarName = calendarName;

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(trigger, false);
        await Index.UpsertAsync(trigger);
        await store.AddCalendar(calendarName, new HolidayCalendar());

        var before = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);

        var replacement = new HolidayCalendar();
        await store.AddCalendar(calendarName, replacement, new AddCalendarOptions { Replace = true });

        var after = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);

        Assert.Equal(before!.NextFireTimeUtc, after!.NextFireTimeUtc);
        Assert.Equal(before.TriggerState, after.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task AddCalendar_update_triggers_recalculates_next_fire_time()
    {
        var store = CreateStore($"j06-{Guid.NewGuid():N}");
        var calendarName = $"calendar-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}");

        var start = DateTimeOffset.UtcNow.Date.AddDays(1).AddHours(10);
        var trigger = CreateQuartzSimpleTrigger(
            $"trigger-{Guid.NewGuid():N}",
            "tests",
            new JobKey(job.JobName, job.JobGroup),
            start,
            repeatCount: 5);

        trigger.CalendarName = calendarName;

        await Storage.StoreJobAsync(job, false);

        await Storage.StoreTriggerAsync(
            Mapping.TriggerMapper.ToModel(trigger, SchedulerName, TriggerStates.Waiting),
            false);

        await Index.UpsertAsync(
            Mapping.TriggerMapper.ToModel(trigger, SchedulerName, TriggerStates.Waiting));

        await store.AddCalendar(calendarName, new HolidayCalendar());

        var replacement = new HolidayCalendar();
        replacement.AddExcludedDay(DateOnly.FromDateTime(start.Date));

        await store.AddCalendar(calendarName, replacement, new AddCalendarOptions
        {
            Replace = true,
            UpdateTriggers = true
        });

        var stored = await Storage.GetTriggerAsync(trigger.Key.Group, trigger.Key.Name);

        Assert.NotNull(stored);
        Assert.NotEqual(start, stored.NextFireTimeUtc);

        await store.Shutdown();
    }

    [Fact]
    public async Task AddCalendar_update_triggers_marks_trigger_complete_when_no_next_fire_time()
    {
        var store = CreateStore($"j07-{Guid.NewGuid():N}");
        var calendarName = $"calendar-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}");

        var start = DateTimeOffset.UtcNow.Date.AddDays(1).AddHours(10);

        var trigger = CreateQuartzSimpleTrigger(
            $"trigger-{Guid.NewGuid():N}",
            "tests",
            new JobKey(job.JobName, job.JobGroup),
            start,
            repeatCount: 0);

        trigger.CalendarName = calendarName;

        var data = Mapping.TriggerMapper.ToModel(trigger, SchedulerName, TriggerStates.Waiting);

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(data, false);
        await Index.UpsertAsync(data);

        await store.AddCalendar(calendarName, new HolidayCalendar());

        var replacement = new HolidayCalendar();
        replacement.AddExcludedDay(DateOnly.FromDateTime(start.Date));

        await store.AddCalendar(calendarName, replacement, new AddCalendarOptions
        {
            Replace = true,
            UpdateTriggers = true
        });

        var stored = await Storage.GetTriggerAsync(trigger.Key.Group, trigger.Key.Name);

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Complete, stored.TriggerState);
        Assert.Null(stored.NextFireTimeUtc);

        await store.Shutdown();
    }

    [Fact]
    public async Task AddCalendar_update_triggers_preserves_paused_state()
    {
        var store = CreateStore($"j08-{Guid.NewGuid():N}");
        var calendarName = $"calendar-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}");

        var start = DateTimeOffset.UtcNow.Date.AddDays(1).AddHours(10);
        var trigger = CreateQuartzSimpleTrigger(
            $"trigger-{Guid.NewGuid():N}",
            "tests",
            new JobKey(job.JobName, job.JobGroup),
            start);

        trigger.CalendarName = calendarName;

        var data = Mapping.TriggerMapper.ToModel(trigger, SchedulerName, TriggerStates.Paused);

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(data, false);
        await Index.UpsertAsync(data);
        await store.AddCalendar(calendarName, new HolidayCalendar());

        var replacement = new HolidayCalendar();
        replacement.AddExcludedDay(DateOnly.FromDateTime(start.Date));

        await store.AddCalendar(calendarName, replacement, new AddCalendarOptions
        {
            Replace = true,
            UpdateTriggers = true
        });

        var stored = await Storage.GetTriggerAsync(trigger.Key.Group, trigger.Key.Name);

        Assert.Equal(TriggerStates.Paused, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task AddCalendar_update_triggers_preserves_paused_and_blocked_state()
    {
        var store = CreateStore($"j09-{Guid.NewGuid():N}");
        var calendarName = $"calendar-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}", nonConcurrent: true);

        var start = DateTimeOffset.UtcNow.Date.AddDays(1).AddHours(10);
        var trigger = CreateQuartzSimpleTrigger(
            $"trigger-{Guid.NewGuid():N}",
            "tests",
            new JobKey(job.JobName, job.JobGroup),
            start);

        trigger.CalendarName = calendarName;

        var data = Mapping.TriggerMapper.ToModel(trigger, SchedulerName, TriggerStates.PausedAndBlocked);

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(data, false);
        await Index.UpsertAsync(data);
        await store.AddCalendar(calendarName, new HolidayCalendar());

        var replacement = new HolidayCalendar();
        replacement.AddExcludedDay(DateOnly.FromDateTime(start.Date));

        await store.AddCalendar(calendarName, replacement, new AddCalendarOptions
        {
            Replace = true,
            UpdateTriggers = true
        });

        var stored = await Storage.GetTriggerAsync(trigger.Key.Group, trigger.Key.Name);

        Assert.Equal(TriggerStates.PausedAndBlocked, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task AddCalendar_updates_next_fire_time_index()
    {
        var store = CreateStore($"j10-{Guid.NewGuid():N}");
        var calendarName = $"calendar-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var start = DateTimeOffset.UtcNow.AddMinutes(10);

        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, start);
        trigger.CalendarName = calendarName;

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(trigger, false);
        await Index.UpsertAsync(trigger);

        await store.AddCalendar(calendarName, new HolidayCalendar());

        Assert.Contains(await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(20), 100), x => x.TriggerName == trigger.TriggerName);

        await store.AddCalendar(calendarName, new HolidayCalendar(), new AddCalendarOptions
        {
            Replace = true,
            UpdateTriggers = true
        });

        Assert.Contains(await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(20), 100), x => x.TriggerName == trigger.TriggerName);

        await store.Shutdown();
    }

    [Fact]
    public async Task AddCalendar_honors_trigger_calendar_name()
    {
        var store = CreateStore($"j11-{Guid.NewGuid():N}");
        var calendarName = $"calendar-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        trigger.CalendarName = calendarName;

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(trigger, false);

        await store.AddCalendar(calendarName, new HolidayCalendar());

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);

        Assert.Equal(calendarName, stored!.CalendarName);

        await store.Shutdown();
    }

    [Fact]
    public async Task DeleteCalendar_removes_existing_calendar()
    {
        var store = CreateStore($"j12-{Guid.NewGuid():N}");
        var name = $"calendar-{Guid.NewGuid():N}";

        await store.AddCalendar(name, new HolidayCalendar());

        Assert.True(await store.DeleteCalendar(name));
        Assert.Null(await Storage.GetCalendarAsync(name));

        await store.Shutdown();
    }

    [Fact]
    public async Task DeleteCalendar_returns_false_for_missing_calendar()
    {
        var store = CreateStore($"j13-{Guid.NewGuid():N}");

        var result = await store.DeleteCalendar($"missing-{Guid.NewGuid():N}");

        Assert.False(result);

        await store.Shutdown();
    }

    [Fact]
    public async Task DeleteCalendar_rejects_calendar_referenced_by_trigger()
    {
        var store = CreateStore($"j14-{Guid.NewGuid():N}");
        var calendarName = $"calendar-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        trigger.CalendarName = calendarName;

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(trigger, false);

        await store.AddCalendar(calendarName, new HolidayCalendar());

        await Assert.ThrowsAsync<JobPersistenceException>(async () => await store.DeleteCalendar(calendarName));

        Assert.NotNull(await Storage.GetCalendarAsync(calendarName));

        await store.Shutdown();
    }

    [Fact]
    public async Task DeleteCalendar_cleans_registry()
    {
        var store = CreateStore($"j15-{Guid.NewGuid():N}");
        var name = $"calendar-{Guid.NewGuid():N}";

        await store.AddCalendar(name, new HolidayCalendar());
        Assert.Contains(name, (await store.QueryCalendarNames(new CalendarQuery { Take = 100 })).Items);

        await store.DeleteCalendar(name);

        var result = await store.QueryCalendarNames(new CalendarQuery { Take = 100 });

        Assert.DoesNotContain(name, result.Items);

        await store.Shutdown();
    }

    [Fact]
    public async Task GetCalendar_returns_deserialized_calendar()
    {
        var store = CreateStore($"j16-{Guid.NewGuid():N}");
        var name = $"calendar-{Guid.NewGuid():N}";
        var calendar = new HolidayCalendar();
        var date = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        calendar.AddExcludedDay(date);

        await store.AddCalendar(name, calendar);

        var result = await store.GetCalendar(name);

        var restored = Assert.IsType<HolidayCalendar>(result);

        Assert.Contains(date, restored.DaysExcluded);

        await store.Shutdown();
    }

    [Fact]
    public async Task GetCalendar_returns_null_for_missing_calendar()
    {
        var store = CreateStore($"j17-{Guid.NewGuid():N}");

        var result = await store.GetCalendar($"missing-{Guid.NewGuid():N}");

        Assert.Null(result);

        await store.Shutdown();
    }

    [Fact]
    public async Task Exists_calendar_returns_true_when_present()
    {
        var store = CreateStore($"j18-{Guid.NewGuid():N}");
        var name = $"calendar-{Guid.NewGuid():N}";

        await store.AddCalendar(name, new HolidayCalendar());

        Assert.True(await store.Exists(name));

        await store.Shutdown();
    }

    [Fact]
    public async Task Exists_calendar_returns_false_when_missing()
    {
        var store = CreateStore($"j19-{Guid.NewGuid():N}");

        Assert.False(await store.Exists($"missing-{Guid.NewGuid():N}"));

        await store.Shutdown();
    }

    [Fact]
    public async Task AddCalendar_honors_cancellation()
    {
        var store = CreateStore($"j20-{Guid.NewGuid():N}");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.AddCalendar($"calendar-{Guid.NewGuid():N}", new HolidayCalendar(), cancellationToken: cts.Token));
    } 
}

