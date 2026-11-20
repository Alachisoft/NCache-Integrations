using System.Globalization;
using NCache.OSS.Quartz.Model;
using NCache.OSS.Quartz.Serialization;
using Quartz;
using Quartz.Extensibility;
using Quartz.Impl.Triggers;

namespace NCache.OSS.Quartz.Mapping;

internal static class TriggerMapper
{
    private static readonly HashSet<Type> BuiltInTypes =
    [
        typeof(SimpleTriggerImpl),
        typeof(CronTriggerImpl),
        typeof(CalendarIntervalTriggerImpl),
        typeof(DailyTimeIntervalTriggerImpl),
        typeof(RecurrenceTriggerImpl)
    ];

    public static NCacheTriggerData ToModel(IOperableTrigger trigger, string schedName, string triggerState, QuartzObjectSerialization? serialization = null)
    {
        var data = new NCacheTriggerData
        {
            SchedulerName = schedName,
            TriggerName = trigger.Key.Name,
            TriggerGroup = trigger.Key.Group,
            JobName = trigger.JobKey.Name,
            JobGroup = trigger.JobKey.Group,
            Description = trigger.Description,
            NextFireTimeUtc = trigger.NextFireTimeUtc,
            PreviousFireTimeUtc = trigger.PreviousFireTimeUtc,
            Priority = trigger.Priority,
            TriggerState = triggerState,
            TriggerKind = NCacheTriggerKind.Custom,
            StartTimeUtc = trigger.StartTimeUtc,
            EndTimeUtc = trigger.EndTimeUtc,
            CalendarName = trigger.CalendarName,
            MisfireInstruction = trigger.MisfireInstructionCode,
            MisfireThreshold = trigger.MisfireThreshold,
            ExecutionGroup = trigger.ExecutionGroup,
            RetryAttempt = trigger.RetryAttempt,
            RetryPolicy = trigger.RetryPolicy?.ToStoredString(),
            PreferredNode = trigger.PreferredNode.Node,
            PreferredNodeIsAutomatic = trigger.PreferredNode.IsAutomatic,
            ContinuesAfterTriggerName = trigger.Continuation.Parent?.Name,
            ContinuesAfterTriggerGroup = trigger.Continuation.Parent?.Group,
            ContinuationCondition = ContinuationRules.StoredCondition(trigger.Continuation),
            OverlapPolicy = (int) trigger.OverlapPolicy,
            JobDataMap = JobDataMapMapper.ToStorageMap(trigger.JobDataMap, serialization)
        };

        ApplyScheduleState(data, trigger, serialization);

        return data;
    }

