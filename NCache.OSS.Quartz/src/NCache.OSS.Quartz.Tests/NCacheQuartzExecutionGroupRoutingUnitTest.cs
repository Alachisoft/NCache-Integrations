using System.Collections.Concurrent;
using NCache.OSS.Quartz.Extensions;
using NCache.OSS.Quartz.Model;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzExecutionGroupRoutingUnitTest : NCacheQuartzScenarioTestBase
{
    [Fact]
    public async Task A_served_list_acquires_only_its_groups_and_not_ungrouped_triggers_unless_listed()
    {
        var store = await NewNodeAsync();
        await ScheduleDueAsync(store, "reports-t", "reports");
        await ScheduleDueAsync(store, "emails-t", "emails");
        await ScheduleDueAsync(store, "plain-t", executionGroup: null);

        var acquired = await store.AcquireNextTriggers(Request(served: new[] { ExecutionGroupScope.Named("reports") }));

        Assert.Equal(new[] { "reports-t" }, acquired.Select(t => t.Key.Name));
    }

    [Fact]
    public async Task A_served_list_with_the_default_bucket_acquires_ungrouped_triggers_too()
    {
        var store = await NewNodeAsync();
        await ScheduleDueAsync(store, "reports-t", "reports");
        await ScheduleDueAsync(store, "emails-t", "emails");
        await ScheduleDueAsync(store, "plain-t", executionGroup: null);

        var acquired = await store.AcquireNextTriggers(Request(served: new[] { ExecutionGroupScope.Named("reports"), ExecutionGroupScope.Default }));

        Assert.Equal(new[] { "plain-t", "reports-t" }, acquired.Select(t => t.Key.Name).Order());
    }

    [Fact]
    public async Task A_refused_list_acquires_every_group_but_the_refused_ones()
    {
        var store = await NewNodeAsync();
        await ScheduleDueAsync(store, "reports-t", "reports");
        await ScheduleDueAsync(store, "emails-t", "emails");
        await ScheduleDueAsync(store, "plain-t", executionGroup: null);

        var acquired = await store.AcquireNextTriggers(Request(refused: new[] { ExecutionGroupScope.Named("reports") }));

        Assert.Equal(new[] { "emails-t", "plain-t" }, acquired.Select(t => t.Key.Name).Order());
    }

    [Fact]
    public async Task Refusing_the_default_bucket_leaves_ungrouped_triggers_alone()
    {
        var store = await NewNodeAsync();
        await ScheduleDueAsync(store, "reports-t", "reports");
        await ScheduleDueAsync(store, "plain-t", executionGroup: null);

        var acquired = await store.AcquireNextTriggers(Request(refused: new[] { ExecutionGroupScope.Default }));

        Assert.Equal(new[] { "reports-t" }, acquired.Select(t => t.Key.Name));
    }

    [Fact]
    public async Task Without_either_list_every_group_is_acquired_as_before()
    {
        var store = await NewNodeAsync();
        await ScheduleDueAsync(store, "reports-t", "reports");
        await ScheduleDueAsync(store, "plain-t", executionGroup: null);

        var acquired = await store.AcquireNextTriggers(Request());

        Assert.Equal(new[] { "plain-t", "reports-t" }, acquired.Select(t => t.Key.Name).Order());
    }

    [Fact]
    public async Task A_declined_overdue_trigger_is_not_misfire_handled_and_waits_unmodified()
    {
        var store = await NewNodeAsync();
        store.MisfireThreshold = TimeSpan.FromSeconds(1);
        var dueAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var job = Job("job");
        var trigger = (IOperableTrigger) TriggerBuilder.Create()
            .WithIdentity("late", "tests")
            .ForJob(job.Key)
            .StartAt(dueAt)
            .WithExecutionGroup("reports")
            .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromHours(1)).RepeatForever().WithMisfireInstruction(SimpleTriggerMisfireInstruction.NextWithRemainingCount))
            .Build();
        trigger.ComputeFirstFireTimeUtc(null);
        await store.ScheduleJob(job, trigger);
        var before = (await Storage.GetTriggerAsync("tests", "late"))!;

        Assert.Empty(await store.AcquireNextTriggers(Request(refused: new[] { ExecutionGroupScope.Named("reports") })));

        var after = (await Storage.GetTriggerAsync("tests", "late"))!;
        Assert.Equal(TriggerStates.Waiting, after.TriggerState);
        Assert.Equal(before.NextFireTimeUtc, after.NextFireTimeUtc);
        Assert.Equal(before.StartTimeUtc, after.StartTimeUtc);

        Assert.Empty(await store.AcquireNextTriggers(Request()));
        Assert.True((await Storage.GetTriggerAsync("tests", "late"))!.NextFireTimeUtc > DateTimeOffset.UtcNow.AddMinutes(50));
    }

    [Fact]
    public async Task A_node_that_refuses_a_group_leaves_it_for_a_node_that_serves_it()
    {
        var refusing = await NewNodeAsync("node-refusing");
        var serving = await NewNodeAsync("node-serving");
        await ScheduleDueAsync(refusing, "reports-t", "reports");

        Assert.Empty(await refusing.AcquireNextTriggers(Request(refused: new[] { ExecutionGroupScope.Named("reports") })));

        var acquired = Assert.Single(await serving.AcquireNextTriggers(Request()));
        Assert.Equal("reports-t", acquired.Key.Name);
    }

    [Fact]
    public async Task A_served_trigger_behind_a_run_of_refused_ones_is_still_acquired()
    {
        var store = await NewNodeAsync();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 30; i++)
        {
            await ScheduleDueAsync(store, $"refused-{i:D2}", "reports", now.AddSeconds(-20).AddMilliseconds(i * 100));
        }

        await ScheduleDueAsync(store, "served-t", "emails", now.AddSeconds(-1));

        var acquired = Assert.Single(await store.AcquireNextTriggers(Request(refused: new[] { ExecutionGroupScope.Named("reports") })));

        Assert.Equal("served-t", acquired.Key.Name);
    }

    [Fact]
    public async Task A_scheduler_configured_to_refuse_a_group_never_runs_its_triggers()
    {
        var instanceId = $"routing-{Guid.NewGuid():N}";
        var ran = RecordingJob.Start(instanceId);
        var scheduler = await QuartzSchedulerBuilder
            .Create(q => q
                .UseNCacheStore(ScenarioKit.Options(_prefix, SchedulerName, instanceId))
                .UseDefaultThreadPool(2)
                .RefuseExecutionGroups("reports"))
            .BuildScheduler();

        try
        {
            await scheduler.Start();

            var job = JobBuilder.Create<RecordingJob>().WithIdentity("recorder", "tests").UsingJobData(RecordingJob.RunKey, instanceId).StoreDurably().Build();
            await scheduler.AddJob(job, new AddJobOptions { Replace = true });
            await scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity("reports-t", "tests").ForJob(job.Key).WithExecutionGroup("reports").StartNow().Build());
            await scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity("emails-t", "tests").ForJob(job.Key).WithExecutionGroup("emails").StartNow().Build());

            Assert.True(await ScenarioKit.WaitUntilAsync(() => Task.FromResult(ran.Contains("emails-t")), TimeSpan.FromSeconds(15)), "the served trigger never ran");
            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.DoesNotContain("reports-t", ran);
            Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState(new TriggerKey("reports-t", "tests")));
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: true);
        }
    }

    // Stores a one-shot trigger due at the given time (a second ago by default) in the given execution group.
    private static async Task ScheduleDueAsync(NCacheJobStore store, string name, string? executionGroup, DateTimeOffset? dueAt = null)
    {
        var job = Job($"{name}-job");
        var builder = TriggerBuilder.Create()
            .WithIdentity(name, "tests")
            .ForJob(job.Key)
            .StartAt(dueAt ?? DateTimeOffset.UtcNow.AddSeconds(-1))
            .WithSimpleSchedule(s => s.WithRepeatCount(0));

        if (executionGroup is not null)
        {
            builder = builder.WithExecutionGroup(executionGroup);
        }

        var trigger = (IOperableTrigger) builder.Build();
        trigger.ComputeFirstFireTimeUtc(null);
        await store.ScheduleJob(job, trigger);
    }

    // An acquisition request for everything due now, with an optional served or refused list.
    private static TriggerAcquisitionRequest Request(IReadOnlyCollection<ExecutionGroupScope>? served = null, IReadOnlyCollection<ExecutionGroupScope>? refused = null) => new()
    {
        NoLaterThan = DateTimeOffset.UtcNow.AddSeconds(5),
        MaxCount = 10,
        TimeWindow = TimeSpan.Zero,
        ServedExecutionGroups = served,
        RefusedExecutionGroups = refused
    };

    public sealed class RecordingJob : IJob
    {
        public const string RunKey = "run";

        private static readonly ConcurrentDictionary<string, ConcurrentBag<string>> Runs = new();

        // Starts a fresh record of which triggers ran for one test's scheduler.
        public static ConcurrentBag<string> Start(string run) => Runs[run] = new ConcurrentBag<string>();

        // Records the trigger that fired this run under the test's record.
        public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
        {
            Runs[context.MergedJobDataMap.GetString(RunKey)!].Add(context.Trigger.Key.Name);
            return ValueTask.CompletedTask;
        }
    }
}
