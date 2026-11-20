using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Model;
using NCache.OSS.Quartz.Serialization;
using Quartz;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzLeftoverJobHeaderUnitTest : NCacheQuartzScenarioTestBase
{
    private static bool IsHeaderKey(string key) => key.EndsWith(":header", StringComparison.Ordinal);

    private void LeaveOrphanHeader(string group, string name)
    {
        var header = new NCacheJobHeaderData
        {
            SchedulerName = SchedulerName,
            JobGroup = group,
            JobName = name,
            Description = "left behind by an interrupted delete",
            JobClassName = typeof(TestJob).AssemblyQualifiedName!,
            IsDurable = true,
            IsNonConcurrent = true
        };

        _cache!.Insert(NCacheKeys.JobHeader(_prefix, SchedulerName, group, name), NCacheSerializer.Serialize(header));
    }

    [Fact]
    public async Task B7_a_DeleteJob_interrupted_while_removing_the_header_does_not_block_the_job_key()
    {
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewStoreAsync(cache: cache);
        var job = Job("job-1");
        await store.AddJob(job);

        control.FailWhen = FaultInjectingCache.Times(1, FaultInjectingCache.Call("Remove", IsHeaderKey));

        await Assert.ThrowsAnyAsync<Exception>(async () => await store.DeleteJob(job.Key));
        control.FailWhen = null;

        Assert.False(await store.Exists(job.Key));

        await store.AddJob(job);
        Assert.True(await store.Exists(job.Key));
        Assert.True(await store.DeleteJob(job.Key));
        await store.AddJob(job);
        Assert.True(await store.Exists(job.Key));
    }

    [Fact]
    public async Task B7_AddJob_over_a_leftover_header_succeeds_and_rewrites_the_header_from_the_new_job()
    {
        var store = CreateStore("node-1");
        LeaveOrphanHeader("tests", "job-1");

        var job = JobBuilder.Create<TestJob>().WithIdentity("job-1", "tests").WithDescription("fresh").StoreDurably().Build();
        await store.AddJob(job);

        var header = await Storage.GetJobHeaderAsync("tests", "job-1");
        Assert.Equal("fresh", header!.Description);
        Assert.False(header.IsNonConcurrent);
        Assert.Equal("fresh", (await store.GetJob(job.Key))!.Description);
    }

    [Fact]
    public async Task B7_ScheduleJob_over_a_leftover_header_succeeds_and_the_trigger_fires()
    {
        var store = CreateStore("node-1");
        LeaveOrphanHeader("tests", "job-1");

        var job = Job("job-1");
        await store.ScheduleJob(job, OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1)));

        var bundle = await FireOneAsync(store);
        Assert.Equal(job.Key, bundle.JobDetail.Key);
        Assert.False(bundle.JobDetail.ConcurrentExecutionDisallowed);
    }

    [Fact]
    public async Task B7_a_leftover_header_alone_does_not_make_the_job_exist()
    {
        var store = CreateStore("node-1");
        LeaveOrphanHeader("tests", "job-1");

        Assert.False(await store.Exists(new JobKey("job-1", "tests")));
        Assert.Null(await store.GetJob(new JobKey("job-1", "tests")));
    }

    [Fact]
    public async Task B7_AddJob_without_Replace_still_refuses_a_job_whose_row_exists()
    {
        var store = CreateStore("node-1");
        await store.AddJob(Job("job-1"));

        await Assert.ThrowsAsync<ObjectAlreadyExistsException>(async () => await store.AddJob(Job("job-1")));
    }

    [Fact]
    public async Task B7_the_start_up_registry_repair_removes_headers_whose_row_is_gone_and_restores_missing_ones()
    {
        var store = CreateStore("node-1");
        var live = Job("live");
        var headerless = Job("headerless");
        await store.AddJob(live);
        await store.AddJob(headerless);

        LeaveOrphanHeader("tests", "gone");
        _cache!.Remove(NCacheKeys.JobHeader(_prefix, SchedulerName, "tests", "headerless"));

        var restarted = CreateStore("node-1");
        await restarted.SchedulerStarted();

        try
        {
            Assert.Null(await Storage.GetJobHeaderAsync("tests", "gone"));
            Assert.NotNull(await Storage.GetJobHeaderAsync("tests", "live"));
            Assert.Equal("headerless", (await Storage.GetJobHeaderAsync("tests", "headerless"))!.JobName);

            Assert.Equal(
                new[] { "headerless", "live" },
                (await restarted.QueryJobs(new JobQuery())).Items.Select(j => j.Key.Name).OrderBy(n => n, StringComparer.Ordinal));
        }
        finally
        {
            await restarted.Shutdown();
        }
    }
}

