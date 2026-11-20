namespace NCache.OSS.Quartz.Keys;

public sealed class NCacheKeys
{
    internal const string DefaultPrefix = "ncache:quartz:";

    private static readonly char[] Reserved = { '%', '.', ':' };

    internal static string Escape(string part)
        => part.IndexOfAny(Reserved) < 0
            ? part
            : part.Replace("%", "%25").Replace(".", "%2E").Replace(":", "%3A");

    private static string Pair(string group, string name) => $"{Escape(group)}.{Escape(name)}";

    public static string Job(string prefix, string schedName, string jobGroup, string jobName)
        => $"{prefix}{schedName}:job:{Pair(jobGroup, jobName)}";

    public static string JobTriggers(string prefix, string schedName, string jobGroup, string jobName)
        => $"{prefix}{schedName}:job:{Pair(jobGroup, jobName)}:triggers";

    public static string Trigger(string prefix, string schedName, string triggerGroup, string triggerName)
        => $"{prefix}{schedName}:trigger:{Pair(triggerGroup, triggerName)}";

    public static string TriggerContinuations(string prefix, string schedName, string triggerGroup, string triggerName)
        => $"{prefix}{schedName}:trigger:{Pair(triggerGroup, triggerName)}:continuations";

    public static string Calendar(string prefix, string schedName, string calendarName)
        => $"{prefix}{schedName}:calendar:{calendarName}";

    public static string CalendarRegistry(string prefix, string schedName)
        => $"{prefix}{schedName}:calendars";

    public static string FiredTrigger(string prefix, string schedName, string entryId)
        => $"{prefix}{schedName}:fired:{entryId}";

    public static string InstanceFiredTriggers(string prefix, string schedName, string instanceName)
        => $"{prefix}{schedName}:instance:{instanceName}:fired";

    public static string JobFiredTriggers(string prefix,string schedName,string jobGroup, string jobName)
    => $"{prefix}{schedName}:job:{Pair(jobGroup, jobName)}:fired";
    public static string SchedulerState(string prefix, string schedName, string instanceName)
        => $"{prefix}{schedName}:state:{instanceName}";

    public static string SchedulerInstances(string prefix, string schedName)
        => $"{prefix}{schedName}:instances";

    public static string JobGroups(string prefix, string schedName)
        => $"{prefix}{schedName}:jobgroups";

    public static string TriggerGroups(string prefix, string schedName)
        => $"{prefix}{schedName}:triggergroups";

    public static string JobsInGroup(string prefix, string schedName, string jobGroup)
        => $"{prefix}{schedName}:jobgroup:{jobGroup}:jobs";

    public static string TriggersInGroup(string prefix, string schedName, string triggerGroup)
        => $"{prefix}{schedName}:triggergroup:{triggerGroup}:triggers";

    public static string PausedTriggerGroups(string prefix, string schedName)
        => $"{prefix}{schedName}:pausedtriggergroups";

    public static string PausedJobGroups(string prefix, string schedName)
        => $"{prefix}{schedName}:pausedjobgroups";

    public static string PausedTriggerGroupRecords(string prefix, string schedName)
        => $"{prefix}{schedName}:pausedtriggergroups:records";

    public static string PausedJobGroupRecords(string prefix, string schedName)
        => $"{prefix}{schedName}:pausedjobgroups:records";
    public static string ResumedJobsInPausedGroups(string prefix, string schedulerName)
        =>$"{prefix}{schedulerName}:paused-job-resumed";

    public static string TriggerAccessLock(string prefix, string schedName)
        => $"{prefix}{schedName}:lock:trigger_access";

    public static string StateAccessLock(string prefix, string schedName)
        => $"{prefix}{schedName}:lock:state_access";

    public static string FiringsAccessLock(string prefix, string schedName, string instanceName)
        => $"{prefix}{schedName}:lock:firings_access:{instanceName}";

    public static string LockRemoteWaitingMarker(string lockKey)
        => $"{lockKey}:waiting";
    public static string JobHeader(string prefix, string schedName, string jobGroup, string jobName)
    => $"{prefix}{schedName}:job:{Pair(jobGroup, jobName)}:header";

    public static string NextFireTimeIndexBucket(string prefix, string schedName, long bucketId) => $"{prefix}{schedName}:index:nextfiretime:{bucketId}";

    public static string NextFireTimeActiveBuckets(string prefix, string schedName) => $"{prefix}{schedName}:index:nextfiretime:active";
}

