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
public sealed class NCacheQuartzPauseResumeUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task PauseTrigger_waiting_becomes_paused()
    {
        var store = CreateStore($"h01-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));

        await StoreJobAndTriggerAsync(job, trigger);

        Assert.True(await store.PauseTrigger(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup)));

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
        Assert.Equal(TriggerStates.Paused, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseTrigger_blocked_becomes_paused_and_blocked()
    {
        var store = CreateStore($"h02-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}", nonConcurrent: true);
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        trigger.TriggerState = TriggerStates.Blocked;

        await StoreJobAndTriggerAsync(job, trigger);

        Assert.True(await store.PauseTrigger(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup)));

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
        Assert.Equal(TriggerStates.PausedAndBlocked, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseTrigger_paused_is_idempotent()
    {
        var store = CreateStore($"h03-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        trigger.TriggerState = TriggerStates.Paused;

        await StoreJobAndTriggerAsync(job, trigger);

        Assert.False(await store.PauseTrigger(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup)));

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
        Assert.Equal(TriggerStates.Paused, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseTrigger_paused_and_blocked_is_idempotent()
    {
        var store = CreateStore($"h04-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}", nonConcurrent: true);
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        trigger.TriggerState = TriggerStates.PausedAndBlocked;

        await StoreJobAndTriggerAsync(job, trigger);

        Assert.False(await store.PauseTrigger(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup)));

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
        Assert.Equal(TriggerStates.PausedAndBlocked, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseTrigger_acquired_is_paused()
    {
        var store = CreateStore($"h05-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateAcquiredTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        Assert.True(await store.PauseTrigger(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup)));

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
        Assert.Equal(TriggerStates.Paused, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseTrigger_complete_is_unchanged()
    {
        var store = CreateStore($"h06-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        trigger.TriggerState = TriggerStates.Complete;
        trigger.NextFireTimeUtc = null;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        var result = await store.PauseTrigger(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup));

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);

        Assert.False(result);
        Assert.Equal(TriggerStates.Complete, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseTrigger_error_returns_false()
    {
        var store = CreateStore($"h07-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        trigger.TriggerState = TriggerStates.Error;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        var result = await store.PauseTrigger(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup));

        Assert.False(result);

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
        Assert.Equal(TriggerStates.Error, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseTrigger_missing_returns_false()
    {
        var store = CreateStore($"h08-{Guid.NewGuid():N}");

        var result = await store.PauseTrigger(new TriggerKey($"missing-{Guid.NewGuid():N}", "tests"));

        Assert.False(result);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseTrigger_updates_index()
    {
        var store = CreateStore($"h09-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddSeconds(30));

        await StoreJobAndTriggerAsync(job, trigger);

        Assert.Contains(await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(2), 10), x => x.TriggerName == trigger.TriggerName);

        await store.PauseTrigger(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup));

        var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(2), 10);

        Assert.DoesNotContain(candidates, x => x.TriggerName == trigger.TriggerName);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseTriggers_pauses_waiting_triggers()
    {
        var store = CreateStore($"h10-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger1 = CreateWaitingTrigger($"trigger1-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        var trigger2 = CreateWaitingTrigger($"trigger2-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));

        await StoreJobAndTriggerAsync(job, trigger1);
        await Storage.StoreTriggerAsync(trigger2, false);
        await Index.UpsertAsync(trigger2);

        var keys = new[]
        {
        new TriggerKey(trigger1.TriggerName, trigger1.TriggerGroup),
        new TriggerKey(trigger2.TriggerName, trigger2.TriggerGroup)
    };

        var paused = await store.PauseTriggers(keys);

        Assert.Equal(2, paused.Count);

        Assert.Equal(TriggerStates.Paused, (await Storage.GetTriggerAsync(trigger1.TriggerGroup, trigger1.TriggerName))!.TriggerState);
        Assert.Equal(TriggerStates.Paused, (await Storage.GetTriggerAsync(trigger2.TriggerGroup, trigger2.TriggerName))!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseTriggers_pauses_blocked_triggers()
    {
        var store = CreateStore($"h11-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}", nonConcurrent: true);
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        trigger.TriggerState = TriggerStates.Blocked;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        var paused = await store.PauseTriggers(new[] { new TriggerKey(trigger.TriggerName, trigger.TriggerGroup) });

        Assert.Single(paused);
        Assert.Equal(TriggerStates.PausedAndBlocked, (await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName))!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseTriggers_skips_missing_triggers()
    {
        var store = CreateStore($"h12-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));

        await StoreJobAndTriggerAsync(job, trigger);

        var existingKey = new TriggerKey(trigger.TriggerName, trigger.TriggerGroup);
        var missingKey = new TriggerKey($"missing-{Guid.NewGuid():N}", "tests");

        var paused = await store.PauseTriggers(new[] { existingKey, missingKey });

        Assert.Single(paused);
        Assert.Equal(existingKey, paused[0]);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseTriggerGroups_registers_paused_group()
    {
        var store = CreateStore($"h13-{Guid.NewGuid():N}");
        var group = $"pause-group-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}", group: group);
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group, jobGroup: group);

        await StoreJobAndTriggerAsync(job, trigger);

        var pausedGroups = await store.PauseTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals(group));

        Assert.Contains(group, pausedGroups);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseTriggerGroups_pauses_matching_group_triggers()
    {
        var store = CreateStore($"h14-{Guid.NewGuid():N}");
        var group = $"pause-group-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}", group: group);
        var trigger1 = CreateWaitingTrigger($"trigger1-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group, jobGroup: group);
        var trigger2 = CreateWaitingTrigger($"trigger2-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group, jobGroup: group);

        await StoreJobAndTriggerAsync(job, trigger1);
        await Storage.StoreTriggerAsync(trigger2, false);
        await Index.UpsertAsync(trigger2);

        var pausedGroups = await store.PauseTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals(group));

        Assert.Contains(group, pausedGroups);
        Assert.Equal(TriggerStates.Paused, (await Storage.GetTriggerAsync(trigger1.TriggerGroup, trigger1.TriggerName))!.TriggerState);
        Assert.Equal(TriggerStates.Paused, (await Storage.GetTriggerAsync(trigger2.TriggerGroup, trigger2.TriggerName))!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseTriggerGroups_does_not_pause_non_matching_groups()
    {
        var store = CreateStore($"h15-{Guid.NewGuid():N}");
        var matchingGroup = $"matching-{Guid.NewGuid():N}";
        var otherGroup = $"other-{Guid.NewGuid():N}";

        var matchingJob = CreateJob($"job1-{Guid.NewGuid():N}", group: matchingGroup);
        var otherJob = CreateJob($"job2-{Guid.NewGuid():N}", group: otherGroup);

        var matchingTrigger = CreateWaitingTrigger($"trigger1-{Guid.NewGuid():N}", matchingJob.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: matchingGroup, jobGroup: matchingGroup);
        var otherTrigger = CreateWaitingTrigger($"trigger2-{Guid.NewGuid():N}", otherJob.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: otherGroup, jobGroup: otherGroup);

        await StoreJobAndTriggerAsync(matchingJob, matchingTrigger);
        await StoreJobAndTriggerAsync(otherJob, otherTrigger);

        await store.PauseTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals(matchingGroup));

        Assert.Equal(TriggerStates.Paused, (await Storage.GetTriggerAsync(matchingTrigger.TriggerGroup, matchingTrigger.TriggerName))!.TriggerState);
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync(otherTrigger.TriggerGroup, otherTrigger.TriggerName))!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseJob_pauses_job_triggers()
    {
        var store = CreateStore($"h16-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger1 = CreateWaitingTrigger($"trigger1-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        var trigger2 = CreateWaitingTrigger($"trigger2-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));

        await StoreJobAndTriggerAsync(job, trigger1);
        await Storage.StoreTriggerAsync(trigger2, false);
        await Index.UpsertAsync(trigger2);

        Assert.True(await store.PauseJob(new JobKey(job.JobName, job.JobGroup)));

        Assert.Equal(TriggerStates.Paused, (await Storage.GetTriggerAsync(trigger1.TriggerGroup, trigger1.TriggerName))!.TriggerState);
        Assert.Equal(TriggerStates.Paused, (await Storage.GetTriggerAsync(trigger2.TriggerGroup, trigger2.TriggerName))!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseJob_returns_false_for_missing_job()
    {
        var store = CreateStore($"h17-{Guid.NewGuid():N}");

        var result = await store.PauseJob(new JobKey($"missing-{Guid.NewGuid():N}", "tests"));

        Assert.False(result);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseJobs_pauses_existing_jobs()
    {
        var store = CreateStore($"h18-{Guid.NewGuid():N}");
        var job1 = CreateJob($"job1-{Guid.NewGuid():N}");
        var job2 = CreateJob($"job2-{Guid.NewGuid():N}");

        var trigger1 = CreateWaitingTrigger($"trigger1-{Guid.NewGuid():N}", job1.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        var trigger2 = CreateWaitingTrigger($"trigger2-{Guid.NewGuid():N}", job2.JobName, DateTimeOffset.UtcNow.AddMinutes(10));

        await StoreJobAndTriggerAsync(job1, trigger1);
        await StoreJobAndTriggerAsync(job2, trigger2);

        var paused = await store.PauseJobs(new[]
        {
        new JobKey(job1.JobName, job1.JobGroup),
        new JobKey(job2.JobName, job2.JobGroup)
    });

        Assert.Equal(2, paused.Count);
        Assert.Equal(TriggerStates.Paused, (await Storage.GetTriggerAsync(trigger1.TriggerGroup, trigger1.TriggerName))!.TriggerState);
        Assert.Equal(TriggerStates.Paused, (await Storage.GetTriggerAsync(trigger2.TriggerGroup, trigger2.TriggerName))!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseJobs_skips_missing_jobs()
    {
        var store = CreateStore($"h19-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));

        await StoreJobAndTriggerAsync(job, trigger);

        var existingKey = new JobKey(job.JobName, job.JobGroup);
        var missingKey = new JobKey($"missing-{Guid.NewGuid():N}", "tests");

        var paused = await store.PauseJobs(new[] { existingKey, missingKey });

        Assert.Single(paused);
        Assert.Equal(existingKey, paused[0]);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseJobGroups_registers_paused_job_group()
    {
        var store = CreateStore($"h20-{Guid.NewGuid():N}");
        var group = $"job-group-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}", group: group);
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group, jobGroup: group);

        await StoreJobAndTriggerAsync(job, trigger);

        var pausedGroups = await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals(group));

        Assert.Contains(group, pausedGroups);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseJobGroups_pauses_group_job_triggers()
    {
        var store = CreateStore($"h21-{Guid.NewGuid():N}");
        var group = $"job-group-{Guid.NewGuid():N}";
        var job1 = CreateJob($"job1-{Guid.NewGuid():N}", group: group);
        var job2 = CreateJob($"job2-{Guid.NewGuid():N}", group: group);

        var trigger1 = CreateWaitingTrigger($"trigger1-{Guid.NewGuid():N}", job1.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group, jobGroup: group);
        var trigger2 = CreateWaitingTrigger($"trigger2-{Guid.NewGuid():N}", job2.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group, jobGroup: group);

        await StoreJobAndTriggerAsync(job1, trigger1);
        await StoreJobAndTriggerAsync(job2, trigger2);

        var pausedGroups = await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals(group));

        Assert.Contains(group, pausedGroups);
        Assert.Equal(TriggerStates.Paused, (await Storage.GetTriggerAsync(trigger1.TriggerGroup, trigger1.TriggerName))!.TriggerState);
        Assert.Equal(TriggerStates.Paused, (await Storage.GetTriggerAsync(trigger2.TriggerGroup, trigger2.TriggerName))!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseJobGroups_does_not_pause_non_matching_groups()
    {
        var store = CreateStore($"h22-{Guid.NewGuid():N}");
        var matchingGroup = $"matching-{Guid.NewGuid():N}";
        var otherGroup = $"other-{Guid.NewGuid():N}";

        var matchingJob = CreateJob($"job1-{Guid.NewGuid():N}", group: matchingGroup);
        var otherJob = CreateJob($"job2-{Guid.NewGuid():N}", group: otherGroup);

        var matchingTrigger = CreateWaitingTrigger($"trigger1-{Guid.NewGuid():N}", matchingJob.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: matchingGroup, jobGroup: matchingGroup);
        var otherTrigger = CreateWaitingTrigger($"trigger2-{Guid.NewGuid():N}", otherJob.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: otherGroup, jobGroup: otherGroup);

        await StoreJobAndTriggerAsync(matchingJob, matchingTrigger);
        await StoreJobAndTriggerAsync(otherJob, otherTrigger);

        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals(matchingGroup));

        Assert.Equal(TriggerStates.Paused, (await Storage.GetTriggerAsync(matchingTrigger.TriggerGroup, matchingTrigger.TriggerName))!.TriggerState);
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync(otherTrigger.TriggerGroup, otherTrigger.TriggerName))!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseAll_pauses_all_trigger_groups()
    {
        var store = CreateStore($"h23-{Guid.NewGuid():N}");
        var group1 = $"group1-{Guid.NewGuid():N}";
        var group2 = $"group2-{Guid.NewGuid():N}";

        var job1 = CreateJob($"job1-{Guid.NewGuid():N}", group: group1);
        var job2 = CreateJob($"job2-{Guid.NewGuid():N}", group: group2);

        var trigger1 = CreateWaitingTrigger($"trigger1-{Guid.NewGuid():N}", job1.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group1, jobGroup: group1);
        var trigger2 = CreateWaitingTrigger($"trigger2-{Guid.NewGuid():N}", job2.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group2, jobGroup: group2);

        await StoreJobAndTriggerAsync(job1, trigger1);
        await StoreJobAndTriggerAsync(job2, trigger2);

        await store.PauseAll();

        Assert.Equal(TriggerStates.Paused, (await Storage.GetTriggerAsync(trigger1.TriggerGroup, trigger1.TriggerName))!.TriggerState);
        Assert.Equal(TriggerStates.Paused, (await Storage.GetTriggerAsync(trigger2.TriggerGroup, trigger2.TriggerName))!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task PauseAll_registers_all_trigger_groups_as_paused()
    {
        var store = CreateStore($"h24-{Guid.NewGuid():N}");
        var group = $"pause-all-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}", group: group);
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group, jobGroup: group);

        await StoreJobAndTriggerAsync(job, trigger);

        await store.PauseAll();

        var newJob = CreateJob($"new-job-{Guid.NewGuid():N}", group: group);
        var newTrigger = CreateWaitingTrigger($"new-trigger-{Guid.NewGuid():N}", newJob.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group, jobGroup: group);

        await store.AddJob(Mapping.JobMapper.ToQuartzJob(newJob));
        await store.AddTrigger(ToQuartzTrigger(newTrigger));

        var stored = await Storage.GetTriggerAsync(newTrigger.TriggerGroup, newTrigger.TriggerName);

        Assert.Equal(TriggerStates.Paused, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeTrigger_paused_becomes_waiting()
    {
        var store = CreateStore($"h25-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        trigger.TriggerState = TriggerStates.Paused;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        Assert.True(await store.ResumeTrigger(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup)));

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
        Assert.Equal(TriggerStates.Waiting, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeTrigger_paused_and_blocked_becomes_blocked()
    {
        var store = CreateStore($"h26-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}", nonConcurrent: true);
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        trigger.TriggerState = TriggerStates.PausedAndBlocked;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        Assert.True(await store.ResumeTrigger(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup)));

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
        Assert.Equal(TriggerStates.Blocked, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeTrigger_waiting_is_idempotent()
    {
        var store = CreateStore($"h27-{Guid.NewGuid():N}"); var job = CreateJob($"job-{Guid.NewGuid():N}"); var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));

        await StoreJobAndTriggerAsync(job, trigger);

        Assert.False(await store.ResumeTrigger(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup)));

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
        Assert.Equal(TriggerStates.Waiting, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeTrigger_blocked_is_idempotent()
    {
        var store = CreateStore($"h28-{Guid.NewGuid():N}"); var job = CreateJob($"job-{Guid.NewGuid():N}", nonConcurrent: true); var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10)); trigger.TriggerState = TriggerStates.Blocked;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        Assert.False(await store.ResumeTrigger(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup)));

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
        Assert.Equal(TriggerStates.Blocked, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeTrigger_acquired_is_idempotent()
    {
        var store = CreateStore($"h29-{Guid.NewGuid():N}"); var job = CreateJob($"job-{Guid.NewGuid():N}"); var trigger = CreateAcquiredTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        Assert.False(await store.ResumeTrigger(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup)));

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
        Assert.Equal(TriggerStates.Acquired, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeTrigger_complete_is_idempotent()
    {
        var store = CreateStore($"h30-{Guid.NewGuid():N}"); var job = CreateJob($"job-{Guid.NewGuid():N}"); var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10)); trigger.TriggerState = TriggerStates.Complete; trigger.NextFireTimeUtc = null;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        Assert.False(await store.ResumeTrigger(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup)));

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
        Assert.Equal(TriggerStates.Complete, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeTrigger_error_is_idempotent()
    {
        var store = CreateStore($"h31-{Guid.NewGuid():N}"); var job = CreateJob($"job-{Guid.NewGuid():N}"); var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10)); trigger.TriggerState = TriggerStates.Error;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        Assert.False(await store.ResumeTrigger(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup)));

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
        Assert.Equal(TriggerStates.Error, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeTrigger_missing_returns_false()
    {
        var store = CreateStore($"h32-{Guid.NewGuid():N}");

        var result = await store.ResumeTrigger(new TriggerKey($"missing-{Guid.NewGuid():N}", "tests"));

        Assert.False(result);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeTrigger_applies_misfire_when_resuming()
    {
        var store = CreateStore($"h33-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(-10));
        trigger.TriggerState = TriggerStates.Paused;
        trigger.MisfireInstruction = 0;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        Assert.True(await store.ResumeTrigger(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup)));

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
        Assert.True(stored.NextFireTimeUtc >= DateTimeOffset.UtcNow.AddSeconds(-1));

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeTriggers_resumes_paused_triggers()
    {
        var store = CreateStore($"h34-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger1 = CreateWaitingTrigger($"trigger1-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        var trigger2 = CreateWaitingTrigger($"trigger2-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));

        trigger1.TriggerState = TriggerStates.Paused;
        trigger2.TriggerState = TriggerStates.Paused;

        await StoreJobAndTriggerAsync(job, trigger1, addToIndex: false);
        await Storage.StoreTriggerAsync(trigger2, false);

        var keys = new[]
        {
        new TriggerKey(trigger1.TriggerName, trigger1.TriggerGroup),
        new TriggerKey(trigger2.TriggerName, trigger2.TriggerGroup)
    };

        var resumed = await store.ResumeTriggers(keys);

        Assert.Equal(2, resumed.Count);
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync(trigger1.TriggerGroup, trigger1.TriggerName))!.TriggerState);
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync(trigger2.TriggerGroup, trigger2.TriggerName))!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeTriggers_resumes_paused_and_blocked_triggers()
    {
        var store = CreateStore($"h35-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}", nonConcurrent: true);
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        trigger.TriggerState = TriggerStates.PausedAndBlocked;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        var resumed = await store.ResumeTriggers(new[] { new TriggerKey(trigger.TriggerName, trigger.TriggerGroup) });

        Assert.Single(resumed);
        Assert.Equal(TriggerStates.Blocked, (await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName))!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeTriggers_skips_missing_triggers()
    {
        var store = CreateStore($"h36-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        trigger.TriggerState = TriggerStates.Paused;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        var existingKey = new TriggerKey(trigger.TriggerName, trigger.TriggerGroup);
        var missingKey = new TriggerKey($"missing-{Guid.NewGuid():N}", "tests");

        var resumed = await store.ResumeTriggers(new[] { existingKey, missingKey });

        Assert.Single(resumed);
        Assert.Equal(existingKey, resumed[0]);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeTriggers_applies_misfire_when_needed()
    {
        var store = CreateStore($"h37-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(-10));
        trigger.TriggerState = TriggerStates.Paused;
        trigger.MisfireInstruction = 0;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        await store.ResumeTriggers(new[] { new TriggerKey(trigger.TriggerName, trigger.TriggerGroup) });

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
        Assert.True(stored.NextFireTimeUtc >= DateTimeOffset.UtcNow.AddSeconds(-1));

        await store.Shutdown();
    }
    
    [Fact]
    public async Task ResumeTriggerGroups_resumes_matching_groups()
    {
        var store = CreateStore($"h38-{Guid.NewGuid():N}");
        var group = $"resume-group-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}", group: group);
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group, jobGroup: group);

        await StoreJobAndTriggerAsync(job, trigger);

        await store.PauseTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals(group));
        Assert.Contains(group, await Storage.GetPausedTriggerGroupsAsync());

        var groups = await store.ResumeTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals(group));

        Assert.Contains(group, groups);
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName))!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeTriggerGroups_removes_paused_group_registration()
    {
        var store = CreateStore($"h39-{Guid.NewGuid():N}");
        var group = $"resume-group-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}", group: group);
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group, jobGroup: group);

        await StoreJobAndTriggerAsync(job, trigger);
        await store.PauseTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals(group));
        await store.ResumeTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals(group));

        var newJob = CreateJob($"new-job-{Guid.NewGuid():N}", group: group);
        var newTrigger = CreateWaitingTrigger($"new-trigger-{Guid.NewGuid():N}", newJob.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group, jobGroup: group);

        await store.AddJob(Mapping.JobMapper.ToQuartzJob(newJob));
        await store.AddTrigger(ToQuartzTrigger(newTrigger));

        var stored = await Storage.GetTriggerAsync(newTrigger.TriggerGroup, newTrigger.TriggerName);

        Assert.Equal(TriggerStates.Waiting, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeTriggerGroups_leaves_non_matching_paused_groups()
    {
        var store = CreateStore($"h40-{Guid.NewGuid():N}");
        var pausedGroup = $"paused-{Guid.NewGuid():N}";
        var otherGroup = $"other-{Guid.NewGuid():N}";

        var pausedJob = CreateJob($"job1-{Guid.NewGuid():N}", group: pausedGroup);
        var otherJob = CreateJob($"job2-{Guid.NewGuid():N}", group: otherGroup);

        var pausedTrigger = CreateWaitingTrigger($"trigger1-{Guid.NewGuid():N}", pausedJob.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: pausedGroup, jobGroup: pausedGroup);
        var otherTrigger = CreateWaitingTrigger($"trigger2-{Guid.NewGuid():N}", otherJob.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: otherGroup, jobGroup: otherGroup);

        await StoreJobAndTriggerAsync(pausedJob, pausedTrigger);
        await StoreJobAndTriggerAsync(otherJob, otherTrigger);

        await store.PauseTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals(pausedGroup));

        await store.ResumeTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals(otherGroup));

        var newJob = CreateJob($"new-job-{Guid.NewGuid():N}", group: pausedGroup);
        var newTrigger = CreateWaitingTrigger($"new-trigger-{Guid.NewGuid():N}", newJob.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: pausedGroup, jobGroup: pausedGroup);

        await store.AddJob(Mapping.JobMapper.ToQuartzJob(newJob));
        await store.AddTrigger(ToQuartzTrigger(newTrigger));

        var stored = await Storage.GetTriggerAsync(newTrigger.TriggerGroup, newTrigger.TriggerName);

        Assert.Equal(TriggerStates.Paused, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeJob_resumes_job_triggers()
    {
        var store = CreateStore($"h41-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger1 = CreateWaitingTrigger($"trigger1-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        var trigger2 = CreateWaitingTrigger($"trigger2-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));

        trigger1.TriggerState = TriggerStates.Paused;
        trigger2.TriggerState = TriggerStates.Paused;

        await StoreJobAndTriggerAsync(job, trigger1, addToIndex: false);
        await Storage.StoreTriggerAsync(trigger2, false);

        Assert.True(await store.ResumeJob(new JobKey(job.JobName, job.JobGroup)));

        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync(trigger1.TriggerGroup, trigger1.TriggerName))!.TriggerState);
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync(trigger2.TriggerGroup, trigger2.TriggerName))!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeJob_returns_false_for_missing_job()
    {
        var store = CreateStore($"h42-{Guid.NewGuid():N}");

        var result = await store.ResumeJob(new JobKey($"missing-{Guid.NewGuid():N}", "tests"));

        Assert.False(result);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeJobs_resumes_existing_jobs()
    {
        var store = CreateStore($"h43-{Guid.NewGuid():N}");
        var job1 = CreateJob($"job1-{Guid.NewGuid():N}");
        var job2 = CreateJob($"job2-{Guid.NewGuid():N}");

        var trigger1 = CreateWaitingTrigger($"trigger1-{Guid.NewGuid():N}", job1.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        var trigger2 = CreateWaitingTrigger($"trigger2-{Guid.NewGuid():N}", job2.JobName, DateTimeOffset.UtcNow.AddMinutes(10));

        trigger1.TriggerState = TriggerStates.Paused;
        trigger2.TriggerState = TriggerStates.Paused;

        await StoreJobAndTriggerAsync(job1, trigger1, addToIndex: false);
        await StoreJobAndTriggerAsync(job2, trigger2, addToIndex: false);

        var resumed = await store.ResumeJobs(new[]
        {
        new JobKey(job1.JobName, job1.JobGroup),
        new JobKey(job2.JobName, job2.JobGroup)
    });

        Assert.Equal(2, resumed.Count);
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync(trigger1.TriggerGroup, trigger1.TriggerName))!.TriggerState);
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync(trigger2.TriggerGroup, trigger2.TriggerName))!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeJobs_skips_missing_jobs()
    {
        var store = CreateStore($"h44-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        trigger.TriggerState = TriggerStates.Paused;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        var existingKey = new JobKey(job.JobName, job.JobGroup);
        var missingKey = new JobKey($"missing-{Guid.NewGuid():N}", "tests");

        var resumed = await store.ResumeJobs(new[] { existingKey, missingKey });

        Assert.Single(resumed);
        Assert.Equal(existingKey, resumed[0]);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeJobGroups_resumes_group_job_triggers()
    {
        var store = CreateStore($"h45-{Guid.NewGuid():N}");
        var group = $"resume-job-group-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}", group: group);
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group, jobGroup: group);

        trigger.TriggerState = TriggerStates.Paused;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);
        await Storage.AddPausedJobGroupAsync(group);

        var groups = await store.ResumeJobGroups(GroupMatcher<JobKey>.GroupEquals(group));

        Assert.Contains(group, groups);
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName))!.TriggerState);
        Assert.DoesNotContain(group, await Storage.GetPausedJobGroupsAsync());

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeJobGroups_removes_paused_job_group_registration()
    {
        var store = CreateStore($"h46-{Guid.NewGuid():N}");
        var group = $"resume-job-group-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}", group: group);
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group, jobGroup: group);

        await StoreJobAndTriggerAsync(job, trigger);
        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals(group));
        await store.ResumeJobGroups(GroupMatcher<JobKey>.GroupEquals(group));

        var newJob = CreateJob($"new-job-{Guid.NewGuid():N}", group: group);
        var newTrigger = CreateWaitingTrigger($"new-trigger-{Guid.NewGuid():N}", newJob.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group, jobGroup: group);

        await store.AddJob(Mapping.JobMapper.ToQuartzJob(newJob));
        await store.AddTrigger(ToQuartzTrigger(newTrigger));

        var stored = await Storage.GetTriggerAsync(newTrigger.TriggerGroup, newTrigger.TriggerName);

        Assert.Equal(TriggerStates.Waiting, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeAll_resumes_all_paused_trigger_groups()
    {
        var store = CreateStore($"h47-{Guid.NewGuid():N}");
        var group1 = $"group1-{Guid.NewGuid():N}";
        var group2 = $"group2-{Guid.NewGuid():N}";

        var job1 = CreateJob($"job1-{Guid.NewGuid():N}", group: group1);
        var job2 = CreateJob($"job2-{Guid.NewGuid():N}", group: group2);

        var trigger1 = CreateWaitingTrigger($"trigger1-{Guid.NewGuid():N}", job1.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group1, jobGroup: group1);
        var trigger2 = CreateWaitingTrigger($"trigger2-{Guid.NewGuid():N}", job2.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group2, jobGroup: group2);

        await StoreJobAndTriggerAsync(job1, trigger1);
        await StoreJobAndTriggerAsync(job2, trigger2);

        await store.PauseAll();
        await store.ResumeAll();

        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync(trigger1.TriggerGroup, trigger1.TriggerName))!.TriggerState);
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync(trigger2.TriggerGroup, trigger2.TriggerName))!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeAll_removes_paused_trigger_group_registrations()
    {
        var store = CreateStore($"h48-{Guid.NewGuid():N}");
        var group = $"resume-all-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}", group: group);
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group, jobGroup: group);

        await StoreJobAndTriggerAsync(job, trigger);

        await store.PauseAll();
        await store.ResumeAll();

        var newJob = CreateJob($"new-job-{Guid.NewGuid():N}", group: group);
        var newTrigger = CreateWaitingTrigger($"new-trigger-{Guid.NewGuid():N}", newJob.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group, jobGroup: group);

        await store.AddJob(Mapping.JobMapper.ToQuartzJob(newJob));
        await store.AddTrigger(ToQuartzTrigger(newTrigger));

        var stored = await Storage.GetTriggerAsync(newTrigger.TriggerGroup, newTrigger.TriggerName);

        Assert.Equal(TriggerStates.Waiting, stored!.TriggerState);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeTrigger_updates_index()
    {
        var store = CreateStore($"h49-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(30));
        trigger.TriggerState = TriggerStates.Paused;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        var before = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddHours(1), 10);
        Assert.DoesNotContain(before, x => x.TriggerName == trigger.TriggerName);

        await store.ResumeTrigger(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup));

        var after = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddHours(1), 10);
        Assert.Contains(after, x => x.TriggerName == trigger.TriggerName);

        await store.Shutdown();
    }

    [Fact]
    public async Task ResumeAll_applies_misfire_to_resumed_triggers()
    {
        var store = CreateStore($"h50-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(-10));

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);
        await store.PauseAll();
        await store.ResumeAll();

        var stored = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
        Assert.True(stored.NextFireTimeUtc >= DateTimeOffset.UtcNow.AddSeconds(-1));

        await store.Shutdown();
    }
}

