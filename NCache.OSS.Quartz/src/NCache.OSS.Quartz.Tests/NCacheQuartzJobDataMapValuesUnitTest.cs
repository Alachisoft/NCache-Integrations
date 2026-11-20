using NCache.OSS.Quartz.Serialization;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzJobDataMapValuesUnitTest : NCacheQuartzScenarioTestBase
{
    private const string RepairLogText = "rebuilding registries and index";

    private sealed class CustomPayload
    {
        public int Value { get; set; } = 42;
    }

    public static IEnumerable<object[]> UnsupportedValues()
    {
        yield return new object[] { 7u };
        yield return new object[] { 7ul };
        yield return new object[] { (sbyte) 7 };
        yield return new object[] { new CustomPayload() };
        yield return new object[] { double.NaN };
        yield return new object[] { double.PositiveInfinity };
        yield return new object[] { float.NaN };
    }

    public static IEnumerable<object[]> SupportedValues()
    {
        yield return new object[] { "text" };
        yield return new object[] { (short) 3 };
        yield return new object[] { 42 };
        yield return new object[] { 42L };
        yield return new object[] { (byte) 7 };
        yield return new object[] { 1.5f };
        yield return new object[] { 2.25d };
        yield return new object[] { 12.5m };
        yield return new object[] { true };
        yield return new object[] { Guid.Parse("7f1d3c1e-9b2a-4a61-8d57-2c3b0e9f4a10") };
        yield return new object[] { TimeSpan.FromMinutes(90) };
        yield return new object[] { new DateTimeOffset(2026, 9, 23, 10, 30, 0, TimeSpan.FromHours(5)) };
    }

    private async Task<(NCacheJobStore Store, CapturingLoggerProvider Log)> StoreWithLogAsync()
    {
        var log = new CapturingLoggerProvider();
        var store = await NewStoreAsync(logger: log.CreateLogger<NCacheJobStore>());
        return (store, log);
    }

    [Theory]
    [MemberData(nameof(UnsupportedValues))]
    public async Task B6_AddJob_with_an_unsupported_value_throws_JobPersistenceException_writes_nothing_and_repairs_nothing(object value)
    {
        var (store, log) = await StoreWithLogAsync();
        var job = Job("job-1", data: new JobDataMap { ["bad"] = value });

        var ex = await Assert.ThrowsAsync<JobPersistenceException>(async () => await store.AddJob(job));

        Assert.Contains("'bad'", ex.Message, StringComparison.Ordinal);
        Assert.False(await store.Exists(job.Key));
        Assert.Null(await Storage.GetJobHeaderAsync("tests", "job-1"));
        Assert.False(log.Contains(RepairLogText));
    }

    [Fact]
    public async Task B6_ScheduleJob_with_an_unsupported_value_in_the_trigger_map_stores_neither_the_job_nor_the_trigger()
    {
        var (store, log) = await StoreWithLogAsync();
        var job = Job("job-1");
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddHours(1), new JobDataMap { ["day"] = 5u });

        await Assert.ThrowsAsync<JobPersistenceException>(async () => await store.ScheduleJob(job, trigger));

        Assert.False(await store.Exists(job.Key));
        Assert.False(await store.Exists(trigger.Key));
        Assert.False(log.Contains(RepairLogText));
    }

    [Fact]
    public async Task B6_AddTrigger_with_an_unsupported_value_is_refused_and_leaves_the_job_untouched()
    {
        var (store, log) = await StoreWithLogAsync();
        var job = Job("job-1");
        await store.AddJob(job);

        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddHours(1), new JobDataMap { ["c"] = (sbyte) 1 });

        await Assert.ThrowsAsync<JobPersistenceException>(async () => await store.AddTrigger(trigger));

        Assert.False(await store.Exists(trigger.Key));
        Assert.Empty(await store.GetTriggersForJob(job.Key));
        Assert.False(log.Contains(RepairLogText));
    }

    [Fact]
    public async Task B6_ScheduleJobs_with_one_bad_job_among_good_ones_stores_none_of_them()
    {
        var (store, log) = await StoreWithLogAsync();
        var good = Job("good");
        var bad = Job("bad", data: new JobDataMap { ["n"] = 5u });
        var later = DateTimeOffset.UtcNow.AddHours(1);

        var batch = new Dictionary<IJobDetail, IReadOnlyCollection<IOperableTrigger>>
        {
            [good] = new[] { OneShot("t-good", "tests", good.Key, later) },
            [bad] = new[] { OneShot("t-bad", "tests", bad.Key, later) }
        };

        await Assert.ThrowsAsync<JobPersistenceException>(async () => await store.ScheduleJobs(batch));

        Assert.False(await store.Exists(good.Key));
        Assert.False(await store.Exists(bad.Key));
        Assert.Empty((await store.QueryTriggers(new TriggerQuery())).Items);
        Assert.False(log.Contains(RepairLogText));
    }

    [Fact]
    public async Task B6_ReplaceTrigger_with_an_unsupported_value_keeps_the_trigger_it_would_have_replaced()
    {
        var (store, log) = await StoreWithLogAsync();
        var job = Job("job-1");
        var original = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddHours(1), new JobDataMap { ["v"] = 1 });
        await store.ScheduleJob(job, original);

        var replacement = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddHours(2), new JobDataMap { ["v"] = 9ul });

        await Assert.ThrowsAsync<JobPersistenceException>(async () => await store.ReplaceTrigger(original.Key, replacement));

        var stored = await store.GetTrigger(original.Key);
        Assert.NotNull(stored);
        Assert.Equal(1, stored!.JobDataMap["v"]);
        Assert.Equal(original.NextFireTimeUtc, stored.NextFireTimeUtc);
        Assert.False(log.Contains(RepairLogText));
    }

    [Fact]
    public async Task B6_replacing_a_job_with_an_unsupported_value_keeps_the_stored_job_data()
    {
        var (store, log) = await StoreWithLogAsync();
        await store.AddJob(Job("job-1", data: new JobDataMap { ["v"] = "kept" }));

        await Assert.ThrowsAsync<JobPersistenceException>(async () =>
            await store.AddJob(Job("job-1", data: new JobDataMap { ["v"] = new CustomPayload() }), new AddJobOptions { Replace = true }));

        Assert.Equal("kept", (await store.GetJob(new JobKey("job-1", "tests")))!.JobDataMap["v"]);
        Assert.False(log.Contains(RepairLogText));
    }

    [Fact]
    public async Task B6_the_store_keeps_working_after_refusing_an_unsupported_value()
    {
        var (store, _) = await StoreWithLogAsync();

        await Assert.ThrowsAsync<JobPersistenceException>(async () =>
            await store.AddJob(Job("job-1", data: new JobDataMap { ["day"] = 1u })));

        await store.AddJob(Job("job-1", data: new JobDataMap { ["day"] = (int) DayOfWeek.Monday }));

        Assert.Equal(1, (await store.GetJob(new JobKey("job-1", "tests")))!.JobDataMap["day"]);
    }

    [Fact]
    public async Task B6_a_run_that_puts_an_unsupported_value_in_a_persisted_map_still_completes_and_keeps_the_previous_data()
    {
        var (store, log) = await StoreWithLogAsync();
        var job = Job("job-1", data: new JobDataMap { ["count"] = 1 }, persistData: true);
        var trigger = Repeating("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);

        var bundle = await FireOneAsync(store);
        bundle.JobDetail.JobDataMap["count"] = 2;
        bundle.JobDetail.JobDataMap["day"] = 1u;

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);

        var stored = await store.GetJob(job.Key);
        Assert.Equal(1, stored!.JobDataMap["count"]);
        Assert.False(stored.JobDataMap.ContainsKey("day"));
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));
        Assert.Empty(await Storage.GetFiredTriggersForJobAsync("tests", "job-1"));
        Assert.True(log.Contains("cannot be persisted"));
        Assert.False(log.Contains(RepairLogText));
    }

    [Fact]
    public async Task B6_a_run_that_puts_only_supported_values_in_a_persisted_map_saves_them()
    {
        var (store, _) = await StoreWithLogAsync();
        var job = Job("job-1", data: new JobDataMap { ["count"] = 1 }, persistData: true);
        await store.ScheduleJob(job, Repeating("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1)));

        var bundle = await FireOneAsync(store);
        bundle.JobDetail.JobDataMap["count"] = 2;

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.Equal(2, (await store.GetJob(job.Key))!.JobDataMap["count"]);
    }

    [Theory]
    [MemberData(nameof(SupportedValues))]
    public async Task B6_every_supported_value_type_round_trips_with_its_type(object value)
    {
        var (store, log) = await StoreWithLogAsync();
        var job = Job("job-1", data: new JobDataMap { ["v"] = value });

        await store.AddJob(job);

        var back = (await store.GetJob(job.Key))!.JobDataMap["v"];
        Assert.Equal(value.GetType(), back!.GetType());
        Assert.Equal(value, back);
        Assert.False(log.Contains(RepairLogText));
    }

    [Fact]
    public async Task B10_a_job_data_entry_with_a_null_value_survives_a_store_and_read_round_trip()
    {
        var store = CreateStore("node-1");
        var map = new JobDataMap();
        map["optional"] = null!;
        map["x"] = 1;
        var job = Job("job-1", data: map);

        await store.AddJob(job);
        var back = (await store.GetJob(job.Key))!.JobDataMap;

        Assert.True(back.ContainsKey("optional"));
        Assert.Null(back["optional"]);
        Assert.Equal(1, back["x"]);
        Assert.Equal(2, back.Count);
    }

    [Fact]
    public async Task B10_a_map_holding_only_null_values_keeps_every_key()
    {
        var store = CreateStore("node-1");
        var map = new JobDataMap();
        map["a"] = null!;
        map["b"] = null!;

        await store.AddJob(Job("job-1", data: map));
        var back = (await store.GetJob(new JobKey("job-1", "tests")))!.JobDataMap;

        Assert.Equal(new[] { "a", "b" }, back.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(back.Keys, k => Assert.Null(back[k]));
    }

    [Fact]
    public async Task B10_a_trigger_data_entry_with_a_null_value_survives_too_and_reaches_the_fired_bundle()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1");
        var map = new JobDataMap();
        map["optional"] = null!;
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), map);
        await store.ScheduleJob(job, trigger);

        Assert.True((await store.GetTrigger(trigger.Key))!.JobDataMap.ContainsKey("optional"));

        var bundle = await FireOneAsync(store);
        Assert.True(bundle.Trigger.JobDataMap.ContainsKey("optional"));
        Assert.Null(bundle.Trigger.JobDataMap["optional"]);
    }

    [Fact]
    public async Task B10_a_run_that_sets_a_persisted_value_to_null_keeps_the_key_with_a_null_value()
    {
        var store = CreateStore("node-1");
        var job = Job("job-1", data: new JobDataMap { ["lastError"] = "timeout" }, persistData: true);
        await store.ScheduleJob(job, Repeating("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1)));

        var bundle = await FireOneAsync(store);
        bundle.JobDetail.JobDataMap["lastError"] = null!;

        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);

        var stored = (await store.GetJob(job.Key))!.JobDataMap;
        Assert.True(stored.ContainsKey("lastError"));
        Assert.Null(stored["lastError"]);
    }

    [Fact]
    public void B10_the_mapper_puts_null_values_back_into_the_map()
    {
        var map = JobDataMapMapper.ToJobDataMap(new Dictionary<string, object?> { ["optional"] = null, ["x"] = 1 });

        Assert.True(map.ContainsKey("optional"));
        Assert.Null(map["optional"]);
        Assert.Equal(1, map["x"]);
    }
}

