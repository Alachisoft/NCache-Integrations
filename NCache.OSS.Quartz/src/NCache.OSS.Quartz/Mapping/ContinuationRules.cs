using NCache.OSS.Quartz.Model;
using Quartz;

namespace NCache.OSS.Quartz.Mapping;

internal static class ContinuationRules
{
    private const int AllOutcomes = (int) ContinuationCondition.OnAnyOutcome;

    public static bool IsSatisfiable(int condition) => condition != 0 && (condition & ~AllOutcomes) == 0;

    public static int? StoredCondition(Continuation continuation)
        => continuation.Parent is null ? null : (int) continuation.When;

    public static Continuation FromStored(NCacheTriggerData data)
    {
        if (!data.HasContinuationParent)
        {
            return Continuation.None;
        }

        var when = data.ContinuationCondition is { } stored && IsSatisfiable(stored)
            ? (ContinuationCondition) stored
            : ContinuationCondition.OnAnyOutcome;

        return Continuation.After(new TriggerKey(data.ContinuesAfterTriggerName!, data.ContinuesAfterTriggerGroup ?? TriggerKey.DefaultGroup), when);
    }

    public static TriggerKey? ParentOf(NCacheTriggerData data)
        => data.HasContinuationParent
            ? new TriggerKey(data.ContinuesAfterTriggerName!, data.ContinuesAfterTriggerGroup ?? TriggerKey.DefaultGroup)
            : null;

    public static ContinuationCondition? ConditionOf(NCacheTriggerData data)
        => data.HasContinuationParent
            ? data.ContinuationCondition is { } stored && IsSatisfiable(stored) ? (ContinuationCondition) stored : ContinuationCondition.OnAnyOutcome
            : null;

    public static ContinuationCondition? ConditionFor(ExecutionOutcome outcome) => outcome switch
    {
        ExecutionOutcome.Succeeded => ContinuationCondition.OnSuccess,
        ExecutionOutcome.Failed => ContinuationCondition.OnFailure,
        ExecutionOutcome.Cancelled => ContinuationCondition.OnCancellation,
        ExecutionOutcome.Vetoed => ContinuationCondition.OnVeto,
        _ => null
    };

    public static bool Releases(NCacheTriggerData awaiting, ContinuationCondition satisfied)
        => ConditionOf(awaiting) is { } when && when.HasFlag(satisfied);

    public static DateTimeOffset? ReleaseFireTime(NCacheTriggerData trigger, ICalendar? calendar, DateTimeOffset now)
    {
        var fireTime = trigger.StartTimeUtc > now ? trigger.StartTimeUtc : now;

        if (calendar is not null && !calendar.IsTimeIncluded(fireTime))
        {
            try
            {
                fireTime = calendar.GetNextIncludedTimeUtc(fireTime);
            }
            catch (SchedulerException)
            {
                return null;
            }
        }

        return trigger.EndTimeUtc is { } end && fireTime > end ? null : fireTime;
    }

    public static void Forget(NCacheTriggerData trigger)
    {
        trigger.ContinuesAfterTriggerName = null;
        trigger.ContinuesAfterTriggerGroup = null;
        trigger.ContinuationCondition = null;
    }
}

