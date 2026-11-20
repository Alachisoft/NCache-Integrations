using System.Text.Json;
using System.Text.Json.Serialization;
using NCache.OSS.Quartz.Extensions;
using NCache.OSS.Quartz.Model;
using Quartz;
using Quartz.Extensibility;
using Quartz.Impl.Triggers;
using Quartz.Serialization.SystemTextJson;
using Quartz.Serialization.SystemTextJson.Triggers;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzCustomTypesUnitTest : NCacheQuartzScenarioTestBase
{
    public static IEnumerable<object[]> NewlySupportedValues()
    {
        yield return new object[] { 'x' };
        yield return new object[] { DayOfWeek.Friday };
        yield return new object[] { Shade.Dark };
        yield return new object[] { Flags.A | Flags.C };
        yield return new object[] { new DateOnly(2026, 9, 30) };
        yield return new object[] { new TimeOnly(13, 45, 10, 250) };
    }

    [Theory]
    [MemberData(nameof(NewlySupportedValues))]
    public async Task A_value_Quartz_accepts_round_trips_with_its_type(object value)
    {
        var store = await NewNodeAsync();
        await store.AddJob(Job("job", data: new JobDataMap { ["v"] = value }));

        var back = (await store.GetJob(new JobKey("job", "tests")))!.JobDataMap["v"];

        Assert.Equal(value.GetType(), back!.GetType());
        Assert.Equal(value, back);
    }

    [Fact]
    public async Task A_string_map_round_trips_as_a_copy()
    {
        var store = await NewNodeAsync();
        var map = new Dictionary<string, string> { ["region"] = "eu", ["tier"] = "gold", ["empty"] = "" };
        await store.AddJob(Job("job", data: new JobDataMap { ["meta"] = map }));

        map["region"] = "changed after storing";
        var back = Assert.IsType<Dictionary<string, string>>((await store.GetJob(new JobKey("job", "tests")))!.JobDataMap["meta"]);

        Assert.Equal("eu", back["region"]);
        Assert.Equal("gold", back["tier"]);
        Assert.Equal("", back["empty"]);
        Assert.Equal(3, back.Count);
    }

    [Fact]
    public async Task New_value_types_travel_in_the_trigger_map_and_reach_the_fired_bundle()
    {
        var store = await NewNodeAsync();
        var job = Job("job");
        var data = new JobDataMap { ["day"] = DayOfWeek.Tuesday, ["date"] = new DateOnly(2030, 1, 2), ["c"] = 'q' };
        await store.ScheduleJob(job, OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), data));

        var bundle = await FireOneAsync(store);

        Assert.Equal(DayOfWeek.Tuesday, bundle.Trigger.JobDataMap["day"]);
        Assert.Equal(new DateOnly(2030, 1, 2), bundle.Trigger.JobDataMap["date"]);
        Assert.Equal('q', bundle.Trigger.JobDataMap["c"]);
    }

    [Fact]
    public async Task A_persisted_map_can_take_new_value_types_at_completion()
    {
        var store = await NewNodeAsync();
        var job = Job("job", data: new JobDataMap { ["n"] = 1 }, persistData: true);
        await store.ScheduleJob(job, Repeating("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1)));

        var bundle = await FireOneAsync(store);
        bundle.JobDetail.JobDataMap["last"] = new TimeOnly(8, 30);
        bundle.JobDetail.JobDataMap["state"] = Shade.Light;
        await store.TriggeredJobComplete(bundle.Trigger, bundle.JobDetail, SchedulerInstruction.NoInstruction);

        var stored = (await store.GetJob(job.Key))!.JobDataMap;
        Assert.Equal(new TimeOnly(8, 30), stored["last"]);
        Assert.Equal(Shade.Light, stored["state"]);
    }

    public static IEnumerable<object[]> StillRefusedValues()
    {
        yield return new object[] { 7u };
        yield return new object[] { 7ul };
        yield return new object[] { (sbyte) 7 };
        yield return new object[] { new Payload { Name = "undeclared" } };
        yield return new object[] { new List<string> { "a" } };
        yield return new object[] { double.NaN };
        yield return new object[] { float.PositiveInfinity };
    }

    [Theory]
    [MemberData(nameof(StillRefusedValues))]
    public async Task A_value_Quartz_refuses_is_refused_and_nothing_is_stored(object value)
    {
        var store = await NewNodeAsync();

        var ex = await Assert.ThrowsAsync<JobPersistenceException>(() => store.AddJob(Job("job", data: new JobDataMap { ["bad"] = value })).AsTask());

        Assert.Contains("'bad'", ex.Message, StringComparison.Ordinal);
        Assert.False(await store.Exists(new JobKey("job", "tests")));
    }

    [Fact]
    public async Task A_refused_value_carries_Quartz_own_reason()
    {
        var store = await NewNodeAsync();

        var ex = await Assert.ThrowsAsync<JobPersistenceException>(() => store.AddJob(Job("job", data: new JobDataMap { ["bad"] = new Payload() })).AsTask());

        Assert.NotNull(ex.InnerException);
        Assert.Contains(ex.InnerException!.Message, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Supported:", ex.Message, StringComparison.Ordinal);
        Assert.False(await store.Exists(new JobKey("job", "tests")));
    }

    [Fact]
    public async Task A_type_declared_through_AddTypeInfoResolver_is_accepted_and_comes_back_as_itself()
    {
        var store = await StoreWithRegistryAsync(r => r.AddTypeInfoResolver(PayloadContext.Default));
        var payload = new FlatPayload { Name = "order-42", Count = 3 };

        await store.AddJob(Job("job", data: new JobDataMap { ["payload"] = payload }));

        var back = Assert.IsType<FlatPayload>((await store.GetJob(new JobKey("job", "tests")))!.JobDataMap["payload"]);
        Assert.Equal("order-42", back.Name);
        Assert.Equal(3, back.Count);
    }

    [Fact]
    public async Task A_declared_type_with_a_collection_member_is_refused_with_Quartz_reason()
    {
        var store = await StoreWithRegistryAsync(r => r.AddTypeInfoResolver(PayloadContext.Default));
        var payload = new Payload { Name = "order-42", Items = new List<int> { 1, 2, 3 } };

        var ex = await Assert.ThrowsAsync<JobPersistenceException>(() => store.AddJob(Job("job", data: new JobDataMap { ["payload"] = payload })).AsTask());

        Assert.Contains("collection", ex.Message, StringComparison.Ordinal);
        Assert.False(await store.Exists(new JobKey("job", "tests")));
    }

    [Fact]
    public async Task A_declared_type_is_refused_by_a_store_whose_registry_does_not_declare_it()
    {
        var declared = await StoreWithRegistryAsync(r => r.AddTypeInfoResolver(PayloadContext.Default));
        var undeclared = await NewNodeAsync("node-2");

        await declared.AddJob(Job("ok", data: new JobDataMap { ["p"] = new FlatPayload() }));

        await Assert.ThrowsAsync<JobPersistenceException>(() => undeclared.AddJob(Job("bad", data: new JobDataMap { ["p"] = new FlatPayload() })).AsTask());
        Assert.IsType<FlatPayload>((await undeclared.GetJob(new JobKey("ok", "tests")))!.JobDataMap["p"]);
    }

    [Fact]
    public async Task A_custom_trigger_with_a_registered_serializer_is_stored_listed_and_read_back_as_itself()
    {
        var store = await StoreWithRegistryAsync(r => r.AddTriggerSerializer<LabelledTrigger>(new LabelledTriggerSerializer()));
        var job = Job("job");
        var trigger = Labelled("t", job.Key, "nightly-export", DateTimeOffset.UtcNow.AddHours(1), new JobDataMap { ["n"] = (short) 3 });

        await store.ScheduleJob(job, trigger);

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal(NCacheTriggerKind.Custom, row.TriggerKind);
        Assert.Equal(TriggerStates.Waiting, row.TriggerState);
        Assert.NotNull(row.CustomTriggerJson);

        var back = Assert.IsType<LabelledTrigger>(await store.GetTrigger(trigger.Key));
        Assert.Equal("nightly-export", back.Label);
        Assert.Equal(trigger.NextFireTimeUtc, back.NextFireTimeUtc);
        Assert.Equal(TimeSpan.FromMinutes(5), back.RepeatInterval);
        Assert.Equal((short) 3, back.JobDataMap["n"]);
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));
        Assert.Equal("BLOB", Assert.Single((await store.QueryTriggers(new TriggerQuery())).Items).TriggerType);
    }

    [Fact]
    public async Task A_custom_trigger_fires_and_its_own_state_advances_with_every_firing()
    {
        var store = await StoreWithRegistryAsync(r => r.AddTriggerSerializer<LabelledTrigger>(new LabelledTriggerSerializer()));
        var job = Job("job");
        var trigger = Labelled("t", job.Key, "every-5m", DateTimeOffset.UtcNow.AddSeconds(-2));
        await store.ScheduleJob(job, trigger);

        var first = await FireOneAsync(store);

        var fired = Assert.IsType<LabelledTrigger>(first.Trigger);
        Assert.Equal("every-5m", fired.Label);
        Assert.Equal(1, fired.TimesTriggered);

        await store.TriggeredJobComplete(first.Trigger, first.JobDetail, SchedulerInstruction.NoInstruction);

        var stored = Assert.IsType<LabelledTrigger>(await store.GetTrigger(trigger.Key));
        Assert.Equal(1, stored.TimesTriggered);
        Assert.Equal(first.Trigger.NextFireTimeUtc, stored.NextFireTimeUtc);
        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
    }

    [Fact]
    public async Task A_custom_trigger_can_be_paused_resumed_and_rescheduled_in_place()
    {
        var store = await StoreWithRegistryAsync(r => r.AddTriggerSerializer<LabelledTrigger>(new LabelledTriggerSerializer()));
        var job = Job("job");
        var trigger = Labelled("t", job.Key, "first", DateTimeOffset.UtcNow.AddHours(1));
        await store.ScheduleJob(job, trigger);

        Assert.True(await store.PauseTriggerWith(trigger.Key, new PauseDetails { Reason = "hold" }));
        Assert.Equal("hold", (await store.GetTriggerPause(trigger.Key))!.Reason);
        Assert.True(await store.ResumeTrigger(trigger.Key));

        Assert.True(await store.ReplaceTrigger(trigger.Key, Labelled("t", job.Key, "second", DateTimeOffset.UtcNow.AddHours(2))));

        Assert.Equal("second", Assert.IsType<LabelledTrigger>(await store.GetTrigger(trigger.Key)).Label);
    }

    [Fact]
    public async Task A_custom_trigger_that_misfires_gets_its_own_misfire_handling()
    {
        var store = await StoreWithRegistryAsync(r => r.AddTriggerSerializer<LabelledTrigger>(new LabelledTriggerSerializer()));
        var job = Job("job");
        var trigger = Labelled("t", job.Key, "late", DateTimeOffset.UtcNow.AddHours(-2));
        await store.ScheduleJob(job, trigger);

        await AcquireDueAsync(store, maxCount: 5);

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.True(row.NextFireTimeUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal("late", Assert.IsType<LabelledTrigger>(await store.GetTrigger(trigger.Key)).Label);
    }

    [Fact]
    public async Task A_custom_trigger_without_a_registered_serializer_is_refused_and_nothing_is_stored()
    {
        var store = await NewNodeAsync();
        var job = Job("job");

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            store.ScheduleJob(job, Labelled("t", job.Key, "x", DateTimeOffset.UtcNow.AddHours(1))).AsTask());

        Assert.Contains("AddTriggerSerializer", ex.Message, StringComparison.Ordinal);
        Assert.False(await store.Exists(job.Key));
        Assert.False(await store.Exists(new TriggerKey("t", "tests")));
    }

    [Fact]
    public async Task A_node_without_the_serializer_cannot_read_the_custom_trigger_and_says_so()
    {
        var withSerializer = await StoreWithRegistryAsync(r => r.AddTriggerSerializer<LabelledTrigger>(new LabelledTriggerSerializer()));
        var without = await NewNodeAsync("node-2");
        var job = Job("job");
        await withSerializer.ScheduleJob(job, Labelled("t", job.Key, "x", DateTimeOffset.UtcNow.AddHours(1)));

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => without.GetTrigger(new TriggerKey("t", "tests")).AsTask());

        Assert.Contains("every node", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_subclass_of_a_built_in_trigger_is_stored_as_custom_so_it_keeps_its_type()
    {
        var store = await StoreWithRegistryAsync(r => r.AddTriggerSerializer<LabelledTrigger>(new LabelledTriggerSerializer()));
        var job = Job("job");
        await store.ScheduleJob(job, Labelled("t", job.Key, "sub", DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Equal(NCacheTriggerKind.Custom, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerKind);
        Assert.Null((await Storage.GetTriggerAsync("tests", "t"))!.Simple);
    }

    private async Task<NCacheJobStore> StoreWithRegistryAsync(Action<SystemTextJsonSerializerRegistry> configure, string instanceId = "node-1")
    {
        var options = ScenarioKit.Options(_prefix, SchedulerName, instanceId);
        options.ConfigureSerializer = configure;
        var store = NCacheQuartzBuilderExtensions.CreateJobStore(_cache!, options);
        await store.Initialize(new SchedulerIdentity { SchedulerName = SchedulerName, InstanceId = instanceId });
        await CheckInAsync(instanceId, DateTimeOffset.UtcNow);
        return store;
    }

    private static LabelledTrigger Labelled(string name, JobKey job, string label, DateTimeOffset start, JobDataMap? data = null)
    {
        var trigger = new LabelledTrigger
        {
            Key = new TriggerKey(name, "tests"),
            JobKey = job,
            Label = label,
            StartTimeUtc = start,
            RepeatInterval = TimeSpan.FromMinutes(5),
            RepeatCount = SimpleTriggerImpl.RepeatIndefinitely
        };

        if (data is not null)
        {
            trigger.JobDataMap = data;
        }

        trigger.ComputeFirstFireTimeUtc(null);
        return trigger;
    }

    public enum Shade { Light = 1, Dark = 2 }

    [Flags]
    public enum Flags { A = 1, B = 2, C = 4 }

    public sealed class Payload
    {
        public string Name { get; set; } = "";
        public List<int> Items { get; set; } = new();
    }

    public sealed class FlatPayload
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }
}

[JsonSerializable(typeof(NCacheQuartzCustomTypesUnitTest.Payload))]
[JsonSerializable(typeof(NCacheQuartzCustomTypesUnitTest.FlatPayload))]
internal sealed partial class PayloadContext : JsonSerializerContext
{
}

public sealed class LabelledTrigger : SimpleTriggerImpl
{
    public string Label { get; set; } = "";
}

public sealed class LabelledTriggerSerializer : TriggerSerializer<LabelledTrigger>
{
    public override string TriggerTypeName => "LabelledTrigger";

    public override IScheduleBuilder CreateScheduleBuilder(JsonElement jsonElement, JsonSerializerOptions options)
        => new Builder(
            jsonElement.GetProperty("RepeatCount").GetInt32(),
            TimeSpan.FromTicks(jsonElement.GetProperty("RepeatIntervalTicks").GetInt64()));

    protected override void SerializeFields(Utf8JsonWriter writer, LabelledTrigger trigger, JsonSerializerOptions options)
    {
        writer.WriteNumber("RepeatCount", trigger.RepeatCount);
        writer.WriteNumber("RepeatIntervalTicks", trigger.RepeatInterval.Ticks);
        writer.WriteNumber("TimesTriggered", trigger.TimesTriggered);
        writer.WriteString("Label", trigger.Label);
    }

    protected override void DeserializeFields(LabelledTrigger trigger, JsonElement jsonElement, JsonSerializerOptions options)
    {
        trigger.TimesTriggered = jsonElement.GetProperty("TimesTriggered").GetInt32();
        trigger.Label = jsonElement.GetProperty("Label").GetString() ?? "";
    }

    private sealed class Builder(int repeatCount, TimeSpan interval) : IScheduleBuilder
    {
        public IMutableTrigger Build() => new LabelledTrigger { RepeatCount = repeatCount, RepeatInterval = interval };
    }
}