    public static void ApplyScheduleState(NCacheTriggerData data, IOperableTrigger trigger, QuartzObjectSerialization? serialization = null)
    {
        data.NextFireTimeUtc = trigger.NextFireTimeUtc;
        data.PreviousFireTimeUtc = trigger.PreviousFireTimeUtc;
        data.StartTimeUtc = trigger.StartTimeUtc;
        data.EndTimeUtc = trigger.EndTimeUtc;

        if (!BuiltInTypes.Contains(trigger.GetType()))
        {
            data.TriggerKind = NCacheTriggerKind.Custom;
            data.CustomTriggerJson = (serialization ?? QuartzObjectSerialization.Default).SerializeTrigger(trigger);
            return;
        }

        switch (trigger)
        {
            case ISimpleTrigger simple:
                data.TriggerKind = NCacheTriggerKind.Simple;
                data.Simple = new NCacheSimpleTriggerData
                {
                    RepeatCount = simple.RepeatCount,
                    RepeatInterval = simple.RepeatInterval,
                    TimesTriggered = simple.TimesTriggered
                };
                break;

            case ICronTrigger cron:
                data.TriggerKind = NCacheTriggerKind.Cron;
                data.Cron = new NCacheCronTriggerData
                {
                    CronExpression = cron.CronExpressionString!,
                    TimeZoneId = cron.TimeZone.Id
                };
                break;

            case ICalendarIntervalTrigger ci:
                data.TriggerKind = NCacheTriggerKind.CalendarInterval;
                data.CalendarInterval = new NCacheCalendarIntervalTriggerData
                {
                    RepeatInterval = ci.RepeatInterval,
                    RepeatIntervalUnit = ci.RepeatIntervalUnit.ToString(),
                    TimesTriggered = ci.TimesTriggered,
                    TimeZoneId = ci.TimeZone.Id,
                    PreserveHourOfDayAcrossDaylightSavings = ci.PreserveHourOfDayAcrossDaylightSavings,
                    SkipDayIfHourDoesNotExist = ci.SkipDayIfHourDoesNotExist
                };
                break;

            case IDailyTimeIntervalTrigger dt:
                data.TriggerKind = NCacheTriggerKind.DailyTimeInterval;
                data.DailyTimeInterval = new NCacheDailyTimeIntervalTriggerData
                {
                    RepeatInterval = dt.RepeatInterval,
                    RepeatIntervalUnit = dt.RepeatIntervalUnit.ToString(),
                    TimesTriggered = dt.TimesTriggered,
                    TimeZoneId = dt.TimeZone.Id,
                    StartTimeOfDay = FormatTimeOfDay(dt.StartTimeOfDay),
                    EndTimeOfDay = FormatTimeOfDay(dt.EndTimeOfDay),
                    DaysOfWeek = dt.DaysOfWeek is null
                        ? null
                        : new HashSet<int>(dt.DaysOfWeek.Select(d => (int)d + 1)),
                    RepeatCount = dt.RepeatCount
                };
                break;

            case IRecurrenceTrigger rr:
                data.TriggerKind = NCacheTriggerKind.Recurrence;
                data.Recurrence = new NCacheRecurrenceTriggerData
                {
                    RecurrenceRule = rr.RecurrenceRule,
                    TimeZoneId = rr.TimeZone.Id,
                    TimesTriggered = rr.TimesTriggered
                };
                break;
        }
    }

    public static IOperableTrigger ToQuartzTrigger(NCacheTriggerData data, QuartzObjectSerialization? serialization = null)
    {
        IOperableTrigger trigger = data.TriggerKind switch
        {
            NCacheTriggerKind.Simple => BuildSimple(data),
            NCacheTriggerKind.Cron => BuildCron(data),
            NCacheTriggerKind.CalendarInterval => BuildCalendarInterval(data),
            NCacheTriggerKind.DailyTimeInterval => BuildDailyTimeInterval(data),
            NCacheTriggerKind.Recurrence => BuildRecurrence(data),
            NCacheTriggerKind.Custom when data.CustomTriggerJson is not null =>
                (serialization ?? QuartzObjectSerialization.Default).DeserializeTrigger(data.CustomTriggerJson, $"{data.TriggerGroup}.{data.TriggerName}"),
            _ => throw new NotSupportedException(
                $"Trigger '{data.TriggerGroup}.{data.TriggerName}' has kind '{data.TriggerKind}' — cannot reconstruct.")
        };

        trigger.Key = new TriggerKey(data.TriggerName, data.TriggerGroup);
        trigger.JobKey = new JobKey(data.JobName, data.JobGroup);
        trigger.Description = data.Description;
        trigger.CalendarName = data.CalendarName;
        trigger.MisfireInstructionCode = data.MisfireInstruction;
        trigger.Priority = data.Priority;
        trigger.StartTimeUtc = data.StartTimeUtc;
        trigger.EndTimeUtc = data.EndTimeUtc;
        trigger.NextFireTimeUtc = data.NextFireTimeUtc;
        trigger.PreviousFireTimeUtc = data.PreviousFireTimeUtc;
        trigger.ExecutionGroup = data.ExecutionGroup;
        trigger.RetryPolicy = data.RetryPolicy is null ? null : RetryPolicy.Parse(data.RetryPolicy);
        trigger.RetryAttempt = data.RetryAttempt;

        trigger.PreferredNode = data.PreferredNodeIsAutomatic
            ? PreferredNode.Auto
            : data.PreferredNode is null
                ? PreferredNode.None
                : PreferredNode.For(data.PreferredNode);

        foreach (var (key, value) in JobDataMapMapper.ToJobDataMap(data.JobDataMap))
        {
            trigger.JobDataMap[key] = value;
        }

        if (trigger is TriggerBase settable)
        {
            settable.Continuation = ContinuationRules.FromStored(data);
            settable.MisfireThreshold = data.MisfireThreshold;

            settable.OverlapPolicy = Enum.IsDefined((OverlapPolicy) data.OverlapPolicy)
                ? (OverlapPolicy) data.OverlapPolicy
                : OverlapPolicy.Default;
        }

        return trigger;
    }

