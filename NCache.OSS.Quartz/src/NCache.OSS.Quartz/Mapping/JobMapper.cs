using Quartz;
using Quartz.Extensibility;
using NCache.OSS.Quartz.Model;
using NCache.OSS.Quartz.Serialization;

namespace NCache.OSS.Quartz.Mapping;

internal static class JobMapper
{
    public static NCacheJobData ToModel(IJobDetail job, string schedName, QuartzObjectSerialization? serialization = null) => new()
    {
        SchedulerName = schedName,
        JobName = job.Key.Name,
        JobGroup = job.Key.Group,
        Description = job.Description,
        JobClassName = job.JobType.FullName,
        IsDurable = job.Durable,
        IsNonConcurrent = job.ConcurrentExecutionDisallowed,
        IsUpdateData = job.PersistJobDataAfterExecution,
        RequestsRecovery = job.RequestsRecovery,
        JobDataMap = JobDataMapMapper.ToStorageMap(job.JobDataMap, serialization)
    };

    public static IJobDetail ToQuartzJob(NCacheJobData data)
    {
        var jobType = ResolveJobType(data.JobClassName);

        return JobBuilder.Create()
            .OfType(jobType)
            .WithIdentity(data.JobName, data.JobGroup)
            .WithDescription(data.Description)
            .StoreDurably(data.IsDurable)
            .RequestRecovery(data.RequestsRecovery)
            .DisallowConcurrentExecution(data.IsNonConcurrent)
            .PersistJobDataAfterExecution(data.IsUpdateData)
            .UsingJobData(new JobDataMap(JobDataMapMapper.ToJobDataMap(data.JobDataMap)))
            .Build();
    }

    private static Type ResolveJobType(string jobClassName)
    {
        var type = Type.GetType(jobClassName, throwOnError: false);
        if (type is not null)
            return type;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(jobClassName, throwOnError: false);
            if (type is not null)
                return type;
        }

        throw new JobPersistenceException(
            $"Could not load job class '{jobClassName}'. " +
            "It may not be loaded into the AppDomain, or the stored name is not assembly-qualified.");
    }
}
