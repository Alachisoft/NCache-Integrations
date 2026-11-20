using NCache.OSS.Quartz.Index;
using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Locking;
using NCache.OSS.Quartz.Storage;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzKeyEscapingUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public void Escape_leaves_a_part_without_reserved_characters_unchanged()
    {
        Assert.Equal("nightly-report_01", NCacheKeys.Escape("nightly-report_01"));
        Assert.Equal(string.Empty, NCacheKeys.Escape(string.Empty));
        Assert.EndsWith(":job:grp.job", NCacheKeys.Job(_prefix, SchedulerName, "grp", "job"));
        Assert.EndsWith(":trigger:grp.trg", NCacheKeys.Trigger(_prefix, SchedulerName, "grp", "trg"));
    }
    [Fact]
    public void Escape_percent_encodes_percent_dot_and_colon_without_double_encoding()
    {
        Assert.Equal("a%2Eb", NCacheKeys.Escape("a.b"));
        Assert.Equal("a%3Ab", NCacheKeys.Escape("a:b"));
        Assert.Equal("a%25b", NCacheKeys.Escape("a%b"));

        Assert.Equal("%25%2E%3A", NCacheKeys.Escape("%.:"));
        Assert.Equal("x%252Ey", NCacheKeys.Escape("x%2Ey"));
    }

    [Fact]
    public void Keys_of_a_group_and_name_split_at_different_dots_are_distinct()
    {
        Assert.NotEqual(NCacheKeys.Job(_prefix, SchedulerName, "a.b", "c"), NCacheKeys.Job(_prefix, SchedulerName, "a", "b.c"));
        Assert.NotEqual(NCacheKeys.Trigger(_prefix, SchedulerName, "a.b", "c"), NCacheKeys.Trigger(_prefix, SchedulerName, "a", "b.c"));
    }

    [Fact]
    public void Keys_of_a_literal_escape_sequence_and_the_character_it_encodes_are_distinct()
    {
        Assert.NotEqual(NCacheKeys.Job(_prefix, SchedulerName, "g", "a%2Eb"), NCacheKeys.Job(_prefix, SchedulerName, "g", "a.b"));
        Assert.NotEqual(NCacheKeys.Trigger(_prefix, SchedulerName, "g", "a%3Ab"), NCacheKeys.Trigger(_prefix, SchedulerName, "g", "a:b"));
    }

    [Fact]
    public void A_name_ending_in_a_registry_suffix_does_not_share_a_key_with_that_registry()
    {
        Assert.NotEqual(NCacheKeys.JobHeader(_prefix, SchedulerName, "g", "nightly"), NCacheKeys.Job(_prefix, SchedulerName, "g", "nightly:header"));
        Assert.NotEqual(NCacheKeys.JobTriggers(_prefix, SchedulerName, "g", "nightly"), NCacheKeys.Job(_prefix, SchedulerName, "g", "nightly:triggers"));
        Assert.NotEqual(NCacheKeys.JobFiredTriggers(_prefix, SchedulerName, "g", "nightly"), NCacheKeys.Job(_prefix, SchedulerName, "g", "nightly:fired"));
    }

    [Fact]
    public async Task Jobs_whose_group_and_name_split_differently_are_stored_side_by_side()
    {
        var store = CreateStore("instance-1");
        var first = ScenarioKit.Job("b.c", "a", data: ("which", "first"));
        var second = ScenarioKit.Job("c", "a.b", data: ("which", "second"));

        await store.AddJob(first);
        await store.AddJob(second);

        await store.AddJob(ScenarioKit.Job("c", "a.b", data: ("which", "second-replaced")), new AddJobOptions { Replace = true });

        var readFirst = await store.GetJob(first.Key);
        var readSecond = await store.GetJob(second.Key);

        Assert.NotNull(readFirst);
        Assert.Equal(first.Key, readFirst!.Key);
        Assert.Equal("first", readFirst.JobDataMap["which"]);
        Assert.Equal(second.Key, readSecond!.Key);
        Assert.Equal("second-replaced", readSecond.JobDataMap["which"]);

        var listed = (await store.QueryJobs(new JobQuery())).Items.Select(h => h.Key).ToList();
        Assert.Contains(first.Key, listed);
        Assert.Contains(second.Key, listed);
        Assert.Equal(2, listed.Count);
    }

    [Fact]
    public async Task Jobs_named_like_registry_suffixes_coexist_with_their_namesake_and_survive_a_restart()
    {
        var store = CreateStore("instance-1");
        var names = new[] { "nightly", "nightly:header", "nightly:triggers", "nightly:fired", "50%.done" };

        foreach (var name in names)
        {
            await store.AddJob(ScenarioKit.Job(name, "g"));
        }

        var trigger = ScenarioKit.Repeating("t", "g", new JobKey("nightly", "g"), DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromHours(1));
        await store.AddTrigger(trigger);

        var restarted = CreateStore("instance-1");

        try
        {
            await restarted.SchedulerStarted();

            var listed = (await restarted.QueryJobs(new JobQuery())).Items.Select(h => h.Key.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), listed);

            foreach (var name in names)
            {
                Assert.True(await restarted.Exists(new JobKey(name, "g")), name);
            }

            var forNightly = await restarted.GetTriggersForJob(new JobKey("nightly", "g"));
            Assert.Equal(trigger.Key, Assert.Single(forNightly).Key);
            Assert.Empty(await restarted.GetTriggersForJob(new JobKey("nightly:triggers", "g")));
        }
        finally
        {
            await restarted.Shutdown();
        }
    }

    [Fact]
    public async Task A_trigger_whose_group_and_name_hold_reserved_characters_is_acquired_and_fired()
    {
        var store = CreateStore("instance-1");
        var job = ScenarioKit.Job("export:csv", "reports.daily");
        var trigger = ScenarioKit.OneShot("fire:100%.now", "grp.x:y", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));

        await store.ScheduleJob(job, trigger);

        var acquired = await store.AcquireNextTriggers(ScenarioKit.DueNow(5));
        Assert.Equal(trigger.Key, Assert.Single(acquired).Key);

        var fired = await store.TriggersFired(acquired);
        var bundle = Assert.Single(fired).TriggerFiredBundle;

        Assert.NotNull(bundle);
        Assert.Equal(job.Key, bundle!.JobDetail.Key);
    }

    [Fact]
    public async Task Initialize_makes_no_cache_calls()
    {
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var store = new NCacheJobStore(
            new NCacheStorage(cache, _prefix, SchedulerName),
            new NextFireTimeIndex(cache, _prefix, SchedulerName),
            new NCacheDistributedLocks(cache, _prefix, SchedulerName));
        control.ResetCalls();

        await store.Initialize(new SchedulerIdentity { SchedulerName = SchedulerName, InstanceId = "instance-1" });

        Assert.Equal(0, control.Calls);
        Assert.Equal("instance-1", store.InstanceId);
    }
}

