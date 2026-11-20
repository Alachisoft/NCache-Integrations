using Microsoft.Extensions.DependencyInjection;
using NCache.OSS.Quartz.Extensions;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzSchedulerNameMismatchUnitTest : NCacheQuartzScenarioTestBase
{
    private NCacheJobStore UninitialisedStore() => new(Storage, Index, Locks);

    private NCacheJobStoreOptions Options(string schedulerName) => new()
    {
        CacheName = TestCache.CacheName,
        SchedulerName = schedulerName,
        InstanceId = "di-node",
        KeyPrefix = _prefix
    };

    private static string UniqueName(string stem) => $"{stem}-{Guid.NewGuid():N}";

    [Fact]
    public async Task B3_Initialize_refuses_an_identity_whose_scheduler_name_differs_from_the_stores()
    {
        var store = UninitialisedStore();

        var ex = await Assert.ThrowsAsync<SchedulerConfigException>(async () =>
            await store.Initialize(new SchedulerIdentity { SchedulerName = "orders", InstanceId = "node-1" }));

        Assert.Contains("'orders'", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"'{SchedulerName}'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task B3_the_scheduler_name_comparison_is_case_sensitive_like_the_keys()
    {
        var store = UninitialisedStore();

        await Assert.ThrowsAsync<SchedulerConfigException>(async () =>
            await store.Initialize(new SchedulerIdentity { SchedulerName = SchedulerName.ToUpperInvariant(), InstanceId = "node-1" }));
    }

    [Fact]
    public async Task B3_a_store_whose_Initialize_was_refused_cannot_be_used_to_write_rows()
    {
        var store = UninitialisedStore();

        await Assert.ThrowsAsync<SchedulerConfigException>(async () =>
            await store.Initialize(new SchedulerIdentity { SchedulerName = "orders", InstanceId = "node-1" }));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.AddJob(Job("job-1")));
        Assert.False(await Storage.JobExistsAsync("tests", "job-1"));
    }

    [Fact]
    public async Task B3_a_refused_Initialize_leaves_the_existing_schedule_intact_for_a_correctly_named_restart()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1");
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddHours(1));
        await store.ScheduleJob(job, trigger);

        await Assert.ThrowsAsync<SchedulerConfigException>(async () =>
            await UninitialisedStore().Initialize(new SchedulerIdentity { SchedulerName = "orders", InstanceId = "node-1" }));

        var restarted = CreateStore("node-1");
        await restarted.SchedulerStarted();

        try
        {
            Assert.Equal(trigger.Key, Assert.Single((await restarted.QueryTriggers(new TriggerQuery())).Items).Key);
            Assert.Equal(job.Key, Assert.Single((await restarted.QueryJobs(new JobQuery())).Items).Key);

            var acquired = await restarted.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddHours(2), 5));
            Assert.Equal(trigger.Key, Assert.Single(acquired).Key);
            await restarted.ReleaseAcquiredTrigger(acquired[0]);
        }
        finally
        {
            await restarted.Shutdown();
        }
    }

    [Fact]
    public async Task C3_matching_names_start_and_keep_their_schedules_across_a_restart()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1");
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddHours(1));
        await store.ScheduleJob(job, trigger);

        var restarted = CreateStore("node-1");
        await restarted.SchedulerStarted();

        try
        {
            Assert.Single((await restarted.QueryTriggers(new TriggerQuery())).Items);
            Assert.Equal(TriggerState.Normal, await restarted.GetTriggerState(trigger.Key));
            Assert.Equal(SchedulerName, restarted.SchedulerName);

            await Assert.ThrowsAsync<ObjectAlreadyExistsException>(async () =>
                await restarted.ScheduleJob(Job("job-1"), OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddHours(1))));
        }
        finally
        {
            await restarted.Shutdown();
        }
    }

    [Fact]
    public void B3_a_named_registration_with_a_different_SchedulerName_is_refused_when_it_is_configured()
    {
        var services = new ServiceCollection();
        var registration = UniqueName("orders");

        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddQuartz(registration, q => q.UseNCacheStore(Options(UniqueName("billing")))));

        Assert.Contains(registration, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task C3_a_named_registration_with_the_same_SchedulerName_creates_a_scheduler_of_that_name()
    {
        var name = UniqueName("same");
        var services = new ServiceCollection();
        services.AddQuartz(name, q => q.UseNCacheStore(Options(name)));

        await using var provider = services.BuildServiceProvider();
        var scheduler = await provider.GetRequiredKeyedService<ISchedulerFactory>(name).GetScheduler();

        try
        {
            Assert.Equal(name, scheduler.SchedulerName);
        }
        finally
        {
            await scheduler.Shutdown();
        }
    }

    [Fact]
    public async Task C3_an_unnamed_registration_takes_its_scheduler_name_from_the_store_options()
    {
        var name = UniqueName("unnamed");
        var services = new ServiceCollection();
        services.AddQuartz(q => q.UseNCacheStore(Options(name)));

        await using var provider = services.BuildServiceProvider();
        var scheduler = await provider.GetRequiredService<ISchedulerFactory>().GetScheduler();

        try
        {
            Assert.Equal(name, scheduler.SchedulerName);
        }
        finally
        {
            await scheduler.Shutdown();
        }
    }
}