    private static SimpleTriggerImpl BuildSimple(NCacheTriggerData data)
    {
        var s = data.Simple ?? throw new InvalidOperationException("Simple trigger missing Simple block.");

        return new SimpleTriggerImpl
        {
            RepeatCount = s.RepeatCount,
            RepeatInterval = s.RepeatInterval,
            TimesTriggered = s.TimesTriggered
        };
    }

    private static CronTriggerImpl BuildCron(NCacheTriggerData data)
    {
        var c = data.Cron ?? throw new InvalidOperationException("Cron trigger missing Cron block.");

        var trigger = new CronTriggerImpl
        {
            CronExpressionString = c.CronExpression
        };

        if (c.TimeZoneId is not null)
        {
            trigger.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(c.TimeZoneId);
        }

        return trigger;
    }

    private static CalendarIntervalTriggerImpl BuildCalendarInterval(NCacheTriggerData data)
    {
        var ci = data.CalendarInterval ?? throw new InvalidOperationException("CalendarInterval trigger missing block.");

        var trigger = new CalendarIntervalTriggerImpl
        {
            RepeatInterval = ci.RepeatInterval,
            RepeatIntervalUnit = Enum.Parse<IntervalUnit>(ci.RepeatIntervalUnit),
            TimesTriggered = ci.TimesTriggered,
            PreserveHourOfDayAcrossDaylightSavings = ci.PreserveHourOfDayAcrossDaylightSavings,
            SkipDayIfHourDoesNotExist = ci.SkipDayIfHourDoesNotExist
        };

        if (ci.TimeZoneId is not null)
        {
            trigger.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(ci.TimeZoneId);
        }

        return trigger;
    }

    private static DailyTimeIntervalTriggerImpl BuildDailyTimeInterval(NCacheTriggerData data)
    {
        var dt = data.DailyTimeInterval ?? throw new InvalidOperationException("DailyTimeInterval trigger missing block.");

        var trigger = new DailyTimeIntervalTriggerImpl
        {
            RepeatInterval = dt.RepeatInterval,
            RepeatIntervalUnit = Enum.Parse<IntervalUnit>(dt.RepeatIntervalUnit),
            TimesTriggered = dt.TimesTriggered,
            StartTimeOfDay = ParseTimeOfDay(dt.StartTimeOfDay),
            EndTimeOfDay = ParseTimeOfDay(dt.EndTimeOfDay),
            RepeatCount = dt.RepeatCount
        };

        if (dt.TimeZoneId is not null)
        {
            trigger.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(dt.TimeZoneId);
        }

        if (dt.DaysOfWeek is not null)
        {
            trigger.DaysOfWeek = new HashSet<DayOfWeek>(
                dt.DaysOfWeek.Select(d => (DayOfWeek)(d - 1)));
        }

        return trigger;
    }

    private static RecurrenceTriggerImpl BuildRecurrence(NCacheTriggerData data)
    {
        var rr = data.Recurrence ?? throw new InvalidOperationException("Recurrence trigger missing Recurrence block.");

        var trigger = new RecurrenceTriggerImpl
        {
            RecurrenceRule = rr.RecurrenceRule,
            TimesTriggered = rr.TimesTriggered
        };

        if (rr.TimeZoneId is not null)
        {
            trigger.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(rr.TimeZoneId);
        }

        return trigger;
    }

    private static string FormatTimeOfDay(TimeOnly time) => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static TimeOnly ParseTimeOfDay(string value)
    {
        if (TimeOnly.TryParseExact(value, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return time;
        }

        throw new FormatException($"Stored time of day '{value}' is not in the HH:mm:ss form.");
    }
}

