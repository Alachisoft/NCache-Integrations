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
public sealed class NCacheQuartzQueryUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task QueryJobs_returns_matching_jobs()
    {
        var store = CreateStore($"i01-{Guid.NewGuid():N}");
        var job1 = CreateJob($"job1-{Guid.NewGuid():N}");
        var job2 = CreateJob($"job2-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(job1, false);
        await Storage.StoreJobAsync(job2, false);

        var result = await store.QueryJobs(new JobQuery { Take = 100 });

        Assert.Contains(result.Items, x => x.Key == new JobKey(job1.JobName, job1.JobGroup));
        Assert.Contains(result.Items, x => x.Key == new JobKey(job2.JobName, job2.JobGroup));

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryJobs_filters_by_group()
    {
        var store = CreateStore($"i02-{Guid.NewGuid():N}");
        var group1 = $"group1-{Guid.NewGuid():N}";
        var group2 = $"group2-{Guid.NewGuid():N}";

        var job1 = CreateJob($"job1-{Guid.NewGuid():N}", group1);
        var job2 = CreateJob($"job2-{Guid.NewGuid():N}", group2);

        await Storage.StoreJobAsync(job1, false);
        await Storage.StoreJobAsync(job2, false);

        var result = await store.QueryJobs(new JobQuery
        {
            Group = GroupMatcher<JobKey>.GroupEquals(group1),
            Take = 100
        });

        Assert.Contains(result.Items, x => x.Key == new JobKey(job1.JobName, group1));
        Assert.DoesNotContain(result.Items, x => x.Key == new JobKey(job2.JobName, group2));

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryJobs_filters_by_name()
    {
        var store = CreateStore($"i03-{Guid.NewGuid():N}");
        var matchingName = $"target-{Guid.NewGuid():N}";
        var otherName = $"other-{Guid.NewGuid():N}";

        var job1 = CreateJob(matchingName);
        var job2 = CreateJob(otherName);

        await Storage.StoreJobAsync(job1, false);
        await Storage.StoreJobAsync(job2, false);

        var result = await store.QueryJobs(new JobQuery
        {
            Name = NameMatcher<JobKey>.NameEquals(matchingName),
            Take = 100
        });

        Assert.Single(result.Items);
        Assert.Equal(matchingName, result.Items[0].Key.Name);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryJobs_returns_sorted_results()
    {
        var store = CreateStore($"i04-{Guid.NewGuid():N}");

        var jobB = CreateJob("b");
        var jobA = CreateJob("a");
        var jobZ = CreateJob("z");

        await Storage.StoreJobAsync(jobB, false);
        await Storage.StoreJobAsync(jobZ, false);
        await Storage.StoreJobAsync(jobA, false);

        var result = await store.QueryJobs(new JobQuery { Take = 100 });

        var names = result.Items.Select(x => x.Key.Name).ToArray();

        Assert.Equal(names.OrderBy(x => x, StringComparer.Ordinal).ToArray(), names);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryJobs_skips_jobs_with_missing_headers()
    {
        var store = CreateStore($"i05-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(job, false);

        _cache!.Remove(NCacheKeys.JobHeader(_prefix, SchedulerName, job.JobGroup, job.JobName));

        var result = await store.QueryJobs(new JobQuery { Take = 100 });

        Assert.DoesNotContain(result.Items, x => x.Key == new JobKey(job.JobName, job.JobGroup));

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryJobs_respects_skip()
    {
        var store = CreateStore($"i06-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(CreateJob("a"), false);
        await Storage.StoreJobAsync(CreateJob("b"), false);
        await Storage.StoreJobAsync(CreateJob("c"), false);

        var result = await store.QueryJobs(new JobQuery
        {
            Skip = 1,
            Take = 100
        });

        Assert.Equal(2, result.Items.Count);
        Assert.DoesNotContain(result.Items, x => x.Key.Name == "a");

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryJobs_respects_take()
    {
        var store = CreateStore($"i07-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(CreateJob("a"), false);
        await Storage.StoreJobAsync(CreateJob("b"), false);
        await Storage.StoreJobAsync(CreateJob("c"), false);

        var result = await store.QueryJobs(new JobQuery
        {
            Take = 2
        });

        Assert.Equal(2, result.Items.Count);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryJobs_reports_has_more()
    {
        var store = CreateStore($"i08-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(CreateJob("a"), false);
        await Storage.StoreJobAsync(CreateJob("b"), false);
        await Storage.StoreJobAsync(CreateJob("c"), false);

        var result = await store.QueryJobs(new JobQuery { Take = 2 });

        Assert.True(result.HasMore);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryJobs_returns_total_count_when_requested()
    {
        var store = CreateStore($"i09-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(CreateJob("a"), false);
        await Storage.StoreJobAsync(CreateJob("b"), false);
        await Storage.StoreJobAsync(CreateJob("c"), false);

        var result = await store.QueryJobs(new JobQuery
        {
            Take = 1,
            IncludeTotalCount = true
        });

        Assert.Equal(3, result.TotalCount);
        Assert.Single(result.Items);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryJobs_does_not_return_total_count_when_not_requested()
    {
        var store = CreateStore($"i10-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(CreateJob("a"), false);
        await Storage.StoreJobAsync(CreateJob("b"), false);

        var result = await store.QueryJobs(new JobQuery { Take = 1 });

        Assert.Null(result.TotalCount);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryJobs_take_zero_returns_no_items()
    {
        var store = CreateStore($"i11-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(CreateJob("a"), false);
        await Storage.StoreJobAsync(CreateJob("b"), false);

        var result = await store.QueryJobs(new JobQuery
        {
            Take = 0
        });

        Assert.Empty(result.Items);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryJobs_skip_beyond_total_returns_no_items()
    {
        var store = CreateStore($"i12-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(CreateJob("a"), false);
        await Storage.StoreJobAsync(CreateJob("b"), false);

        var result = await store.QueryJobs(new JobQuery
        {
            Skip = 10,
            Take = 10
        });

        Assert.Empty(result.Items);
        Assert.False(result.HasMore);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryTriggers_returns_matching_triggers()
    {
        var store = CreateStore($"i13-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger1 = CreateWaitingTrigger("trigger1", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        var trigger2 = CreateWaitingTrigger("trigger2", job.JobName, DateTimeOffset.UtcNow.AddMinutes(20));

        await StoreJobAndTriggerAsync(job, trigger1);
        await Storage.StoreTriggerAsync(trigger2, false);
        await Index.UpsertAsync(trigger2);

        var result = await store.QueryTriggers(new TriggerQuery { Take = 100 });

        Assert.Contains(result.Items, x => x.Key == new TriggerKey(trigger1.TriggerName, trigger1.TriggerGroup));
        Assert.Contains(result.Items, x => x.Key == new TriggerKey(trigger2.TriggerName, trigger2.TriggerGroup));

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryTriggers_filters_by_group()
    {
        var store = CreateStore($"i14-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var group1 = $"g1-{Guid.NewGuid():N}";
        var group2 = $"g2-{Guid.NewGuid():N}";

        var trigger1 = CreateWaitingTrigger("trigger1", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group1);
        var trigger2 = CreateWaitingTrigger("trigger2", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group2);

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(trigger1, false);
        await Storage.StoreTriggerAsync(trigger2, false);
        await Index.UpsertAsync(trigger1);
        await Index.UpsertAsync(trigger2);

        var result = await store.QueryTriggers(new TriggerQuery
        {
            Group = GroupMatcher<TriggerKey>.GroupEquals(group1),
            Take = 100
        });

        Assert.Contains(result.Items, x => x.Key.Group == group1);
        Assert.DoesNotContain(result.Items, x => x.Key.Group == group2);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryTriggers_filters_by_name()
    {
        var store = CreateStore($"i15-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var target = $"target-{Guid.NewGuid():N}";
        var other = $"other-{Guid.NewGuid():N}";

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger(target, job.JobName, DateTimeOffset.UtcNow.AddMinutes(10)), false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger(other, job.JobName, DateTimeOffset.UtcNow.AddMinutes(10)), false);

        var result = await store.QueryTriggers(new TriggerQuery
        {
            Name = NameMatcher<TriggerKey>.NameEquals(target),
            Take = 100
        });

        Assert.Single(result.Items);
        Assert.Equal(target, result.Items[0].Key.Name);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryTriggers_filters_by_job()
    {
        var store = CreateStore($"i16-{Guid.NewGuid():N}");
        var job1 = CreateJob($"job1-{Guid.NewGuid():N}");
        var job2 = CreateJob($"job2-{Guid.NewGuid():N}");
        var trigger1 = CreateWaitingTrigger("trigger1", job1.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        var trigger2 = CreateWaitingTrigger("trigger2", job2.JobName, DateTimeOffset.UtcNow.AddMinutes(10));

        await Storage.StoreJobAsync(job1, false);
        await Storage.StoreJobAsync(job2, false);
        await Storage.StoreTriggerAsync(trigger1, false);
        await Storage.StoreTriggerAsync(trigger2, false);

        var result = await store.QueryTriggers(new TriggerQuery
        {
            Job = new JobKey(job1.JobName, job1.JobGroup),
            Take = 100
        });

        Assert.Single(result.Items);
        Assert.Equal("trigger1", result.Items[0].Key.Name);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryTriggers_filters_by_calendar()
    {
        var store = CreateStore($"i17-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");

        var trigger1 = CreateWaitingTrigger("trigger1", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        var trigger2 = CreateWaitingTrigger("trigger2", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));

        trigger1.CalendarName = "calendar-a";
        trigger2.CalendarName = "calendar-b";

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(trigger1, false);
        await Storage.StoreTriggerAsync(trigger2, false);

        var result = await store.QueryTriggers(new TriggerQuery
        {
            CalendarName = "calendar-a",
            Take = 100
        });

        Assert.Single(result.Items);
        Assert.Equal("calendar-a", result.Items[0].CalendarName);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryTriggers_filters_by_state()
    {
        var store = CreateStore($"i18-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");

        var waiting = CreateWaitingTrigger("waiting", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        var paused = CreateWaitingTrigger("paused", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));
        paused.TriggerState = TriggerStates.Paused;

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(waiting, false);
        await Storage.StoreTriggerAsync(paused, false);

        var result = await store.QueryTriggers(new TriggerQuery
        {
            State = TriggerState.Paused,
            Take = 100
        });

        Assert.Single(result.Items);
        Assert.Equal("paused", result.Items[0].Key.Name);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryTriggers_returns_sorted_results()
    {
        var store = CreateStore($"i19-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");

        var triggerB = CreateWaitingTrigger("b", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: "b");
        var triggerA = CreateWaitingTrigger("a", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: "a");

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(triggerB, false);
        await Storage.StoreTriggerAsync(triggerA, false);

        var result = await store.QueryTriggers(new TriggerQuery { Take = 100 });

        var keys = result.Items.Select(x => (x.Key.Group, x.Key.Name)).ToArray();
        var expected = keys.OrderBy(x => x.Group, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, keys);

        await store.Shutdown();
    }
    [Fact]
    public async Task QueryTriggers_skips_missing_triggers()
    {
        var store = CreateStore($"i20-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(trigger, false);

        _cache!.Remove(NCacheKeys.Trigger(_prefix, SchedulerName, trigger.TriggerGroup, trigger.TriggerName));

        var result = await store.QueryTriggers(new TriggerQuery { Take = 100 });

        Assert.DoesNotContain(result.Items, x => x.Key == new TriggerKey(trigger.TriggerName, trigger.TriggerGroup));

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryTriggers_respects_skip()
    {
        var store = CreateStore($"i21-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("a", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10)), false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("b", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10)), false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("c", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10)), false);

        var result = await store.QueryTriggers(new TriggerQuery
        {
            Skip = 1,
            Take = 100
        });

        Assert.Equal(2, result.Items.Count);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryTriggers_respects_take()
    {
        var store = CreateStore($"i22-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("a", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10)), false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("b", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10)), false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("c", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10)), false);

        var result = await store.QueryTriggers(new TriggerQuery { Take = 2 });

        Assert.Equal(2, result.Items.Count);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryTriggers_reports_has_more()
    {
        var store = CreateStore($"i23-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("a", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10)), false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("b", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10)), false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("c", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10)), false);

        var result = await store.QueryTriggers(new TriggerQuery { Take = 2 });

        Assert.True(result.HasMore);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryTriggers_reports_total_count()
    {
        var store = CreateStore($"i24-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("a", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10)), false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("b", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10)), false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("c", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10)), false);

        var result = await store.QueryTriggers(new TriggerQuery
        {
            Take = 1,
            IncludeTotalCount = true
        });

        Assert.Equal(3, result.TotalCount);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryJobGroups_returns_registered_groups()
    {
        var store = CreateStore($"i25-{Guid.NewGuid():N}");
        var group1 = $"group1-{Guid.NewGuid():N}";
        var group2 = $"group2-{Guid.NewGuid():N}";

        await Storage.StoreJobAsync(CreateJob("job1", group1), false);
        await Storage.StoreJobAsync(CreateJob("job2", group2), false);

        var result = await store.QueryJobGroups(new JobGroupQuery { Take = 100 });

        Assert.Contains(result.Items, x => x.Name == group1);
        Assert.Contains(result.Items, x => x.Name == group2);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryJobGroups_includes_paused_only_groups()
    {
        var store = CreateStore($"i26-{Guid.NewGuid():N}");
        var group = $"paused-only-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}", group);

        await Storage.StoreJobAsync(job, false);

        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals(group));

        var result = await store.QueryJobGroups(new JobGroupQuery
        {
            Paused = true,
            Take = 100
        });

        Assert.Contains(result.Items, x => x.Name == group && x.Paused);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryJobGroups_filters_by_name()
    {
        var store = CreateStore($"i27-{Guid.NewGuid():N}");
        var matching = $"tenant-a-{Guid.NewGuid():N}";
        var other = $"tenant-b-{Guid.NewGuid():N}";

        await Storage.StoreJobAsync(CreateJob("job1", matching), false);
        await Storage.StoreJobAsync(CreateJob("job2", other), false);

        var result = await store.QueryJobGroups(new JobGroupQuery
        {
            Name = NameMatcher.NameEquals(matching),
            Take = 100
        });

        Assert.Single(result.Items);
        Assert.Equal(matching, result.Items[0].Name);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryJobGroups_filters_by_paused_state()
    {
        var store = CreateStore($"i28-{Guid.NewGuid():N}");
        var pausedGroup = $"paused-{Guid.NewGuid():N}";
        var activeGroup = $"active-{Guid.NewGuid():N}";

        await Storage.StoreJobAsync(CreateJob("job1", pausedGroup), false);
        await Storage.StoreJobAsync(CreateJob("job2", activeGroup), false);

        await store.PauseJobGroups(GroupMatcher<JobKey>.GroupEquals(pausedGroup));

        var result = await store.QueryJobGroups(new JobGroupQuery
        {
            Paused = true,
            Take = 100
        });

        Assert.Contains(result.Items, x => x.Name == pausedGroup);
        Assert.DoesNotContain(result.Items, x => x.Name == activeGroup);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryJobGroups_returns_sorted_results()
    {
        var store = CreateStore($"i29-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(CreateJob("job", "z-group"), false);
        await Storage.StoreJobAsync(CreateJob("job2", "a-group"), false);
        await Storage.StoreJobAsync(CreateJob("job3", "m-group"), false);

        var result = await store.QueryJobGroups(new JobGroupQuery { Take = 100 });

        var names = result.Items.Select(x => x.Name).ToArray();

        Assert.Equal(names.OrderBy(x => x, StringComparer.Ordinal).ToArray(), names);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryJobGroups_respects_paging()
    {
        var store = CreateStore($"i30-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(CreateJob("job1", "a"), false);
        await Storage.StoreJobAsync(CreateJob("job2", "b"), false);
        await Storage.StoreJobAsync(CreateJob("job3", "c"), false);

        var result = await store.QueryJobGroups(new JobGroupQuery
        {
            Skip = 1,
            Take = 1
        });

        Assert.Single(result.Items);
        Assert.Equal("b", result.Items[0].Name);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryTriggerGroups_returns_registered_groups()
    {
        var store = CreateStore($"i31-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var group1 = $"group1-{Guid.NewGuid():N}";
        var group2 = $"group2-{Guid.NewGuid():N}";

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("t1", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group1), false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("t2", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group2), false);

        var result = await store.QueryTriggerGroups(new TriggerGroupQuery { Take = 100 });

        Assert.Contains(result.Items, x => x.Name == group1);
        Assert.Contains(result.Items, x => x.Name == group2);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryTriggerGroups_includes_paused_only_groups()
    {
        var store = CreateStore($"i32-{Guid.NewGuid():N}");
        var group = $"paused-only-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: group);

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(trigger, false);

        await store.PauseTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals(group));

        var result = await store.QueryTriggerGroups(new TriggerGroupQuery
        {
            Paused = true,
            Take = 100
        });

        Assert.Contains(result.Items, x => x.Name == group && x.Paused);

        await store.Shutdown();
    }
    [Fact]
    public async Task QueryTriggerGroups_filters_by_name()
    {
        var store = CreateStore($"i33-{Guid.NewGuid():N}");
        var matching = $"trigger-a-{Guid.NewGuid():N}";
        var other = $"trigger-b-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("t1", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: matching), false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("t2", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: other), false);

        var result = await store.QueryTriggerGroups(new TriggerGroupQuery
        {
            Name = NameMatcher.NameEquals(matching),
            Take = 100
        });

        Assert.Single(result.Items);
        Assert.Equal(matching, result.Items[0].Name);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryTriggerGroups_filters_by_paused_state()
    {
        var store = CreateStore($"i34-{Guid.NewGuid():N}");
        var pausedGroup = $"paused-{Guid.NewGuid():N}";
        var activeGroup = $"active-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("t1", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: pausedGroup), false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("t2", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: activeGroup), false);

        await store.PauseTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals(pausedGroup));

        var result = await store.QueryTriggerGroups(new TriggerGroupQuery
        {
            Paused = true,
            Take = 100
        });

        Assert.Contains(result.Items, x => x.Name == pausedGroup);
        Assert.DoesNotContain(result.Items, x => x.Name == activeGroup);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryTriggerGroups_returns_sorted_results()
    {
        var store = CreateStore($"i35-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("t1", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: "z"), false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("t2", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: "a"), false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("t3", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: "m"), false);

        var result = await store.QueryTriggerGroups(new TriggerGroupQuery { Take = 100 });

        var names = result.Items.Select(x => x.Name).ToArray();

        Assert.Equal(names.OrderBy(x => x, StringComparer.Ordinal).ToArray(), names);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryTriggerGroups_respects_paging()
    {
        var store = CreateStore($"i36-{Guid.NewGuid():N}");
        var job = CreateJob($"job-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("t1", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: "a"), false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("t2", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: "b"), false);
        await Storage.StoreTriggerAsync(CreateWaitingTrigger("t3", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10), triggerGroup: "c"), false);

        var result = await store.QueryTriggerGroups(new TriggerGroupQuery
        {
            Skip = 1,
            Take = 1
        });

        Assert.Single(result.Items);
        Assert.Equal("b", result.Items[0].Name);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryCalendarNames_returns_calendar_names()
    {
        var store = CreateStore($"i37-{Guid.NewGuid():N}");
        
        var calendar1 = $"calendar-a-{Guid.NewGuid():N}";
        var calendar2 = $"calendar-b-{Guid.NewGuid():N}";

        await store.AddCalendar(calendar1, new HolidayCalendar());
        await store.AddCalendar(calendar2, new HolidayCalendar());


        var result = await store.QueryCalendarNames(new CalendarQuery { Take = 100 });

        Assert.Contains(calendar1, result.Items);
        Assert.Contains(calendar2, result.Items);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryCalendarNames_filters_by_name()
    {
        var store = CreateStore($"i38-{Guid.NewGuid():N}");
        var matching = $"holiday-{Guid.NewGuid():N}";
        var other = $"other-{Guid.NewGuid():N}";

        await store.AddCalendar(matching, new HolidayCalendar());
        await store.AddCalendar(other, new HolidayCalendar());

        var result = await store.QueryCalendarNames(new CalendarQuery
        {
            Name = NameMatcher.NameEquals(matching),
            Take = 100
        });

        Assert.Single(result.Items);
        Assert.Equal(matching, result.Items[0]);

        await store.Shutdown();
    }
    [Fact]
    public async Task QueryCalendarNames_returns_sorted_results()
    {
        var store = CreateStore($"i39-{Guid.NewGuid():N}");

        foreach (var name in new[] { "z", "a", "m" })
        {
            await store.AddCalendar(name, new HolidayCalendar(), new AddCalendarOptions { Replace = false });
        }

        var result = await store.QueryCalendarNames(new CalendarQuery { Take = 100 });

        var names = result.Items.ToArray();

        Assert.Equal(names.OrderBy(x => x, StringComparer.Ordinal).ToArray(), names);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryCalendarNames_respects_paging()
    {
        var store = CreateStore($"i40-{Guid.NewGuid():N}");

        foreach (var name in new[] { "a", "b", "c" })
        {
            await store.AddCalendar(name, new HolidayCalendar(), new AddCalendarOptions { Replace = false });
        }

        var result = await store.QueryCalendarNames(new CalendarQuery
        {
            Skip = 1,
            Take = 1
        });

        Assert.Single(result.Items);
        Assert.Equal("b", result.Items[0]);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryFireInstances_returns_fire_instances()
    {
        var store = CreateStore($"i41-{Guid.NewGuid():N}");
        var instanceId = $"fire-instance-{Guid.NewGuid():N}";
        var schedulerInstanceId = $"scheduler-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}");
        var trigger = CreateWaitingTrigger($"trigger-{Guid.NewGuid():N}", job.JobName, DateTimeOffset.UtcNow.AddMinutes(10));

        await Storage.StoreJobAsync(job, false);
        await Storage.StoreTriggerAsync(trigger, false);

        await Storage.CheckInAsync(new NCacheSchedulerState
        {
            SchedulerName = SchedulerName,
            InstanceName = schedulerInstanceId,
            LastCheckinTimeUtc = DateTimeOffset.UtcNow,
            CheckinInterval = TimeSpan.FromSeconds(10)
        });

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = instanceId,
            TriggerName = trigger.TriggerName,
            TriggerGroup = trigger.TriggerGroup,
            InstanceName = schedulerInstanceId,
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = trigger.NextFireTimeUtc!.Value,
            Priority = trigger.Priority,
            State = FiredTriggerStates.Executing,
            JobName = job.JobName,
            JobGroup = job.JobGroup,
            IsNonConcurrent = job.IsNonConcurrent,
            RequestsRecovery = job.RequestsRecovery
        });

        var result = await store.QueryFireInstances(new FireInstanceQuery
        {
            State = FireInstanceState.Executing,
            Take = 100
        });

        Assert.Contains(result.Items, x => x.FireInstanceId == instanceId);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryFireInstances_filters_by_scheduler_instance()
    {
        var store = CreateStore($"i42-{Guid.NewGuid():N}");
        var node1 = $"node1-{Guid.NewGuid():N}";
        var node2 = $"node2-{Guid.NewGuid():N}";
        var job = CreateJob($"job-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(job, false);

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = $"fire1-{Guid.NewGuid():N}",
            TriggerName = "t1",
            TriggerGroup = "tests",
            InstanceName = node1,
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = DateTimeOffset.UtcNow,
            Priority = 5,
            State = FiredTriggerStates.Executing,
            JobName = job.JobName,
            JobGroup = job.JobGroup
        });

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = $"fire2-{Guid.NewGuid():N}",
            TriggerName = "t2",
            TriggerGroup = "tests",
            InstanceName = node2,
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = DateTimeOffset.UtcNow,
            Priority = 5,
            State = FiredTriggerStates.Executing,
            JobName = job.JobName,
            JobGroup = job.JobGroup
        });

        var result = await store.QueryFireInstances(new FireInstanceQuery
        {
            SchedulerInstanceId = node1,
            Take = 100
        });

        Assert.All(result.Items, x => Assert.Equal(node1, x.SchedulerInstanceId));

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryFireInstances_filters_by_trigger_group()
    {
        var store = CreateStore($"i43-{Guid.NewGuid():N}");

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = $"fire1-{Guid.NewGuid():N}",
            TriggerName = "t1",
            TriggerGroup = "group-a",
            InstanceName = "node-a",
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = DateTimeOffset.UtcNow,
            Priority = 5,
            State = FiredTriggerStates.Executing
        });

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = $"fire2-{Guid.NewGuid():N}",
            TriggerName = "t2",
            TriggerGroup = "group-b",
            InstanceName = "node-a",
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = DateTimeOffset.UtcNow,
            Priority = 5,
            State = FiredTriggerStates.Executing
        });

        var result = await store.QueryFireInstances(new FireInstanceQuery
        {
            TriggerGroup = GroupMatcher<TriggerKey>.GroupEquals("group-a"),
            Take = 100
        });

        Assert.All(result.Items, x => Assert.Equal("group-a", x.TriggerKey.Group));

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryFireInstances_filters_by_trigger_name()
    {
        var store = CreateStore($"i44-{Guid.NewGuid():N}");

        await Storage.CheckInAsync(new NCacheSchedulerState
        {
            SchedulerName = SchedulerName,
            InstanceName = "node",
            LastCheckinTimeUtc = DateTimeOffset.UtcNow,
            CheckinInterval = TimeSpan.FromSeconds(10)
        });
        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = $"fire1-{Guid.NewGuid():N}",
            TriggerName = "target",
            TriggerGroup = "tests",
            InstanceName = "node",
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = DateTimeOffset.UtcNow,
            Priority = 5,
            State = FiredTriggerStates.Executing
        });

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = $"fire2-{Guid.NewGuid():N}",
            TriggerName = "other",
            TriggerGroup = "tests",
            InstanceName = "node",
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = DateTimeOffset.UtcNow,
            Priority = 5,
            State = FiredTriggerStates.Executing
        });

        var result = await store.QueryFireInstances(new FireInstanceQuery
        {
            TriggerName = NameMatcher<TriggerKey>.NameEquals("target"),
            Take = 100
        });

        Assert.Single(result.Items);
        Assert.Equal("target", result.Items[0].TriggerKey.Name);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryFireInstances_filters_by_job()
    {
        var store = CreateStore($"i45-{Guid.NewGuid():N}");
        var job1 = CreateJob($"job1-{Guid.NewGuid():N}");
        var job2 = CreateJob($"job2-{Guid.NewGuid():N}");

        await Storage.StoreJobAsync(job1, false);
        await Storage.StoreJobAsync(job2, false);

        await Storage.CheckInAsync(new NCacheSchedulerState
        {
            SchedulerName = SchedulerName,
            InstanceName = "node",
            LastCheckinTimeUtc = DateTimeOffset.UtcNow,
            CheckinInterval = TimeSpan.FromSeconds(10)
        });

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = $"fire1-{Guid.NewGuid():N}",
            TriggerName = "t1",
            TriggerGroup = "tests",
            InstanceName = "node",
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = DateTimeOffset.UtcNow,
            Priority = 5,
            State = FiredTriggerStates.Executing,
            JobName = job1.JobName,
            JobGroup = job1.JobGroup
        });

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = $"fire2-{Guid.NewGuid():N}",
            TriggerName = "t2",
            TriggerGroup = "tests",
            InstanceName = "node",
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = DateTimeOffset.UtcNow,
            Priority = 5,
            State = FiredTriggerStates.Executing,
            JobName = job2.JobName,
            JobGroup = job2.JobGroup
        });

        var result = await store.QueryFireInstances(new FireInstanceQuery
        {
            Job = new JobKey(job1.JobName, job1.JobGroup),
            Take = 100
        });

        Assert.Single(result.Items);
        Assert.Equal(job1.JobName, result.Items[0].JobKey!.Name);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryFireInstances_filters_by_state()
    {
        var store = CreateStore($"i46-{Guid.NewGuid():N}");

        await Storage.CheckInAsync(new NCacheSchedulerState
        {
            SchedulerName = SchedulerName,
            InstanceName = "node",
            LastCheckinTimeUtc = DateTimeOffset.UtcNow,
            CheckinInterval = TimeSpan.FromSeconds(10)
        });

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = $"fire1-{Guid.NewGuid():N}",
            TriggerName = "executing",
            TriggerGroup = "tests",
            InstanceName = "node",
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = DateTimeOffset.UtcNow,
            Priority = 5,
            State = FiredTriggerStates.Executing
        });

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = $"fire2-{Guid.NewGuid():N}",
            TriggerName = "acquired",
            TriggerGroup = "tests",
            InstanceName = "node",
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = DateTimeOffset.UtcNow,
            Priority = 5,
            State = FiredTriggerStates.Acquired
        });

        var result = await store.QueryFireInstances(new FireInstanceQuery
        {
            State = FireInstanceState.Acquired,
            Take = 100
        });

        Assert.Single(result.Items);
        Assert.Equal("acquired", result.Items[0].TriggerKey.Name);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryFireInstances_returns_sorted_results()
    {
        var store = CreateStore($"i47-{Guid.NewGuid():N}");

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = "z-fire",
            TriggerName = "b",
            TriggerGroup = "group-b",
            InstanceName = "node",
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = DateTimeOffset.UtcNow,
            Priority = 5,
            State = FiredTriggerStates.Executing
        });

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = "a-fire",
            TriggerName = "a",
            TriggerGroup = "group-a",
            InstanceName = "node",
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = DateTimeOffset.UtcNow,
            Priority = 5,
            State = FiredTriggerStates.Executing
        });

        var result = await store.QueryFireInstances(new FireInstanceQuery
        {
            Take = 100
        });

        var keys = result.Items.Select(x => (x.TriggerKey.Group, x.TriggerKey.Name, x.FireInstanceId)).ToArray();
        var expected = keys
            .OrderBy(x => x.Group, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => x.FireInstanceId, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, keys);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryFireInstances_handles_firing_without_job()
    {
        var store = CreateStore($"i48-{Guid.NewGuid():N}");
        var fireId = $"fire-{Guid.NewGuid():N}";

        await Storage.CheckInAsync(new NCacheSchedulerState
        {
            SchedulerName = SchedulerName,
            InstanceName = "node",
            LastCheckinTimeUtc = DateTimeOffset.UtcNow,
            CheckinInterval = TimeSpan.FromSeconds(10)
        });
        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = fireId,
            TriggerName = "orphan",
            TriggerGroup = "tests",
            InstanceName = "node",
            FiredTimeUtc = DateTimeOffset.UtcNow,
            ScheduledTimeUtc = DateTimeOffset.UtcNow,
            Priority = 5,
            State = FiredTriggerStates.Acquired,
            JobName = null,
            JobGroup = null
        });

        var result = await store.QueryFireInstances(new FireInstanceQuery
        {
            State = null,
            Take = 100
        });

        var firing = Assert.Single(result.Items, x => x.FireInstanceId == fireId);

        Assert.Null(firing.JobKey);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryFireInstances_respects_paging()
    {
        var store = CreateStore($"i49-{Guid.NewGuid():N}");
        await Storage.CheckInAsync(new NCacheSchedulerState
        {
            SchedulerName = SchedulerName,
            InstanceName = "node",
            LastCheckinTimeUtc = DateTimeOffset.UtcNow,
            CheckinInterval = TimeSpan.FromSeconds(10)
        });
        for (var i = 0; i < 3; i++)
        {
            await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
            {
                SchedulerName = SchedulerName,
                EntryId = $"fire-{i}",
                TriggerName = $"trigger-{i}",
                TriggerGroup = "tests",
                InstanceName = "node",
                FiredTimeUtc = DateTimeOffset.UtcNow,
                ScheduledTimeUtc = DateTimeOffset.UtcNow,
                Priority = 5,
                State = FiredTriggerStates.Executing
            });
        }

        var result = await store.QueryFireInstances(new FireInstanceQuery
        {
            Take = 2,
            State = FireInstanceState.Executing
        });

        Assert.Equal(2, result.Items.Count);
        Assert.True(result.HasMore);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryClusterNodes_returns_current_node()
    {
        var instanceId = $"i50-{Guid.NewGuid():N}";
        var store = CreateStore(instanceId);

        var nodes = await store.QueryClusterNodes();

        var current = Assert.Single(nodes);

        Assert.Equal(instanceId, current.InstanceId);
        Assert.True(current.IsCurrentNode);

        await store.Shutdown();
    }

    [Fact]
    public async Task QueryClusterNodes_marks_live_nodes_alive()
    {
        var currentId = $"i51-current-{Guid.NewGuid():N}";
        var otherId = $"i51-other-{Guid.NewGuid():N}";

        var current = CreateStore(currentId);
        var other = CreateStore(otherId);

        await other.SchedulerStarted();
        await current.SchedulerStarted();

        var nodes = await current.QueryClusterNodes();

        var otherNode = Assert.Single(nodes, x => x.InstanceId == otherId);

        Assert.Equal(ClusterNodeState.Alive, otherNode.State);
        Assert.False(otherNode.IsCurrentNode);

        await current.Shutdown();
        await other.Shutdown();
    }

    [Fact]
    public async Task QueryClusterNodes_marks_stale_nodes_failed()
    {
        var currentId = $"i52-current-{Guid.NewGuid():N}";
        var staleId = $"i52-stale-{Guid.NewGuid():N}";

        var current = CreateStore(currentId);

        await Storage.CheckInAsync(new NCacheSchedulerState
        {
            SchedulerName = SchedulerName,
            InstanceName = staleId,
            LastCheckinTimeUtc = DateTimeOffset.UtcNow,
            CheckinInterval = TimeSpan.FromSeconds(10)
        });

        var state = await Storage.GetSchedulerStateAsync(staleId);
        Assert.NotNull(state);

        await Task.Delay(TimeSpan.FromSeconds(21));

        var nodes = await current.QueryClusterNodes();
        var staleNode = Assert.Single(nodes, x => x.InstanceId == staleId);

        Assert.Equal(ClusterNodeState.Failed, staleNode.State);

        await current.Shutdown();
    }


    [Fact]
    public async Task QueryClusterNodes_handles_missing_current_state()
    {
        var instanceId = $"i53-{Guid.NewGuid():N}";
        var store = CreateStore(instanceId);

        var nodes = await store.QueryClusterNodes();

        var current = Assert.Single(nodes);

        Assert.Equal(instanceId, current.InstanceId);
        Assert.True(current.IsCurrentNode);
        Assert.Equal(ClusterNodeState.Alive, current.State);

        await store.Shutdown();
    }
}

