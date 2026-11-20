using NCache.OSS.Quartz.Keys;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzKeyCollisionUnitTest : NCacheQuartzScenarioTestBase
{
    [Fact]
    public void Keys_of_names_without_reserved_characters_are_not_escaped()
    {
        Assert.Equal("p:s:job:reports.nightly", NCacheKeys.Job("p:", "s", "reports", "nightly"));
        Assert.Equal("p:s:trigger:reports.nightly", NCacheKeys.Trigger("p:", "s", "reports", "nightly"));
        Assert.Equal("p:s:job:reports.nightly:header", NCacheKeys.JobHeader("p:", "s", "reports", "nightly"));
    }

    [Theory]
    [InlineData("a", "b.c", "a.b", "c")]
    [InlineData("g", "nightly:header", "g", "nightly")]
    [InlineData("g", "x:triggers", "g", "x")]
    [InlineData("g", "x:fired", "g", "x")]
    [InlineData("g", "100%", "g", "100%25")]
    [InlineData("g", "a%2Eb", "g", "a.b")]
    [InlineData("a:b", "c", "a", "b:c")]
    public void Distinct_group_and_name_pairs_never_share_a_row_or_registry_key(string group1, string name1, string group2, string name2)
    {
        var keys1 = new[]
        {
            NCacheKeys.Job("p:", "s", group1, name1), NCacheKeys.JobHeader("p:", "s", group1, name1),
            NCacheKeys.JobTriggers("p:", "s", group1, name1), NCacheKeys.JobFiredTriggers("p:", "s", group1, name1)
        };

        var keys2 = new[]
        {
            NCacheKeys.Job("p:", "s", group2, name2), NCacheKeys.JobHeader("p:", "s", group2, name2),
            NCacheKeys.JobTriggers("p:", "s", group2, name2), NCacheKeys.JobFiredTriggers("p:", "s", group2, name2)
        };

        Assert.Empty(keys1.Intersect(keys2, StringComparer.Ordinal));
        Assert.NotEqual(NCacheKeys.Trigger("p:", "s", group1, name1), NCacheKeys.Trigger("p:", "s", group2, name2));
    }

    [Fact]
    public async Task B1a_jobs_whose_group_and_name_join_to_the_same_dotted_string_are_different_jobs()
    {
        var store = CreateStore("node-1");
        var first = Job("b.c", "a");
        var second = Job("c", "a.b");

        await store.AddJob(first);
        await store.AddJob(second);

        var readFirst = await store.GetJob(first.Key);
        var readSecond = await store.GetJob(second.Key);

        Assert.Equal(first.Key, readFirst!.Key);
        Assert.Equal(second.Key, readSecond!.Key);
        Assert.Equal(2, (await store.QueryJobs(new JobQuery())).Items.Count);
    }

    [Fact]
    public async Task B1a_replacing_one_dotted_job_does_not_overwrite_the_other()
    {
        var store = CreateStore("node-1");
        var first = Job("b.c", "a", data: new JobDataMap { ["owner"] = "first" });
        var second = Job("c", "a.b", data: new JobDataMap { ["owner"] = "second" });

        await store.AddJob(first);
        await store.AddJob(second, new AddJobOptions { Replace = true });

        var readFirst = await store.GetJob(first.Key);

        Assert.NotNull(readFirst);
        Assert.Equal(first.Key, readFirst!.Key);
        Assert.Equal("first", readFirst.JobDataMap["owner"]);
        Assert.Equal("second", (await store.GetJob(second.Key))!.JobDataMap["owner"]);
    }

    [Fact]
    public async Task B1a_deleting_one_dotted_job_leaves_the_other_in_place()
    {
        var store = CreateStore("node-1");
        var first = Job("b.c", "a");
        var second = Job("c", "a.b");

        await store.AddJob(first);
        await store.AddJob(second);

        Assert.True(await store.DeleteJob(second.Key));

        Assert.True(await store.Exists(first.Key));
        Assert.False(await store.Exists(second.Key));
        Assert.Equal(first.Key, Assert.Single((await store.QueryJobs(new JobQuery())).Items).Key);
    }

    [Fact]
    public async Task B1a_triggers_whose_group_and_name_join_to_the_same_dotted_string_are_both_stored_and_both_fire()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1");
        var due = DateTimeOffset.UtcNow.AddSeconds(-1);

        await store.ScheduleJob(job, OneShot("b.c", "a", job.Key, due));
        await store.AddTrigger(OneShot("c", "a.b", job.Key, due));

        Assert.Equal(new TriggerKey("b.c", "a"), (await store.GetTrigger(new TriggerKey("b.c", "a")))!.Key);
        Assert.Equal(new TriggerKey("c", "a.b"), (await store.GetTrigger(new TriggerKey("c", "a.b")))!.Key);
        Assert.Equal(2, (await store.GetTriggersForJob(job.Key)).Count);

        var acquired = await AcquireDueAsync(store, 5);
        Assert.Equal(2, acquired.Count);

        var fired = await store.TriggersFired(acquired);
        Assert.All(fired, f => Assert.NotNull(f.TriggerFiredBundle));
        Assert.Equal(
            new[] { new TriggerKey("b.c", "a"), new TriggerKey("c", "a.b") }.OrderBy(k => k.ToString()),
            fired.Select(f => f.TriggerFiredBundle!.Trigger.Key).OrderBy(k => k.ToString()));
    }

    [Fact]
    public async Task B1a_a_job_with_reserved_characters_in_both_parts_goes_through_a_full_firing_cycle()
    {
        var store = CreateStore("node-1");
        var job = Job("export:daily.v2", "reports.eu%", durable: false);
        var trigger = OneShot("run.now:1", "g.x:y", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));

        await store.ScheduleJob(job, trigger);

        var bundle = await FireOneAsync(store);
        Assert.Equal(job.Key, bundle.JobDetail.Key);
        Assert.Equal(trigger.Key, bundle.Trigger.Key);

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.DeleteTrigger);

        Assert.False(await store.Exists(trigger.Key));
        Assert.False(await store.Exists(job.Key));
    }

    [Fact]
    public async Task B1b_a_job_named_like_another_jobs_header_record_is_a_separate_job()
    {
        var store = CreateStore("node-1");

        await store.AddJob(Job("nightly", "g"));
        await store.AddJob(Job("nightly:header", "g"));

        Assert.True(await store.Exists(new JobKey("nightly", "g")));
        Assert.True(await store.Exists(new JobKey("nightly:header", "g")));
        Assert.Equal("nightly", (await Storage.GetJobHeaderAsync("g", "nightly"))!.JobName);
    }

    [Fact]
    public async Task B1b_jobs_named_like_another_jobs_trigger_and_firing_registries_keep_their_own_triggers()
    {
        var store = CreateStore("node-1");
        var x = Job("x", "g");
        var xTriggers = Job("x:triggers", "g");
        var xFired = Job("x:fired", "g");
        var later = DateTimeOffset.UtcNow.AddHours(1);

        await store.ScheduleJob(x, OneShot("tx", "g", x.Key, later));
        await store.ScheduleJob(xTriggers, OneShot("tx-triggers", "g", xTriggers.Key, later));
        await store.ScheduleJob(xFired, OneShot("tx-fired", "g", xFired.Key, later));

        Assert.Equal("tx", Assert.Single(await store.GetTriggersForJob(x.Key)).Key.Name);
        Assert.Equal("tx-triggers", Assert.Single(await store.GetTriggersForJob(xTriggers.Key)).Key.Name);
        Assert.Equal("tx-fired", Assert.Single(await store.GetTriggersForJob(xFired.Key)).Key.Name);
        Assert.Equal(3, (await store.QueryJobs(new JobQuery())).Items.Count);
    }

    [Theory]
    [InlineData("cleanup:fired")]
    [InlineData("cleanup:triggers")]
    [InlineData("cleanup:header")]
    public async Task B1b_a_job_whose_name_ends_in_a_registry_suffix_survives_the_start_up_registry_repair(string jobName)
    {
        var store = CreateStore("node-1");
        var lone = Job(jobName, "g");
        await store.ScheduleJob(lone, OneShot("t", "g", lone.Key, DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Single((await store.QueryJobs(new JobQuery())).Items);

        var restarted = CreateStore("node-1");
        await restarted.SchedulerStarted();

        try
        {
            Assert.Equal(lone.Key, Assert.Single((await restarted.QueryJobs(new JobQuery())).Items).Key);
            Assert.True(await restarted.Exists(lone.Key));
            Assert.Single(await restarted.GetTriggersForJob(lone.Key));
        }
        finally
        {
            await restarted.Shutdown();
        }
    }

    [Fact]
    public async Task B1b_names_containing_the_escape_character_itself_stay_distinct_and_survive_a_restart()
    {
        var store = CreateStore("node-1");

        foreach (var name in new[] { "a.b", "a%2Eb", "100%", "100%25" })
        {
            await store.AddJob(Job(name, "g"));
        }

        var restarted = CreateStore("node-1");
        await restarted.SchedulerStarted();

        try
        {
            var listed = (await restarted.QueryJobs(new JobQuery())).Items.Select(j => j.Key.Name).OrderBy(n => n, StringComparer.Ordinal);
            Assert.Equal(new[] { "100%", "100%25", "a%2Eb", "a.b" }, listed);

            foreach (var name in new[] { "a.b", "a%2Eb", "100%", "100%25" })
            {
                Assert.Equal(name, (await restarted.GetJob(new JobKey(name, "g")))!.Key.Name);
            }
        }
        finally
        {
            await restarted.Shutdown();
        }
    }
}

