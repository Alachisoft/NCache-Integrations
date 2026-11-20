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
public sealed class NCacheQuartzJobCompletionUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task TriggeredJobComplete_removes_fired_trigger()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var quartzTrigger = await AcquireForTestAsync(storedTrigger);

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = quartzTrigger.FireInstanceId!,
            TriggerName = storedTrigger.TriggerName,
            TriggerGroup = storedTrigger.TriggerGroup,
            InstanceName = "instance-1",
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = storedTrigger.NextFireTimeUtc!.Value,
            Priority = storedTrigger.Priority,
            State = FiredTriggerStates.Executing,
            JobName = storedTrigger.JobName,
            JobGroup = storedTrigger.JobGroup,
            IsNonConcurrent = false,
            RequestsRecovery = false,
            ExecutionGroup = storedTrigger.ExecutionGroup
        });

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var fired = await Storage.GetFiredTriggerAsync(quartzTrigger.FireInstanceId!);

        Assert.Null(fired);
    }

    [Fact]
    public async Task TriggeredJobComplete_handles_trigger_without_fire_instance_id()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var quartzTrigger = ToQuartzTrigger(storedTrigger);
        quartzTrigger.FireInstanceId = null;

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
    }

    [Fact]
    public async Task TriggeredJobComplete_handles_missing_stored_job()
    {
        var store = CreateStore("instance-1");

        var storedTrigger = CreateAcquiredTrigger("trigger-1", "missing-job", DateTimeOffset.UtcNow.AddSeconds(-1));
        await Storage.StoreTriggerAsync(storedTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(storedTrigger);
        quartzTrigger.FireInstanceId = null;

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("missing-job", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
    }

    [Fact]
    public async Task TriggeredJobComplete_handles_missing_stored_trigger()
    {
        var store = CreateStore("instance-1");

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        var trigger = CreateQuartzSimpleTrigger("trigger-1", "tests", jobDetail.Key, DateTimeOffset.UtcNow);

        await store.TriggeredJobComplete(trigger, jobDetail, SchedulerInstruction.NoInstruction);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.Null(stored);
    }

    [Fact]
    public async Task TriggeredJobComplete_persists_job_data_when_requested()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", persistData: true);
        job.JobDataMap["counter"] = 1;

        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var quartzTrigger = ToQuartzTrigger(storedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .UsingJobData("counter", 2)
            .PersistJobDataAfterExecution()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var storedJob = await Storage.GetJobAsync("tests", "job-1");

        Assert.NotNull(storedJob);
        Assert.Equal(2, storedJob.JobDataMap["counter"]);
    }

    [Fact]
    public async Task TriggeredJobComplete_does_not_persist_job_data_when_not_requested()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", persistData: false);
        job.JobDataMap["counter"] = 1;

        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var quartzTrigger = ToQuartzTrigger(storedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .UsingJobData("counter", 2)
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var storedJob = await Storage.GetJobAsync("tests", "job-1");

        Assert.NotNull(storedJob);
        Assert.Equal(1, storedJob.JobDataMap["counter"]);
    }

    [Fact]
    public async Task TriggeredJobComplete_persists_updated_job_data_map()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", persistData: true);
        job.JobDataMap["original"] = "value";

        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var quartzTrigger = ToQuartzTrigger(storedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .PersistJobDataAfterExecution()
            .UsingJobData("updated", "value")
            .UsingJobData("count", 10)
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var storedJob = await Storage.GetJobAsync("tests", "job-1");

        Assert.NotNull(storedJob);
        Assert.Equal("value", storedJob.JobDataMap["updated"]);
        Assert.Equal(10, storedJob.JobDataMap["count"]);
        Assert.DoesNotContain("original", storedJob.JobDataMap.Keys);
    }

    [Fact]
    public async Task TriggeredJobComplete_preserves_existing_job_fields_when_persisting_job_data()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", persistData: true, nonConcurrent: true, requestsRecovery: true);
        job.Description = "Original description";

        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var quartzTrigger = ToQuartzTrigger(storedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .WithDescription("Quartz description")
            .StoreDurably()
            .RequestRecovery()
            .PersistJobDataAfterExecution()
            .UsingJobData("updated", "value")
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var storedJob = await Storage.GetJobAsync("tests", "job-1");

        Assert.NotNull(storedJob);
        Assert.Equal("Original description", storedJob.Description);
        Assert.True(storedJob.IsNonConcurrent);
        Assert.True(storedJob.RequestsRecovery);
        Assert.Equal("value", storedJob.JobDataMap["updated"]);
    }

    [Fact]
    public async Task TriggeredJobComplete_unblocks_sibling_triggers_for_non_concurrent_job()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);

        var completedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));

        siblingTrigger.TriggerState = TriggerStates.Blocked;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);
        await Index.UpsertAsync(siblingTrigger);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var storedSibling = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(storedSibling);
        Assert.Equal(TriggerStates.Waiting, storedSibling.TriggerState);
    }

    [Fact]
    public async Task TriggeredJobComplete_changes_blocked_sibling_to_waiting()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);

        var completedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));

        siblingTrigger.TriggerState = TriggerStates.Blocked;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var sibling = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(sibling);
        Assert.Equal(TriggerStates.Waiting, sibling.TriggerState);
    }

    [Fact]
    public async Task TriggeredJobComplete_changes_paused_and_blocked_sibling_to_paused()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);

        var completedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var siblingTrigger = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));

        siblingTrigger.TriggerState = TriggerStates.PausedAndBlocked;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(siblingTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var sibling = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(sibling);
        Assert.Equal(TriggerStates.Paused, sibling.TriggerState);
    }

    [Fact]
    public async Task TriggeredJobComplete_does_not_unblock_unrelated_job()
    {
        var store = CreateStore("instance-1");

        var job1 = CreateJob("job-1", nonConcurrent: true);
        var job2 = CreateJob("job-2");

        var completedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var unrelatedTrigger = CreateWaitingTrigger("trigger-2", "job-2", DateTimeOffset.UtcNow.AddMinutes(1));

        unrelatedTrigger.TriggerState = TriggerStates.Blocked;

        await Storage.StoreJobAsync(job1, replaceExisting: false);
        await Storage.StoreJobAsync(job2, replaceExisting: false);
        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);
        await Storage.StoreTriggerAsync(unrelatedTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var unrelated = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(unrelated);
        Assert.Equal(TriggerStates.Blocked, unrelated.TriggerState);
    }

    [Fact]
    public async Task TriggeredJobComplete_does_not_modify_completed_trigger_as_sibling()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);

        var completedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        completedTrigger.TriggerState = TriggerStates.Complete;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Complete, stored.TriggerState);
    }

    [Fact]
    public async Task TriggeredJobComplete_NoInstruction_leaves_trigger_state_unchanged()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var quartzTrigger = ToQuartzTrigger(storedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.NoInstruction);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Acquired, stored.TriggerState);
    }

    [Fact]
    public async Task TriggeredJobComplete_DeleteTrigger_removes_trigger()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", durable: true);
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var quartzTrigger = ToQuartzTrigger(storedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.DeleteTrigger);

        Assert.False(await Storage.TriggerExistsAsync("tests", "trigger-1"));
    }

    [Fact]
    public async Task TriggeredJobComplete_DeleteTrigger_removes_index_entry()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", durable: true);
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(storedTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(storedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.DeleteTrigger);

        var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10);

        Assert.DoesNotContain(candidates, x => x.TriggerGroup == "tests" && x.TriggerName == "trigger-1");
    }

    [Fact]
    public async Task TriggeredJobComplete_DeleteTrigger_removes_non_durable_orphan_job()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", durable: false);
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(storedTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(storedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.DeleteTrigger);

        Assert.False(await Storage.TriggerExistsAsync("tests", "trigger-1"));
        Assert.False(await Storage.JobExistsAsync("tests", "job-1"));
    }

    [Fact]
    public async Task TriggeredJobComplete_DeleteTrigger_preserves_durable_job()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", durable: true);
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(storedTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(storedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.DeleteTrigger);

        Assert.False(await Storage.TriggerExistsAsync("tests", "trigger-1"));
        Assert.True(await Storage.JobExistsAsync("tests", "job-1"));
    }

    [Fact]
    public async Task TriggeredJobComplete_SetTriggerComplete_sets_complete_state()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var quartzTrigger = ToQuartzTrigger(storedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.SetTriggerComplete);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Complete, stored.TriggerState);
    }

    [Fact]
    public async Task TriggeredJobComplete_SetTriggerComplete_removes_trigger_from_due_index()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        storedTrigger.TriggerState = TriggerStates.Acquired;

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(storedTrigger, replaceExisting: false);
        await Index.UpsertAsync(storedTrigger);

        var quartzTrigger = ToQuartzTrigger(storedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.SetTriggerComplete);

        var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10);

        Assert.DoesNotContain(candidates, x => x.TriggerGroup == "tests" && x.TriggerName == "trigger-1");
    }

    [Fact]
    public async Task TriggeredJobComplete_SetTriggerError_sets_error_state()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var quartzTrigger = ToQuartzTrigger(storedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.SetTriggerError);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Error, stored.TriggerState);
    }

    [Fact]
    public async Task TriggeredJobComplete_SetTriggerError_removes_trigger_from_due_index()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(storedTrigger, replaceExisting: false);
        await Index.UpsertAsync(storedTrigger);

        var quartzTrigger = ToQuartzTrigger(storedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.SetTriggerError);

        var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10);

        Assert.DoesNotContain(candidates, x => x.TriggerGroup == "tests" && x.TriggerName == "trigger-1");
    }

    [Fact]
    public async Task TriggeredJobComplete_SetAllJobTriggersError_sets_all_job_triggers_to_error()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");

        var trigger1 = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));
        var trigger2 = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(2));

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(trigger1, replaceExisting: false);
        await Storage.StoreTriggerAsync(trigger2, replaceExisting: false);
        await Index.UpsertAsync(trigger1);
        await Index.UpsertAsync(trigger2);

        var completedTrigger = CreateAcquiredTrigger("completed-trigger", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        completedTrigger.TriggerState = TriggerStates.Complete;

        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.SetAllJobTriggersError);

        var stored1 = await Storage.GetTriggerAsync("tests", "trigger-1");
        var stored2 = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored1);
        Assert.NotNull(stored2);
        Assert.Equal(TriggerStates.Error, stored1.TriggerState);
        Assert.Equal(TriggerStates.Error, stored2.TriggerState);
    }

    [Fact]
    public async Task TriggeredJobComplete_SetAllJobTriggersError_does_not_modify_other_jobs()
    {
        var store = CreateStore("instance-1");

        var job1 = CreateJob("job-1");
        var job2 = CreateJob("job-2");

        var trigger1 = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));
        var trigger2 = CreateWaitingTrigger("trigger-2", "job-2", DateTimeOffset.UtcNow.AddMinutes(2));

        await Storage.StoreJobAsync(job1, replaceExisting: false);
        await Storage.StoreJobAsync(job2, replaceExisting: false);
        await Storage.StoreTriggerAsync(trigger1, replaceExisting: false);
        await Storage.StoreTriggerAsync(trigger2, replaceExisting: false);

        var completedTrigger = CreateAcquiredTrigger("completed-trigger", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.SetAllJobTriggersError);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
    }

    [Fact]
    public async Task TriggeredJobComplete_SetAllJobTriggersComplete_sets_all_job_triggers_to_complete()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");

        var trigger1 = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));
        var trigger2 = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(2));

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Storage.StoreTriggerAsync(trigger1, replaceExisting: false);
        await Storage.StoreTriggerAsync(trigger2, replaceExisting: false);
        await Index.UpsertAsync(trigger1);
        await Index.UpsertAsync(trigger2);

        var completedTrigger = CreateAcquiredTrigger("completed-trigger", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        completedTrigger.TriggerState = TriggerStates.Complete;

        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.SetAllJobTriggersComplete);

        var stored1 = await Storage.GetTriggerAsync("tests", "trigger-1");
        var stored2 = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored1);
        Assert.NotNull(stored2);
        Assert.Equal(TriggerStates.Complete, stored1.TriggerState);
        Assert.Equal(TriggerStates.Complete, stored2.TriggerState);
    }

    [Fact]
    public async Task TriggeredJobComplete_SetAllJobTriggersComplete_does_not_modify_other_jobs()
    {
        var store = CreateStore("instance-1");

        var job1 = CreateJob("job-1");
        var job2 = CreateJob("job-2");

        var trigger1 = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));
        var trigger2 = CreateWaitingTrigger("trigger-2", "job-2", DateTimeOffset.UtcNow.AddMinutes(2));

        await Storage.StoreJobAsync(job1, replaceExisting: false);
        await Storage.StoreJobAsync(job2, replaceExisting: false);
        await Storage.StoreTriggerAsync(trigger1, replaceExisting: false);
        await Storage.StoreTriggerAsync(trigger2, replaceExisting: false);

        var completedTrigger = CreateAcquiredTrigger("completed-trigger", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.SetAllJobTriggersComplete);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
    }

    [Fact]
    public async Task TriggeredJobComplete_ReExecuteJob_leaves_trigger_state_unchanged()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var quartzTrigger = ToQuartzTrigger(storedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.ReExecuteJob);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Acquired, stored.TriggerState);
    }

    [Fact]
    public async Task TriggeredJobComplete_throws_for_unknown_scheduler_instruction()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var quartzTrigger = ToQuartzTrigger(storedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        var unknownInstruction = (SchedulerInstruction)999;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.TriggeredJobComplete(quartzTrigger, jobDetail, unknownInstruction));
    }

    [Fact]
    public async Task TriggeredJobComplete_set_all_job_triggers_error_removes_due_index_entries()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");

        var trigger1 = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));
        var trigger2 = CreateWaitingTrigger("trigger-2", "job-1", DateTimeOffset.UtcNow.AddMinutes(2));
        var completedTrigger = CreateAcquiredTrigger("completed-trigger", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await Storage.StoreJobAsync(job, replaceExisting: false);
        await Index.UpsertManyAsync(new[] { trigger1, trigger2 });
        await Storage.StoreTriggerAsync(trigger1, replaceExisting: false);
        await Storage.StoreTriggerAsync(trigger2, replaceExisting: false);
        await Storage.StoreTriggerAsync(completedTrigger, replaceExisting: false);

        var quartzTrigger = ToQuartzTrigger(completedTrigger);

        var jobDetail = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.TriggeredJobComplete(quartzTrigger, jobDetail, SchedulerInstruction.SetAllJobTriggersError);

        var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(10), 10);

        Assert.DoesNotContain(candidates, x => x.TriggerName == "trigger-1");
        Assert.DoesNotContain(candidates, x => x.TriggerName == "trigger-2");
    }
}

