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
public sealed class NCacheQuartzRetryUnitTest : NCacheQuartzStoreTestBase
{
    private static IOperableTrigger CreateRetryingTrigger(JobKey jobKey, RetryPolicy? policy, int repeatCount = 5, DateTimeOffset? startAt = null)
    {
        var builder = TriggerBuilder.Create()
            .WithIdentity("trigger-1", "tests")
            .ForJob(jobKey)
            .StartAt(startAt ?? DateTimeOffset.UtcNow.AddSeconds(-1))
            .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromMinutes(10)).WithRepeatCount(repeatCount));

        if (policy is not null)
        {
            builder = builder.WithRetryPolicy(policy);
        }

        var trigger = (IOperableTrigger)builder.Build();
        trigger.ComputeFirstFireTimeUtc(null);
        return trigger;
    }

    private static async Task<(IOperableTrigger Trigger, SchedulerInstruction Instruction)> FireOnceAsync(NCacheJobStore store, IJobDetail job, Exception? failure)
    {
        var acquired = Assert.Single(await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 1)));
        var fired = Assert.Single(await store.TriggersFired(new[] { acquired }));
        Assert.NotNull(fired.TriggerFiredBundle);

        var instruction = acquired.ExecutionComplete(new FakeExecutionContext(), failure is null ? null : new JobExecutionException(failure));
        await store.TriggeredJobComplete(acquired, job, instruction);

        return (acquired, instruction);
    }

    [Fact]
    public async Task TriggeredJobComplete_RetryTrigger_persists_the_retry_instant_and_attempt_carried_by_the_trigger()
    {
        var store = CreateStore("instance-1");
        var job = JobBuilder.Create<TestJob>().WithIdentity("job-1", "tests").StoreDurably().Build();
        await store.AddJob(job);

        var trigger = CreateRetryingTrigger(job.Key, RetryPolicy.Fixed(3, TimeSpan.FromSeconds(30)));
        await store.AddTrigger(trigger);

        var (fired, instruction) = await FireOnceAsync(store, job, new InvalidOperationException("boom"));

        Assert.Equal(SchedulerInstruction.RetryTrigger, instruction);
        Assert.Equal(1, fired.RetryAttempt);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(1, stored.RetryAttempt);
        Assert.Equal(fired.NextFireTimeUtc, stored.NextFireTimeUtc);
        Assert.InRange(stored.NextFireTimeUtc!.Value, DateTimeOffset.UtcNow.AddSeconds(25), DateTimeOffset.UtcNow.AddSeconds(31));
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
        Assert.Contains(await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10), x => x.TriggerName == "trigger-1");
        Assert.Null(await Storage.GetFiredTriggerAsync(fired.FireInstanceId!));
    }

    [Fact]
    public async Task TriggeredJobComplete_RetryTrigger_leaves_trigger_paused_when_its_group_is_paused()
    {
        var store = CreateStore("instance-1");
        var job = JobBuilder.Create<TestJob>().WithIdentity("job-1", "tests").StoreDurably().Build();
        await store.AddJob(job);

        var trigger = CreateRetryingTrigger(job.Key, RetryPolicy.Fixed(3, TimeSpan.FromSeconds(30)));
        await store.AddTrigger(trigger);

        var acquired = Assert.Single(await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 1)));
        Assert.NotNull(Assert.Single(await store.TriggersFired(new[] { acquired })).TriggerFiredBundle);

        await Storage.AddPausedTriggerGroupAsync("tests");

        var instruction = acquired.ExecutionComplete(new FakeExecutionContext(), new JobExecutionException("boom"));
        Assert.Equal(SchedulerInstruction.RetryTrigger, instruction);
        await store.TriggeredJobComplete(acquired, job, instruction);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Paused, stored.TriggerState);
        Assert.Equal(1, stored.RetryAttempt);
        Assert.DoesNotContain(await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10), x => x.TriggerName == "trigger-1");
    }

    [Fact]
    public async Task TriggersFired_fires_a_retry_with_RetryFired_and_does_not_burn_a_repeat_count()
    {
        var store = CreateStore("instance-1");
        var job = JobBuilder.Create<TestJob>().WithIdentity("job-1", "tests").StoreDurably().Build();
        await store.AddJob(job);

        var trigger = CreateRetryingTrigger(job.Key, RetryPolicy.Fixed(3, TimeSpan.FromMilliseconds(10)), repeatCount: 2);
        await store.AddTrigger(trigger);

        var (first, _) = await FireOnceAsync(store, job, new InvalidOperationException("boom"));
        var originalOccurrence = first.PreviousFireTimeUtc;
        var afterFirst = (await Storage.GetTriggerAsync("tests", "trigger-1"))!;
        Assert.Equal(1, afterFirst.Simple!.TimesTriggered);

        await Task.Delay(20);

        var retry = Assert.Single(await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 1)));
        Assert.Equal(1, retry.RetryAttempt);

        var fired = Assert.Single(await store.TriggersFired(new[] { retry }));
        Assert.NotNull(fired.TriggerFiredBundle);
        Assert.Equal(originalOccurrence, fired.TriggerFiredBundle!.ScheduledFireTimeUtc);

        var stored = (await Storage.GetTriggerAsync("tests", "trigger-1"))!;
        Assert.Equal(1, stored.Simple!.TimesTriggered);
        Assert.Equal(originalOccurrence, stored.PreviousFireTimeUtc);
        Assert.Equal(1, stored.RetryAttempt);
        Assert.True(stored.NextFireTimeUtc > DateTimeOffset.UtcNow.AddMinutes(9));

        var instruction = retry.ExecutionComplete(new FakeExecutionContext(), null);
        Assert.Equal(SchedulerInstruction.NoInstruction, instruction);
        Assert.Equal(0, retry.RetryAttempt);
        await store.TriggeredJobComplete(retry, job, instruction);

        Assert.Equal(0, (await Storage.GetTriggerAsync("tests", "trigger-1"))!.RetryAttempt);
    }

    [Fact]
    public async Task Retry_flow_enforces_max_attempts_end_to_end()
    {
        var store = CreateStore("instance-1");
        var job = JobBuilder.Create<TestJob>().WithIdentity("job-1", "tests").StoreDurably().Build();
        await store.AddJob(job);

        var trigger = CreateRetryingTrigger(job.Key, RetryPolicy.Fixed(2, TimeSpan.FromMilliseconds(10)));
        await store.AddTrigger(trigger);

        var attemptsSeen = new List<int>();

        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(20);
            var acquired = Assert.Single(await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 1)));
            attemptsSeen.Add(acquired.RetryAttempt);
            Assert.NotNull(Assert.Single(await store.TriggersFired(new[] { acquired })).TriggerFiredBundle);

            var instruction = acquired.ExecutionComplete(new FakeExecutionContext(), new JobExecutionException("boom"));
            await store.TriggeredJobComplete(acquired, job, instruction);

            var stored = (await Storage.GetTriggerAsync("tests", "trigger-1"))!;

            if (i < 2)
            {
                Assert.Equal(SchedulerInstruction.RetryTrigger, instruction);
                Assert.Equal(i + 1, stored.RetryAttempt);
            }
            else
            {
                Assert.Equal(SchedulerInstruction.NoInstruction, instruction);
                Assert.Equal(0, stored.RetryAttempt);
                Assert.True(stored.NextFireTimeUtc > DateTimeOffset.UtcNow.AddMinutes(9));
            }
        }

        Assert.Equal(new[] { 0, 1, 2 }, attemptsSeen);
        Assert.Empty(await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1)));
    }

    [Fact]
    public async Task TriggeredJobComplete_NoInstruction_persists_a_cleared_retry_attempt()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(5));
        storedTrigger.RetryAttempt = 2;

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = ToQuartzTrigger(storedTrigger);
        trigger.RetryAttempt = 0;

        await store.TriggeredJobComplete(trigger, Mapping.JobMapper.ToQuartzJob(job), SchedulerInstruction.NoInstruction);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(0, stored.RetryAttempt);
        Assert.Equal(TriggerStates.Acquired, stored.TriggerState);
    }

    [Fact]
    public async Task TriggeredJobComplete_NoInstruction_writes_nothing_when_retry_attempt_is_unchanged()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var storedTrigger = CreateAcquiredTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(5));
        storedTrigger.Description = "as stored";

        await StoreJobAndTriggerAsync(job, storedTrigger, addToIndex: false);

        var trigger = ToQuartzTrigger(storedTrigger);
        trigger.Description = "changed in memory only";

        await store.TriggeredJobComplete(trigger, Mapping.JobMapper.ToQuartzJob(job), SchedulerInstruction.NoInstruction);

        Assert.Equal("as stored", (await Storage.GetTriggerAsync("tests", "trigger-1"))!.Description);
    }

    [Fact]
    public async Task Misfire_resets_a_pending_retry_attempt()
    {
        var store = CreateStore("instance-1");
        store.MisfireThreshold = TimeSpan.FromSeconds(1);

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-10), repeatCount: -1);
        trigger.RetryAttempt = 2;
        trigger.MisfireInstruction = (int)SimpleTriggerMisfireInstruction.NextWithRemainingCount;

        await StoreJobAndTriggerAsync(job, trigger);

        await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 1));

        Assert.Equal(0, (await Storage.GetTriggerAsync("tests", "trigger-1"))!.RetryAttempt);
    }

    private sealed class FakeExecutionContext : IJobExecutionContext
    {
        public IScheduler Scheduler => throw new NotSupportedException();
        public ITrigger Trigger => throw new NotSupportedException();
        public ICalendar? Calendar => null;
        public bool Recovering => false;
        public TriggerKey RecoveringTriggerKey => throw new NotSupportedException();
        public int RefireCount => 0;
        public int RetryAttempt => 0;
        public JobDataMap MergedJobDataMap => new();
        public IJobDetail JobDetail => throw new NotSupportedException();
        public IJob JobInstance => throw new NotSupportedException();
        public DateTimeOffset FireTimeUtc => DateTimeOffset.UtcNow;
        public DateTimeOffset? ScheduledFireTimeUtc => null;
        public DateTimeOffset? PreviousFireTimeUtc => null;
        public DateTimeOffset? NextFireTimeUtc => null;
        public string FireInstanceId => "fake";
        public object? Result { get; set; }
        public TimeSpan JobRunTime => TimeSpan.Zero;
        public void Put(object key, object objectValue) { }
        public object? Get(object key) => null;
        public CancellationToken CancellationToken => default;
    }
}

