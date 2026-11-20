using System.Collections.Concurrent;
using NCache.OSS.Quartz.Model;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzFiringFailureUnitTest : NCacheQuartzScenarioTestBase
{
    private const string MissingJobClass = "Removed.Namespace.ReportJob, Removed.Assembly";

    private async Task BreakJobClassAsync(string group, string name)
    {
        var row = (await Storage.GetJobAsync(group, name))!;
        row.JobClassName = MissingJobClass;
        await Storage.UpdateJobAsync(row);
    }

    private async Task RestoreJobClassAsync(string group, string name)
    {
        var row = (await Storage.GetJobAsync(group, name))!;
        row.JobClassName = typeof(TestJob).AssemblyQualifiedName!;
        await Storage.UpdateJobAsync(row);
    }

    private sealed class RecordingSignaler : ISchedulerSignaler
    {
        public ConcurrentQueue<TriggerKey> TriggersInError { get; } = new();

        public ValueTask NotifyTriggerListenersMisfired(ITrigger trigger, CancellationToken cancellationToken = default) => default;

        public ValueTask NotifySchedulerListenersFinalized(ITrigger trigger, CancellationToken cancellationToken = default) => default;

        public ValueTask NotifySchedulerListenersJobDeleted(JobKey jobKey, CancellationToken cancellationToken = default) => default;

        public ValueTask NotifySchedulerListenersTriggerInError(TriggerKey triggerKey, CancellationToken cancellationToken = default)
        {
            TriggersInError.Enqueue(triggerKey);
            return default;
        }

        public ValueTask NotifySchedulerListenersTriggersInError(JobKey jobKey, CancellationToken cancellationToken = default) => default;

        public ValueTask SignalSchedulingChange(DateTimeOffset? candidateNewNextFireTimeUtc, CancellationToken cancellationToken = default) => default;

        public ValueTask NotifySchedulerListenersError(SchedulerErrorContext errorContext, CancellationToken cancellationToken = default) => default;
    }

    [Fact]
    public async Task B4_an_unloadable_job_class_fails_its_own_trigger_and_the_rest_of_the_batch_fires()
    {
        var store = CreateStore("node-1");
        var at = DateTimeOffset.UtcNow.AddSeconds(-1);
        var bad = Job("bad");
        var good = Job("good");
        await store.ScheduleJob(bad, OneShot("tbad", "tests", bad.Key, at));
        await store.ScheduleJob(good, OneShot("tgood", "tests", good.Key, at));
        await BreakJobClassAsync("tests", "bad");

        var acquired = await AcquireDueAsync(store, 2);
        Assert.Equal(2, acquired.Count);

        var results = await store.TriggersFired(acquired);

        for (var i = 0; i < acquired.Count; i++)
        {
            if (acquired[i].Key.Name == "tbad")
            {
                Assert.Null(results[i].TriggerFiredBundle);
                Assert.IsAssignableFrom<JobPersistenceException>(results[i].Exception);
            }
            else
            {
                Assert.NotNull(results[i].TriggerFiredBundle);
                Assert.Null(results[i].Exception);
                Assert.Equal(good.Key, results[i].TriggerFiredBundle!.JobDetail.Key);
            }
        }

        Assert.Equal(TriggerState.Error, await store.GetTriggerState(new TriggerKey("tbad", "tests")));
    }

    [Fact]
    public async Task B4_the_failed_trigger_is_released_as_Quartz_does_and_is_never_acquired_again()
    {
        var store = CreateStore("node-1");
        var bad = Job("bad");
        var trigger = Repeating("tbad", "tests", bad.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromSeconds(1));
        await store.ScheduleJob(bad, trigger);
        await BreakJobClassAsync("tests", "bad");

        var acquired = Assert.Single(await AcquireDueAsync(store));
        var result = Assert.Single(await store.TriggersFired(new[] { acquired }));
        Assert.NotNull(result.Exception);

        await store.ReleaseAcquiredTrigger(acquired);

        Assert.Equal(TriggerState.Error, await store.GetTriggerState(trigger.Key));
        Assert.Empty(await Storage.GetFiredTriggersForInstanceAsync("node-1"));
        Assert.Empty(await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddSeconds(30), 5)));
    }

    [Fact]
    public async Task B4_the_scheduler_listeners_are_told_the_trigger_is_in_error()
    {
        var signaler = new RecordingSignaler();
        var store = await NewStoreAsync(signaler: signaler);
        var bad = Job("bad");
        await store.ScheduleJob(bad, OneShot("tbad", "tests", bad.Key, DateTimeOffset.UtcNow.AddSeconds(-1)));
        await BreakJobClassAsync("tests", "bad");

        await store.TriggersFired(await AcquireDueAsync(store));

        Assert.Equal(new TriggerKey("tbad", "tests"), Assert.Single(signaler.TriggersInError));
    }

    [Fact]
    public async Task B4_once_the_class_loads_again_ResetTriggerFromErrorState_makes_the_trigger_fire()
    {
        var store = CreateStore("node-1");
        var bad = Job("bad");
        var trigger = OneShot("tbad", "tests", bad.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(bad, trigger);
        await BreakJobClassAsync("tests", "bad");

        var acquired = Assert.Single(await AcquireDueAsync(store));
        await store.TriggersFired(new[] { acquired });
        await store.ReleaseAcquiredTrigger(acquired);
        Assert.Equal(TriggerState.Error, await store.GetTriggerState(trigger.Key));

        await RestoreJobClassAsync("tests", "bad");
        Assert.True(await store.ResetTriggerFromErrorState(trigger.Key));
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));

        var bundle = await FireOneAsync(store);
        Assert.Equal(bad.Key, bundle.JobDetail.Key);
    }

    [Fact]
    public async Task B4_a_batch_of_healthy_triggers_is_unaffected()
    {
        var signaler = new RecordingSignaler();
        var store = await NewStoreAsync(signaler: signaler);
        var at = DateTimeOffset.UtcNow.AddSeconds(-1);

        for (var i = 0; i < 3; i++)
        {
            var job = Job($"job-{i}");
            await store.ScheduleJob(job, OneShot($"t-{i}", "tests", job.Key, at));
        }

        var acquired = await AcquireDueAsync(store, 3);
        var results = await store.TriggersFired(acquired);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.NotNull(r.TriggerFiredBundle));
        Assert.All(results, r => Assert.Null(r.Exception));
        Assert.Empty(signaler.TriggersInError);
    }

    private async Task AssertReleasedAndAcquirableByAnotherNodeAsync(TriggerKey key)
    {
        var row = await Storage.GetTriggerAsync(key.Group, key.Name);
        Assert.Equal(TriggerStates.Waiting, row!.TriggerState);
        Assert.Empty(await Storage.GetFiredTriggersForInstanceAsync("node-1"));

        var other = await NewStoreAsync("node-2");
        var acquired = Assert.Single(await AcquireDueAsync(other));
        Assert.Equal(key, acquired.Key);
        await other.ReleaseAcquiredTrigger(acquired);
    }

    [Fact]
    public async Task B9_an_acquisition_that_loses_its_lock_after_its_bulk_write_releases_what_it_wrote()
    {
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewStoreAsync(cache: cache, lockExpiration: TimeSpan.FromSeconds(1));
        var job = Job("job-1");
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        var armed = 1;
        control.DelayAfter = (method, _) =>
        {
            if (method.Name != "InsertBulk" || Interlocked.Exchange(ref armed, 0) != 1)
            {
                return null;
            }

            _cache!.Remove(TriggerAccessLockKey);
            return TimeSpan.FromSeconds(2);
        };

        var ex = await Assert.ThrowsAsync<JobPersistenceException>(async () => await AcquireDueAsync(store));
        control.DelayAfter = null;

        Assert.Contains("lock expired", ex.Message, StringComparison.OrdinalIgnoreCase);

        await AssertReleasedAndAcquirableByAnotherNodeAsync(trigger.Key);
    }

    [Fact]
    public async Task B9_an_acquisition_whose_bulk_write_fails_outright_is_released_and_the_trigger_can_be_acquired_again()
    {
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewStoreAsync(cache: cache);
        var job = Job("job-1");
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        control.FailWhen = FaultInjectingCache.Times(1, FaultInjectingCache.Call("InsertBulk"));

        await Assert.ThrowsAnyAsync<Exception>(async () => await AcquireDueAsync(store));
        control.FailWhen = null;

        Assert.Equal(TriggerStates.Waiting, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        Assert.Empty(await Storage.GetFiredTriggersForInstanceAsync("node-1"));

        Assert.Equal(trigger.Key, Assert.Single(await AcquireDueAsync(store)).Key);
    }

    [Fact]
    public async Task B9_an_acquisition_whose_bulk_write_lands_the_rows_but_not_the_records_puts_the_rows_back_to_waiting()
    {
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewStoreAsync(cache: cache);
        var job = Job("job-1");
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        control.PartialInsertBulk = FaultInjectingCache.Times(1, FaultInjectingCache.Call("InsertBulk"));

        await Assert.ThrowsAnyAsync<Exception>(async () => await AcquireDueAsync(store));
        control.PartialInsertBulk = null;

        await AssertReleasedAndAcquirableByAnotherNodeAsync(trigger.Key);
    }

    [Fact]
    public async Task B9_a_failed_acquisition_of_a_non_concurrent_job_does_not_leave_its_second_trigger_blocked()
    {
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewStoreAsync(cache: cache);
        var job = Job("job-1", nonConcurrent: true);
        var at = DateTimeOffset.UtcNow.AddSeconds(-1);
        await store.ScheduleJob(job, OneShot("t1", "tests", job.Key, at));
        await store.AddTrigger(OneShot("t2", "tests", job.Key, at.AddMilliseconds(-500)));

        control.PartialInsertBulk = FaultInjectingCache.Times(1, FaultInjectingCache.Call("InsertBulk"));

        await Assert.ThrowsAnyAsync<Exception>(async () => await AcquireDueAsync(store, 2));
        control.PartialInsertBulk = null;

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(new TriggerKey("t1", "tests")));
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(new TriggerKey("t2", "tests")));
        Assert.Empty(await Storage.GetFiredTriggersForInstanceAsync("node-1"));
        Assert.Single(await AcquireDueAsync(store, 2));
    }

    [Fact]
    public async Task B9_an_acquisition_that_succeeds_keeps_its_trigger_acquired_with_one_record()
    {
        var store = await NewStoreAsync();
        var job = Job("job-1");
        var trigger = OneShot("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.ScheduleJob(job, trigger);

        var acquired = Assert.Single(await AcquireDueAsync(store));

        Assert.Equal(TriggerStates.Acquired, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        var record = Assert.Single(await Storage.GetFiredTriggersForInstanceAsync("node-1"));
        Assert.Equal(acquired.FireInstanceId, record.EntryId);
        Assert.Equal("node-1", record.InstanceName);
    }
}

