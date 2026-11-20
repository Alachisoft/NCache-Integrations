using Quartz;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzResumeAllJobGroupsUnitTest : NCacheQuartzScenarioTestBase
{
    [Fact]
    public async Task B8_after_ResumeAll_a_trigger_added_for_a_job_in_a_previously_paused_job_group_starts_normal()
    {
        var store = CreateStore("node-1");
        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals("reports"));
        await store.ResumeAll();

        var job = Job("job-1", "reports");
        var trigger = OneShot("t", "triggers", job.Key, DateTimeOffset.UtcNow.AddHours(1));
        await store.ScheduleJob(job, trigger);

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));
        Assert.Empty(await Storage.GetPausedJobGroupsAsync());
    }

    [Fact]
    public async Task B8_after_ResumeAll_QueryJobGroups_reports_no_group_as_paused()
    {
        var store = CreateStore("node-1");
        await store.AddJob(Job("job-1", "reports"));
        await store.AddJob(Job("job-2", "billing"));
        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals("reports"));
        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals("billing"));

        Assert.All((await store.QueryJobGroups(new JobGroupQuery())).Items, g => Assert.True(g.Paused));

        await store.ResumeAll();

        var groups = (await store.QueryJobGroups(new JobGroupQuery())).Items;
        Assert.Equal(new[] { "billing", "reports" }, groups.Select(g => g.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(groups, g => Assert.False(g.Paused));
    }

    [Fact]
    public async Task B8_ResumeAll_resumes_the_existing_triggers_of_a_paused_job_group_and_they_fire()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1", "reports");
        var trigger = OneShot("t", "triggers", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals("reports"));
        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
        Assert.Empty(await AcquireDueAsync(store));

        await store.ResumeAll();

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));
        Assert.Equal(trigger.Key, Assert.Single(await AcquireDueAsync(store)).Key);
    }

    [Fact]
    public async Task B8_ResumeAll_after_PauseAll_and_PauseJobGroups_leaves_nothing_paused()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1", "reports");
        await store.ScheduleJob(job, OneShot("t", "triggers", job.Key, DateTimeOffset.UtcNow.AddHours(1)));

        await store.PauseAll();
        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals("reports"));
        await store.ResumeAll();

        var job2 = Job("job-2", "reports");
        var added = OneShot("t2", "other-triggers", job2.Key, DateTimeOffset.UtcNow.AddHours(1));
        await store.ScheduleJob(job2, added);

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(new TriggerKey("t", "triggers")));
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(added.Key));
        Assert.Empty(await Storage.GetPausedTriggerGroupsAsync());
        Assert.All((await store.QueryTriggerGroups(new TriggerGroupQuery())).Items, g => Assert.False(g.Paused));
        Assert.All((await store.QueryJobGroups(new JobGroupQuery())).Items, g => Assert.False(g.Paused));
    }

    [Fact]
    public async Task B8_without_a_resume_a_trigger_added_to_a_paused_job_group_starts_paused()
    {
        var store = CreateStore("node-1");
        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals("reports"));

        var job = Job("job-1", "reports");
        var trigger = OneShot("t", "triggers", job.Key, DateTimeOffset.UtcNow.AddHours(1));
        await store.ScheduleJob(job, trigger);

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
        Assert.Contains("reports", await Storage.GetPausedJobGroupsAsync());
    }

    [Fact]
    public async Task B8_ResumeJobGroups_still_resumes_only_the_group_it_names()
    {
        var store = CreateStore("node-1");
        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals("reports"));
        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals("billing"));

        await store.ResumeJobGroups(GroupMatcher<JobKey>.GroupEquals("reports"));

        var reports = Job("job-1", "reports");
        var billing = Job("job-2", "billing");
        await store.ScheduleJob(reports, OneShot("t1", "triggers", reports.Key, DateTimeOffset.UtcNow.AddHours(1)));
        await store.ScheduleJob(billing, OneShot("t2", "triggers", billing.Key, DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(new TriggerKey("t1", "triggers")));
        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(new TriggerKey("t2", "triggers")));
    }
}

