using Alachisoft.NCache.Client;
using Microsoft.Extensions.DependencyInjection;
using NCache.OSS.Quartz.Extensions;
using NCache.OSS.Quartz.Index;
using NCache.OSS.Quartz.Locking;
using NCache.OSS.Quartz.Storage;
using Quartz;
using Quartz.Extensibility;
using Quartz.Serialization.SystemTextJson;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzDependencyInjectionUnitTest : IAsyncLifetime
{
    private readonly string _prefix = $"ncache:quartz:test:di:{Guid.NewGuid():N}:";
    private ICache? _cache;

    public Task InitializeAsync()
    {
        _cache = TestCache.Get();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _cache = null;
        return Task.CompletedTask;
    }

    private NCacheStorage StorageFor(string schedulerName) => new(_cache!, _prefix, schedulerName);

    private NCacheJobStoreOptions Options(string schedulerName, string instanceId) => new()
    {
        CacheName = TestCache.CacheName,
        SchedulerName = schedulerName,
        InstanceId = instanceId,
        KeyPrefix = _prefix,
        RepairRegistriesOnStart = false
    };

    private static async Task ShutdownQuietlyAsync(IScheduler scheduler)
    {
        try
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
        }
        catch (SchedulerException)
        {
        }
    }

    [Fact]
    public async Task Options_registration_gives_the_scheduler_the_options_identity_and_a_working_clustered_store()
    {
        const string schedulerName = "di-options";

        var scheduler = await QuartzSchedulerBuilder
            .Create(q => q
                .UseNCacheStore(Options(schedulerName, "di-node-1"))
                .UseDefaultThreadPool(1))
            .BuildScheduler();

        try
        {
            await scheduler.Start();

            Assert.Equal(schedulerName, scheduler.SchedulerName);
            Assert.Equal("di-node-1", scheduler.SchedulerInstanceId);

            var meta = await scheduler.GetMetadata();
            Assert.Contains(nameof(NCacheJobStore), meta.JobStoreTypeName);
            Assert.True(meta.JobStoreClustered);
            Assert.True(meta.JobStorePersistent);

            var self = Assert.Single(await scheduler.QueryClusterNodes(), n => n.IsCurrentNode);
            Assert.Equal("di-node-1", self.InstanceId);
            Assert.Contains("di-node-1", await StorageFor(schedulerName).GetRegisteredSchedulerInstanceNamesAsync());

            var job = JobBuilder.Create<NoOpJob>().WithIdentity("job-1", "di").StoreDurably().Build();
            var trigger = TriggerBuilder.Create().WithIdentity("trigger-1", "di").ForJob(job.Key).StartAt(DateTimeOffset.UtcNow.AddHours(1)).Build();
            await scheduler.ScheduleJob(job, trigger);

            var storedJob = await StorageFor(schedulerName).GetJobAsync("di", "job-1");
            Assert.NotNull(storedJob);
            Assert.Equal(schedulerName, storedJob!.SchedulerName);
            Assert.NotNull(await StorageFor(schedulerName).GetTriggerAsync("di", "trigger-1"));
        }
        finally
        {
            await ShutdownQuietlyAsync(scheduler);
        }
    }

    [Fact]
    public async Task Named_registration_with_the_same_scheduler_name_builds_and_starts()
    {
        const string name = "di-named-match";

        var services = new ServiceCollection();
        services.AddQuartz(name, q => q.UseNCacheStore(Options(name, "named-node")).UseDefaultThreadPool(1));

        await using var provider = services.BuildServiceProvider();
        var scheduler = await provider.GetRequiredKeyedService<ISchedulerFactory>(name).GetScheduler();

        try
        {
            await scheduler.Start();

            Assert.Equal(name, scheduler.SchedulerName);
            Assert.Equal("named-node", scheduler.SchedulerInstanceId);
            Assert.Contains("named-node", await StorageFor(name).GetRegisteredSchedulerInstanceNamesAsync());
        }
        finally
        {
            await ShutdownQuietlyAsync(scheduler);
        }
    }

    [Fact]
    public void Named_registration_refuses_a_different_scheduler_name_in_the_options()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddQuartz("di-registered", q => q.UseNCacheStore(Options("di-other", "n1"))));

        Assert.Contains("di-registered", ex.Message);
        Assert.Contains("di-other", ex.Message);
    }

    [Fact]
    public async Task The_store_built_by_the_container_raises_listener_notifications_through_the_scheduler_signaler()
    {
        const string schedulerName = "di-signaler";
        var options = Options(schedulerName, "signal-node");
        options.MisfireThreshold = TimeSpan.FromSeconds(1);

        var scheduler = await QuartzSchedulerBuilder
            .Create(q => q
                .UseNCacheStore(options)
                .UseDefaultThreadPool(1))
            .BuildScheduler();

        var listener = new MisfireListener();
        scheduler.ListenerManager.AddTriggerListener(listener, Matchers.AllTriggers());

        try
        {
            var job = JobBuilder.Create<NoOpJob>().WithIdentity("late-job", "di").StoreDurably().Build();
            var trigger = TriggerBuilder.Create()
                .WithIdentity("late-trigger", "di")
                .ForJob(job.Key)
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(1))
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromMinutes(10)).RepeatForever())
                .Build();
            await scheduler.ScheduleJob(job, trigger);
            await Task.Delay(TimeSpan.FromSeconds(3.5));

            await scheduler.Start();

            var notified = await Task.WhenAny(listener.Misfired.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.Same(listener.Misfired.Task, notified);
            Assert.Equal(trigger.Key, await listener.Misfired.Task);
        }
        finally
        {
            await ShutdownQuietlyAsync(scheduler);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void String_overload_rejects_an_empty_cache_name(string? cacheName)
    {
        var services = new ServiceCollection();

        Assert.ThrowsAny<ArgumentException>(() =>
            services.AddQuartz(q => q.UseNCacheStore(cacheName!, "sched", "node")));
    }

    [Fact]
    public void Options_overload_names_a_missing_cache_name()
    {
        var services = new ServiceCollection();

        var ex = Assert.ThrowsAny<ArgumentException>(() =>
            services.AddQuartz(q => q.UseNCacheStore(new NCacheJobStoreOptions { CacheName = "", SchedulerName = "sched", InstanceId = "node" })));

        Assert.Contains("CacheName", ex.Message);
    }

    [Theory]
    [InlineData("AUTO")]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData(null)]
    public async Task An_auto_or_missing_instance_id_is_generated_by_quartz_and_used_by_the_store(string? instanceId)
    {
        var schedulerName = $"di-auto-{Guid.NewGuid():N}";
        var options = Options(schedulerName, "unused");
        options.InstanceId = instanceId;

        var scheduler = await QuartzSchedulerBuilder.Create(q => q.UseNCacheStore(options).UseDefaultThreadPool(1)).BuildScheduler();

        try
        {
            await scheduler.Start();

            var id = scheduler.SchedulerInstanceId;
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.NotEqual("AUTO", id, StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual(QuartzSchedulerOptions.DefaultInstanceId, id);
            Assert.Equal(id, Assert.Single(await scheduler.QueryClusterNodes(), n => n.IsCurrentNode).InstanceId);
            Assert.Contains(id, await StorageFor(schedulerName).GetRegisteredSchedulerInstanceNamesAsync());
        }
        finally
        {
            await ShutdownQuietlyAsync(scheduler);
        }
    }

    [Fact]
    public async Task Two_nodes_with_auto_instance_ids_get_different_ids()
    {
        var schedulerName = $"di-auto-two-{Guid.NewGuid():N}";
        var first = Options(schedulerName, "AUTO");
        var second = Options(schedulerName, "AUTO");

        var a = await QuartzSchedulerBuilder.Create(q => q.UseNCacheStore(first).UseDefaultThreadPool(1)).BuildScheduler();
        var b = await QuartzSchedulerBuilder.Create(q => q.UseNCacheStore(second).UseDefaultThreadPool(1)).BuildScheduler();

        try
        {
            await a.Start();
            await b.Start();

            Assert.NotEqual(a.SchedulerInstanceId, b.SchedulerInstanceId);

            var registered = await StorageFor(schedulerName).GetRegisteredSchedulerInstanceNamesAsync();
            Assert.Contains(a.SchedulerInstanceId, registered);
            Assert.Contains(b.SchedulerInstanceId, registered);
        }
        finally
        {
            await ShutdownQuietlyAsync(a);
            await ShutdownQuietlyAsync(b);
        }
    }

    [Fact]
    public async Task A_missing_scheduler_name_defaults_to_quartz_default_name()
    {
        var options = Options("unused", "default-name-node");
        options.SchedulerName = null;
        options.KeyPrefix = $"{_prefix}defaultname:";

        var scheduler = await QuartzSchedulerBuilder.Create(q => q.UseNCacheStore(options).UseDefaultThreadPool(1)).BuildScheduler();

        try
        {
            await scheduler.Start();

            Assert.Equal(QuartzSchedulerOptions.DefaultInstanceName, scheduler.SchedulerName);
            Assert.Contains("default-name-node", await new NCacheStorage(_cache!, options.KeyPrefix, QuartzSchedulerOptions.DefaultInstanceName).GetRegisteredSchedulerInstanceNamesAsync());
        }
        finally
        {
            await ShutdownQuietlyAsync(scheduler);
        }
    }

    [Fact]
    public async Task A_missing_scheduler_name_takes_the_named_registration_name()
    {
        var name = $"di-named-default-{Guid.NewGuid():N}";
        var options = Options("unused", "named-default-node");
        options.SchedulerName = null;

        var services = new ServiceCollection();
        services.AddQuartz(name, q => q.UseNCacheStore(options).UseDefaultThreadPool(1));

        await using var provider = services.BuildServiceProvider();
        var scheduler = await provider.GetRequiredKeyedService<ISchedulerFactory>(name).GetScheduler();

        try
        {
            await scheduler.Start();

            Assert.Equal(name, scheduler.SchedulerName);
            Assert.Contains("named-default-node", await StorageFor(name).GetRegisteredSchedulerInstanceNamesAsync());
        }
        finally
        {
            await ShutdownQuietlyAsync(scheduler);
        }
    }

    [Fact]
    public void The_string_overload_accepts_only_a_cache_name()
    {
        var services = new ServiceCollection();

        services.AddQuartz(q => q.UseNCacheStore("cache"));
    }

    [Fact]
    public void Options_overload_rejects_null_options()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() =>
            services.AddQuartz(q => q.UseNCacheStore((NCacheJobStoreOptions) null!)));
    }

    [Fact]
    public async Task CreateJobStore_applies_the_tuning_values()
    {
        var options = Options("di-tuning", "n1");
        options.MisfireThreshold = TimeSpan.FromSeconds(7);
        options.ClusterCheckinInterval = TimeSpan.FromSeconds(3);
        options.ClusterCheckinMisfireThreshold = TimeSpan.FromSeconds(4);
        options.LockAcquireTimeout = TimeSpan.FromSeconds(9);
        options.RepairRegistriesOnStart = false;

        var store = NCacheQuartzBuilderExtensions.CreateJobStore(_cache!, options);

        Assert.Equal(TimeSpan.FromSeconds(7), store.MisfireThreshold);
        Assert.Equal(TimeSpan.FromSeconds(3), store.ClusterCheckinInterval);
        Assert.Equal(TimeSpan.FromSeconds(4), store.ClusterCheckinMisfireThreshold);
        Assert.Equal(TimeSpan.FromSeconds(9), store.LockAcquireTimeout);
        Assert.False(store.RepairRegistriesOnStart);

        await store.Initialize(new SchedulerIdentity { SchedulerName = "di-tuning", InstanceId = "n1" });
        Assert.Equal("di-tuning", store.SchedulerName);
        Assert.Equal("n1", store.InstanceId);
        Assert.False(await store.Exists(new JobKey("nothing", "here")));
    }

    [Fact]
    public void CreateJobStore_leaves_the_defaults_when_no_tuning_value_is_set()
    {
        var store = NCacheQuartzBuilderExtensions.CreateJobStore(_cache!, new NCacheJobStoreOptions
        {
            CacheName = TestCache.CacheName,
            SchedulerName = "di-defaults",
            InstanceId = "n1",
            KeyPrefix = _prefix
        });

        Assert.Equal(TimeSpan.FromSeconds(60), store.MisfireThreshold);
        Assert.Equal(TimeSpan.FromSeconds(10), store.ClusterCheckinInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(7500), store.ClusterCheckinMisfireThreshold);
        Assert.Null(store.LockAcquireTimeout);
        Assert.True(store.RepairRegistriesOnStart);
    }

    [Fact]
    public void CreateJobStore_rejects_an_out_of_range_tuning_value()
    {
        var options = Options("di-invalid", "n1");
        options.ClusterCheckinInterval = TimeSpan.FromMilliseconds(10);

        var ex = Assert.Throws<ArgumentException>(() => NCacheQuartzBuilderExtensions.CreateJobStore(_cache!, options));
        Assert.Contains("ClusterCheckinInterval", ex.Message);
    }

    [Fact]
    public void CreateJobStore_runs_ConfigureSerializer_once_against_a_registry_of_its_own()
    {
        var calls = new List<SystemTextJsonSerializerRegistry>();
        var options = Options("di-serializer", "n1");
        options.ConfigureSerializer = registry => calls.Add(registry);

        var containerRegistry = new SystemTextJsonSerializerRegistry();
        NCacheQuartzBuilderExtensions.CreateJobStore(_cache!, options, null, null, containerRegistry);

        var registry = Assert.Single(calls);
        Assert.NotSame(containerRegistry, registry);
    }

    [Fact]
    public async Task Initialize_refuses_a_scheduler_name_other_than_the_one_the_store_was_built_for()
    {
        var store = new NCacheJobStore(
            StorageFor("sched-a"),
            new NextFireTimeIndex(_cache!, _prefix, "sched-a"),
            new NCacheDistributedLocks(_cache!, _prefix, "sched-a"));

        var ex = await Assert.ThrowsAsync<SchedulerConfigException>(async () =>
            await store.Initialize(new SchedulerIdentity { SchedulerName = "sched-b", InstanceId = "n1" }));

        Assert.Contains("sched-a", ex.Message);
        Assert.Contains("sched-b", ex.Message);
        Assert.Equal(string.Empty, store.SchedulerName);

        await store.Initialize(new SchedulerIdentity { SchedulerName = "sched-a", InstanceId = "n1" });
        Assert.Equal("sched-a", store.SchedulerName);
        Assert.Equal("n1", store.InstanceId);
    }

    [Fact]
    public void Constructor_rejects_missing_collaborators()
    {
        var storage = StorageFor("sched-null");
        var index = new NextFireTimeIndex(_cache!, _prefix, "sched-null");
        var locks = new NCacheDistributedLocks(_cache!, _prefix, "sched-null");

        Assert.Throws<ArgumentNullException>(() => new NCacheJobStore(null!, index, locks));
        Assert.Throws<ArgumentNullException>(() => new NCacheJobStore(storage, null!, locks));
        Assert.Throws<ArgumentNullException>(() => new NCacheJobStore(storage, index, null!));
    }

    public sealed class NoOpJob : IJob
    {
        public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class MisfireListener : ITriggerListener
    {
        public string Name => nameof(MisfireListener);

        public TaskCompletionSource<TriggerKey> Misfired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask TriggerMisfired(ITrigger trigger, IScheduler scheduler, CancellationToken cancellationToken = default)
        {
            Misfired.TrySetResult(trigger.Key);
            return default;
        }
    }
}

