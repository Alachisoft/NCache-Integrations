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

public sealed class NCacheQuartzNonFiniteJobDataUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task N10_a_NaN_put_in_a_persisted_JobDataMap_does_not_leave_a_non_concurrent_job_blocked()
    {
        var log = new CapturingLoggerProvider();
        var store = await ScenarioKit.CreateStoreAsync(_cache!, _prefix, SchedulerName, "instance-1", log);
        var job = ScenarioKit.Job("j", "g", nonConcurrent: true, persistData: true, data: ("count", 1));
        var now = DateTimeOffset.UtcNow;
        var t2 = ScenarioKit.Repeating("t2", "g", job.Key, now.AddSeconds(2), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, ScenarioKit.Repeating("t1", "g", job.Key, now.AddSeconds(-1), TimeSpan.FromHours(1)));
        await store.AddTrigger(t2);
        var bundle = await ScenarioKit.FireOneAsync(store);
        Assert.Equal(TriggerState.Blocked, await store.GetTriggerState(t2.Key));

        bundle.JobDetail.JobDataMap["count"] = 2;
        bundle.JobDetail.JobDataMap["avg"] = double.NaN;

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(t2.Key));
        Assert.Empty(await Storage.GetFiredTriggersForJobAsync("g", "j"));

        var stored = await store.GetJob(job.Key);
        Assert.Equal(1, stored!.JobDataMap["count"]);
        Assert.False(stored.JobDataMap.ContainsKey("avg"));
        Assert.True(log.Contains("cannot be persisted; keeping the previously stored data"), string.Join(Environment.NewLine, log.Lines));
    }

    [Fact]
    public async Task N10_a_finite_value_changed_during_the_run_is_persisted()
    {
        var store = CreateStore("instance-1");
        var job = ScenarioKit.Job("j", "g", nonConcurrent: true, persistData: true, data: ("avg", 0.0));
        await store.ScheduleJob(job, ScenarioKit.Repeating("t", "g", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1)));
        var bundle = await ScenarioKit.FireOneAsync(store);

        bundle.JobDetail.JobDataMap["avg"] = 2.5;
        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.Equal(2.5, (await store.GetJob(job.Key))!.JobDataMap["avg"]);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task N10_AddJob_refuses_a_non_finite_double_as_a_JobPersistenceException_without_a_repair(double value)
    {
        var log = new CapturingLoggerProvider();
        var store = await ScenarioKit.CreateStoreAsync(_cache!, _prefix, SchedulerName, "instance-1", log);
        var job = ScenarioKit.Job("j", "g", data: ("value", value));

        var ex = await Assert.ThrowsAsync<JobPersistenceException>(async () => await store.AddJob(job));

        Assert.Contains("'value'", ex.Message);
        Assert.False(await store.Exists(job.Key));
        Assert.False(log.Contains("failed unexpectedly"), string.Join(Environment.NewLine, log.Lines));
    }

    [Fact]
    public void N10_only_finite_floating_point_values_are_supported()
    {
        Assert.False(JobDataMapValueConverter.IsBuiltIn(double.NaN));
        Assert.False(JobDataMapValueConverter.IsBuiltIn(double.PositiveInfinity));
        Assert.False(JobDataMapValueConverter.IsBuiltIn(float.NaN));
        Assert.False(JobDataMapValueConverter.IsBuiltIn(float.NegativeInfinity));

        Assert.True(JobDataMapValueConverter.IsBuiltIn(0.0));
        Assert.True(JobDataMapValueConverter.IsBuiltIn(double.MaxValue));
        Assert.True(JobDataMapValueConverter.IsBuiltIn(double.Epsilon));
        Assert.True(JobDataMapValueConverter.IsBuiltIn(-1.5f));
    }
}

