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
public sealed class NCacheQuartzAcquisitionUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task AcquireNextTriggers_applies_misfire_before_acquisition()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        await Storage.StoreJobAsync(job, replaceExisting: false);

        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-10), repeatCount: 5);
        trigger.MisfireInstruction = 1;

        await Storage.StoreTriggerAsync(trigger, replaceExisting: false);
        await Index.UpsertAsync(trigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(5), 1));

        Assert.Single(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Acquired, stored.TriggerState);
    }



    [Fact]
    public async Task AcquireNextTriggers_reacquires_trigger_when_misfire_keeps_it_due()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        await Storage.StoreJobAsync(job, replaceExisting: false);

        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-10), repeatCount: -1);
        trigger.MisfireInstruction = (int)SimpleTriggerMisfireInstruction.NextWithRemainingCount;
        trigger.Simple!.TimesTriggered = 1;

        await Storage.StoreTriggerAsync(trigger, replaceExisting: false);
        await Index.UpsertAsync(trigger);

        var request = CreateRequest(DateTimeOffset.UtcNow.AddMinutes(5), 1);

        var acquired = await store.AcquireNextTriggers(request);

        Assert.Single(acquired);
        Assert.Equal("trigger-1", acquired[0].Key.Name);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Acquired, stored.TriggerState);
    }
    [Fact]
    public async Task AcquireNextTriggers_skips_trigger_when_job_is_missing()
    {
        var store = CreateStore("instance-1");

        var trigger = CreateWaitingTrigger("trigger-1", "missing-job", DateTimeOffset.UtcNow.AddMinutes(-1));

        await Storage.StoreTriggerAsync(trigger, replaceExisting: false);
        await Index.UpsertAsync(trigger);

        var request = CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1);

        var acquired = await store.AcquireNextTriggers(request);

        Assert.Empty(acquired);
        Assert.False(await Storage.JobExistsAsync("tests", "missing-job"));
    }

    [Fact]
    public async Task AcquireNextTriggers_skips_excluded_job_type()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", jobClassName: typeof(TestJob).AssemblyQualifiedName!);
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        await StoreJobAndTriggerAsync(job, trigger);

        var request = new TriggerAcquisitionRequest
        {
            NoLaterThan = DateTimeOffset.UtcNow.AddMinutes(1),
            MaxCount = 1,
            TimeWindow = TimeSpan.Zero,
            ExcludedJobTypeNames = new[] { typeof(TestJob).AssemblyQualifiedName! }
        };

        var acquired = await store.AcquireNextTriggers(request);

        Assert.Empty(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);

        var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10);

        Assert.Contains(candidates, x => x.TriggerGroup == "tests" && x.TriggerName == "trigger-1");
    }

    [Fact]
    public async Task AcquireNextTriggers_automatic_preferred_node_without_claim_claims_current_instance()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        trigger.PreferredNodeIsAutomatic = true;
        trigger.PreferredNode = null;

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Single(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.True(stored.PreferredNodeIsAutomatic);
        Assert.Equal("instance-1", stored.PreferredNode);
    }

    [Fact]
    public async Task AcquireNextTriggers_automatic_preferred_node_claimed_by_alive_other_instance_is_skipped()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        trigger.PreferredNodeIsAutomatic = true;
        trigger.PreferredNode = "instance-2";

        await StoreJobAndTriggerAsync(job, trigger);

        await Storage.CheckInAsync(new NCacheSchedulerState
        {
            SchedulerName = SchedulerName,
            InstanceName = "instance-2",
            LastCheckinTimeUtc = DateTimeOffset.UtcNow,
            CheckinInterval = TimeSpan.FromSeconds(10)
        });

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Empty(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal("instance-2", stored.PreferredNode);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
    }

    [Fact]
    public async Task AcquireNextTriggers_automatic_preferred_node_claimed_by_stale_instance_is_reassigned()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        trigger.PreferredNodeIsAutomatic = true;
        trigger.PreferredNode = "instance-2";

        await StoreJobAndTriggerAsync(job, trigger);

        await Storage.CheckInAsync(new NCacheSchedulerState
        {
            SchedulerName = SchedulerName,
            InstanceName = "instance-2",
            LastCheckinTimeUtc = DateTimeOffset.UtcNow.AddSeconds(-60),
            CheckinInterval = TimeSpan.FromSeconds(10)
        });

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Single(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal("instance-1", stored.PreferredNode);
        Assert.Equal(TriggerStates.Acquired, stored.TriggerState);
    }

    [Fact]
    public async Task AcquireNextTriggers_explicit_preferred_node_owned_by_alive_other_instance_is_skipped()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        trigger.PreferredNodeIsAutomatic = false;
        trigger.PreferredNode = "instance-2";

        await StoreJobAndTriggerAsync(job, trigger);

        await Storage.CheckInAsync(new NCacheSchedulerState
        {
            SchedulerName = SchedulerName,
            InstanceName = "instance-2",
            LastCheckinTimeUtc = DateTimeOffset.UtcNow,
            CheckinInterval = TimeSpan.FromSeconds(10)
        });

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Empty(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal("instance-2", stored.PreferredNode);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
    }

    [Fact]
    public async Task AcquireNextTriggers_explicit_preferred_node_owned_by_stale_instance_is_acquired()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        trigger.PreferredNodeIsAutomatic = false;
        trigger.PreferredNode = "instance-2";

        await StoreJobAndTriggerAsync(job, trigger);

        await Storage.CheckInAsync(new NCacheSchedulerState
        {
            SchedulerName = SchedulerName,
            InstanceName = "instance-2",
            LastCheckinTimeUtc = DateTimeOffset.UtcNow.AddSeconds(-60),
            CheckinInterval = TimeSpan.FromSeconds(10)
        });

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Single(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal("instance-2", stored.PreferredNode);
        Assert.Equal(TriggerStates.Acquired, stored.TriggerState);
    }

    [Fact]
    public async Task AcquireNextTriggers_automatic_preferred_node_claim_is_persisted()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        trigger.PreferredNodeIsAutomatic = true;
        trigger.PreferredNode = null;

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Single(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.True(stored.PreferredNodeIsAutomatic);
        Assert.Equal("instance-1", stored.PreferredNode);
    }

    [Fact]
    public async Task AcquireNextTriggers_acquires_non_concurrent_job_when_no_active_firing_exists()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Single(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Acquired, stored.TriggerState);
    }
    [Fact]
    public async Task AcquireNextTriggers_blocks_non_concurrent_job_when_active_firing_exists()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        await StoreJobAndTriggerAsync(job, trigger);

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = "active-fire",
            TriggerName = "existing-trigger",
            TriggerGroup = "tests",
            InstanceName = "instance-1",
            FiredTimeUtc = DateTimeOffset.UtcNow.AddSeconds(-5),
            ScheduledTimeUtc = DateTimeOffset.UtcNow.AddSeconds(-5),
            Priority = 5,
            State = FiredTriggerStates.Executing,
            JobName = "job-1",
            JobGroup = "tests",
            IsNonConcurrent = true,
            RequestsRecovery = false,
            ExecutionGroup = null
        });

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Empty(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Blocked, stored.TriggerState);
    }
    [Fact]
    public async Task AcquireNextTriggers_blocks_only_candidate_sibling_trigger()
    {
        var store = CreateStore("instance-1");

        var job1 = CreateJob("job-1", nonConcurrent: true);
        var job2 = CreateJob("job-2");

        var siblingTrigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-2));
        var unrelatedTrigger = CreateWaitingTrigger("trigger-2", "job-2", DateTimeOffset.UtcNow.AddMinutes(-1));

        await StoreJobAndTriggerAsync(job1, siblingTrigger);
        await StoreJobAndTriggerAsync(job2, unrelatedTrigger);

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = "active-fire",
            TriggerName = "existing-trigger",
            TriggerGroup = "tests",
            InstanceName = "instance-1",
            FiredTimeUtc = DateTimeOffset.UtcNow.AddSeconds(-5),
            ScheduledTimeUtc = DateTimeOffset.UtcNow.AddSeconds(-5),
            Priority = 5,
            State = FiredTriggerStates.Executing,
            JobName = "job-1",
            JobGroup = "tests",
            IsNonConcurrent = true,
            RequestsRecovery = false,
            ExecutionGroup = null
        });

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 2));

        Assert.Single(acquired);
        Assert.Equal("trigger-2", acquired[0].Key.Name);

        var storedSibling = await Storage.GetTriggerAsync("tests", "trigger-1");
        var storedUnrelated = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(storedSibling);
        Assert.NotNull(storedUnrelated);
        Assert.Equal(TriggerStates.Blocked, storedSibling.TriggerState);
        Assert.Equal(TriggerStates.Acquired, storedUnrelated.TriggerState);
    }

    [Fact]
    public async Task AcquireNextTriggers_persists_blocked_state_and_updates_index()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        await StoreJobAndTriggerAsync(job, trigger);

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = "active-fire",
            TriggerName = "existing-trigger",
            TriggerGroup = "tests",
            InstanceName = "instance-1",
            FiredTimeUtc = DateTimeOffset.UtcNow.AddSeconds(-5),
            ScheduledTimeUtc = DateTimeOffset.UtcNow.AddSeconds(-5),
            Priority = 5,
            State = FiredTriggerStates.Executing,
            JobName = "job-1",
            JobGroup = "tests",
            IsNonConcurrent = true,
            RequestsRecovery = false,
            ExecutionGroup = null
        });

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Empty(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Blocked, stored.TriggerState);

        var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10);

        Assert.DoesNotContain(candidates, x => x.TriggerGroup == "tests" && x.TriggerName == "trigger-1");
    }

    [Fact]
    public async Task AcquireNextTriggers_does_not_consume_execution_slot_when_non_concurrent_trigger_is_blocked()
    {
        var store = CreateStore("instance-1");

        var blockedJob = CreateJob("job-1", nonConcurrent: true);
        var validJob = CreateJob("job-2");

        var blockedTrigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-2));
        var validTrigger = CreateWaitingTrigger("trigger-2", "job-2", DateTimeOffset.UtcNow.AddMinutes(-1));

        blockedTrigger.ExecutionGroup = "batch";
        validTrigger.ExecutionGroup = "batch";

        await StoreJobAndTriggerAsync(blockedJob, blockedTrigger);
        await StoreJobAndTriggerAsync(validJob, validTrigger);

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = "active-fire",
            TriggerName = "existing-trigger",
            TriggerGroup = "tests",
            InstanceName = "instance-1",
            FiredTimeUtc = DateTimeOffset.UtcNow.AddSeconds(-5),
            ScheduledTimeUtc = DateTimeOffset.UtcNow.AddSeconds(-5),
            Priority = 5,
            State = FiredTriggerStates.Executing,
            JobName = "job-1",
            JobGroup = "tests",
            IsNonConcurrent = true,
            RequestsRecovery = false,
            ExecutionGroup = "batch"
        });

        var limits = ExecutionLimitsBuilder.Create().ForGroup("batch", 1).Build();

        var request = new TriggerAcquisitionRequest
        {
            NoLaterThan = DateTimeOffset.UtcNow.AddMinutes(1),
            MaxCount = 2,
            TimeWindow = TimeSpan.Zero,
            ExecutionLimits = limits
        };

        var acquired = await store.AcquireNextTriggers(request);

        Assert.Single(acquired);
        Assert.Equal("trigger-2", acquired[0].Key.Name);

        var storedBlocked = await Storage.GetTriggerAsync("tests", "trigger-1");
        var storedValid = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(storedBlocked);
        Assert.NotNull(storedValid);
        Assert.Equal(TriggerStates.Blocked, storedBlocked.TriggerState);
        Assert.Equal(TriggerStates.Acquired, storedValid.TriggerState);
    }


    [Fact]
    public async Task AcquireNextTriggers_marks_trigger_acquired()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Single(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Acquired, stored.TriggerState);
    }

    [Fact]
    public async Task AcquireNextTriggers_creates_acquired_fired_trigger()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Single(acquired);
        Assert.False(string.IsNullOrWhiteSpace(acquired[0].FireInstanceId));

        var fired = await Storage.GetFiredTriggerAsync(acquired[0].FireInstanceId!);

        Assert.NotNull(fired);
        Assert.Equal(FiredTriggerStates.Acquired, fired.State);
        Assert.Equal("instance-1", fired.InstanceName);
        Assert.Equal("trigger-1", fired.TriggerName);
        Assert.Equal("tests", fired.TriggerGroup);
        Assert.Equal("job-1", fired.JobName);
        Assert.Equal("tests", fired.JobGroup);
    }

    [Fact]
    public async Task AcquireNextTriggers_assigns_fire_instance_id()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Single(acquired);
        Assert.False(string.IsNullOrWhiteSpace(acquired[0].FireInstanceId));

        var fired = await Storage.GetFiredTriggerAsync(acquired[0].FireInstanceId!);

        Assert.NotNull(fired);
        Assert.Equal(acquired[0].FireInstanceId, fired.EntryId);
    }

    [Fact]
    public async Task AcquireNextTriggers_returns_quartz_trigger()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Single(acquired);
        Assert.NotNull(acquired[0]);
        Assert.IsAssignableFrom<IOperableTrigger>(acquired[0]);
    }

    [Fact]
    public async Task AcquireNextTriggers_returns_correct_trigger_identity()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Single(acquired);
        Assert.Equal("trigger-1", acquired[0].Key.Name);
        Assert.Equal("tests", acquired[0].Key.Group);
        Assert.Equal("job-1", acquired[0].JobKey.Name);
        Assert.Equal("tests", acquired[0].JobKey.Group);
    }

    [Fact]
    public async Task AcquireNextTriggers_updates_index_after_acquisition()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Single(acquired);

        var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10);

        Assert.DoesNotContain(candidates, x => x.TriggerGroup == "tests" && x.TriggerName == "trigger-1");
    }


    [Fact]
    public async Task AcquireNextTriggers_acquires_multiple_eligible_triggers()
    {
        var store = CreateStore("instance-1");

        var job1 = CreateJob("job-1");
        var job2 = CreateJob("job-2");

        var trigger1 = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-2));
        var trigger2 = CreateWaitingTrigger("trigger-2", "job-2", DateTimeOffset.UtcNow.AddMinutes(-1));

        await StoreJobAndTriggerAsync(job1, trigger1);
        await StoreJobAndTriggerAsync(job2, trigger2);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 2));

        Assert.Equal(2, acquired.Count);
        Assert.Contains(acquired, x => x.Key.Name == "trigger-1");
        Assert.Contains(acquired, x => x.Key.Name == "trigger-2");
    }

    [Fact]
    public async Task AcquireNextTriggers_stops_after_requested_max_count()
    {
        var store = CreateStore("instance-1");

        for (var i = 1; i <= 3; i++)
        {
            var job = CreateJob($"job-{i}");
            var trigger = CreateWaitingTrigger($"trigger-{i}", $"job-{i}", DateTimeOffset.UtcNow.AddMinutes(-i));

            await StoreJobAndTriggerAsync(job, trigger);
        }

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 2));

        Assert.Equal(2, acquired.Count);
    }

    [Fact]
    public async Task AcquireNextTriggers_skips_ineligible_candidates_and_continues()
    {
        var store = CreateStore("instance-1");

        var staleJob = CreateJob("stale-job");
        var validJob = CreateJob("valid-job");

        var staleTrigger = CreateWaitingTrigger("stale-trigger", "stale-job", DateTimeOffset.UtcNow.AddMinutes(-2));
        var validTrigger = CreateWaitingTrigger("valid-trigger", "valid-job", DateTimeOffset.UtcNow.AddMinutes(-1));

        await StoreJobAndTriggerAsync(staleJob, staleTrigger);
        await StoreJobAndTriggerAsync(validJob, validTrigger);

        await Storage.RemoveTriggerAsync("tests", "stale-trigger");

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Single(acquired);
        Assert.Equal("valid-trigger", acquired[0].Key.Name);

        var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10);

        Assert.DoesNotContain(candidates, x => x.TriggerGroup == "tests" && x.TriggerName == "stale-trigger");
    }

    [Fact]
    public async Task AcquireNextTriggers_preserves_candidate_order()
    {
        var store = CreateStore("instance-1");

        var job1 = CreateJob("job-1");
        var job2 = CreateJob("job-2");

        var earlier = CreateWaitingTrigger("earlier", "job-1", DateTimeOffset.UtcNow.AddMinutes(-2), priority: 1);
        var later = CreateWaitingTrigger("later", "job-2", DateTimeOffset.UtcNow.AddMinutes(-1), priority: 10);

        await StoreJobAndTriggerAsync(job1, earlier);
        await StoreJobAndTriggerAsync(job2, later);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 2));

        Assert.Equal(2, acquired.Count);
        Assert.Equal("earlier", acquired[0].Key.Name);
        Assert.Equal("later", acquired[1].Key.Name);
    }


    [Fact]
    public async Task ReleaseAcquiredTrigger_returns_acquired_trigger_to_waiting()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Single(acquired);

        await store.ReleaseAcquiredTrigger(acquired[0]);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
    }

    [Fact]
    public async Task ReleaseAcquiredTrigger_removes_fired_trigger_record()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Single(acquired);
        Assert.False(string.IsNullOrWhiteSpace(acquired[0].FireInstanceId));

        await store.ReleaseAcquiredTrigger(acquired[0]);

        var fired = await Storage.GetFiredTriggerAsync(acquired[0].FireInstanceId!);

        Assert.Null(fired);
    }

    [Fact]
    public async Task ReleaseAcquiredTrigger_updates_index()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        await StoreJobAndTriggerAsync(job, trigger);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1), 1));

        Assert.Single(acquired);

        await store.ReleaseAcquiredTrigger(acquired[0]);

        var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10);

        Assert.Contains(candidates, x => x.TriggerGroup == "tests" && x.TriggerName == "trigger-1");
    }

    [Fact]
    public async Task ReleaseAcquiredTrigger_does_not_change_non_acquired_trigger()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        await StoreJobAndTriggerAsync(job, trigger);

        var quartzTrigger = ToQuartzTrigger(trigger);

        await store.ReleaseAcquiredTrigger(quartzTrigger);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
    }

    [Fact]
    public async Task ReleaseAcquiredTrigger_handles_missing_stored_trigger()
    {
        var store = CreateStore("instance-1");

        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));
        var quartzTrigger = ToQuartzTrigger(trigger);

        await store.ReleaseAcquiredTrigger(quartzTrigger);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.Null(stored);
    }

    [Fact]
    public async Task ReleaseAcquiredTrigger_handles_trigger_without_fire_instance_id()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));
        trigger.TriggerState = TriggerStates.Acquired;

        await StoreJobAndTriggerAsync(job, trigger, addToIndex: false);

        var quartzTrigger = ToQuartzTrigger(trigger);

        await store.ReleaseAcquiredTrigger(quartzTrigger);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
    } 
    [Fact]
    public async Task AcquireNextTriggers_honors_execution_group_limit()
    {
        var store = CreateStore("instance-1");

        var job1 = CreateJob("job-1");
        var job2 = CreateJob("job-2");

        var trigger1 = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-2));
        var trigger2 = CreateWaitingTrigger("trigger-2", "job-2", DateTimeOffset.UtcNow.AddMinutes(-1));

        trigger1.ExecutionGroup = "batch";
        trigger2.ExecutionGroup = "batch";

        await StoreJobAndTriggerAsync(job1, trigger1);
        await StoreJobAndTriggerAsync(job2, trigger2);

        var limits = ExecutionLimitsBuilder.Create()
            .ForGroup("batch", 1)
            .Build();

        var request = new TriggerAcquisitionRequest
        {
            NoLaterThan = DateTimeOffset.UtcNow.AddMinutes(1),
            MaxCount = 2,
            TimeWindow = TimeSpan.Zero,
            ExecutionLimits = limits
        };

        var acquired = await store.AcquireNextTriggers(request);

        Assert.Single(acquired);
    }
    [Fact]
    public async Task AcquireNextTriggers_honors_trigger_group_limit()
    {
        var store = CreateStore("instance-1");

        var job1 = CreateJob("job-1");
        var job2 = CreateJob("job-2");

        var trigger1 = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-2), triggerGroup: "tenant-a");
        var trigger2 = CreateWaitingTrigger("trigger-2", "job-2", DateTimeOffset.UtcNow.AddMinutes(-1), triggerGroup: "tenant-a");

        trigger1.ExecutionGroup = null;
        trigger2.ExecutionGroup = null;

        await StoreJobAndTriggerAsync(job1, trigger1);
        await StoreJobAndTriggerAsync(job2, trigger2);

        var limits = ExecutionLimitsBuilder.Create()
            .UseTriggerGroupWhenUnset()
            .ForGroup("tenant-a", 1)
            .Build();

        var request = new TriggerAcquisitionRequest
        {
            NoLaterThan = DateTimeOffset.UtcNow.AddMinutes(1),
            MaxCount = 2,
            TimeWindow = TimeSpan.Zero,
            ExecutionLimits = limits
        };

        var acquired = await store.AcquireNextTriggers(request);

        Assert.Single(acquired);
    }
    [Fact]
    public async Task AcquireNextTriggers_honors_cluster_scoped_execution_limit()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");

        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));
        trigger.ExecutionGroup = "cluster-batch";

        await StoreJobAndTriggerAsync(job, trigger);

        await Storage.CheckInAsync(new NCacheSchedulerState
        {
            SchedulerName = SchedulerName,
            InstanceName = "instance-2",
            LastCheckinTimeUtc = DateTimeOffset.UtcNow,
            CheckinInterval = TimeSpan.FromSeconds(10)
        });

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = "active-fire",
            TriggerName = "existing-trigger",
            TriggerGroup = "tests",
            InstanceName = "instance-2",
            FiredTimeUtc = DateTimeOffset.UtcNow.AddSeconds(-5),
            ScheduledTimeUtc = DateTimeOffset.UtcNow.AddSeconds(-5),
            Priority = 5,
            State = FiredTriggerStates.Executing,
            JobName = "job-1",
            JobGroup = "tests",
            IsNonConcurrent = false,
            RequestsRecovery = false,
            ExecutionGroup = "cluster-batch"
        });

        var limits = ExecutionLimitsBuilder.Create()
            .ForGroup("cluster-batch", 1, ExecutionLimitScope.Cluster)
            .Build();

        var request = new TriggerAcquisitionRequest
        {
            NoLaterThan = DateTimeOffset.UtcNow.AddMinutes(1),
            MaxCount = 1,
            TimeWindow = TimeSpan.Zero,
            ExecutionLimits = limits
        };

        var acquired = await store.AcquireNextTriggers(request);

        Assert.Empty(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
    }
    [Fact]
    public async Task AcquireNextTriggers_counts_active_firings_from_other_instances()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");

        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));
        trigger.ExecutionGroup = "shared";

        await StoreJobAndTriggerAsync(job, trigger);

        await Storage.CheckInAsync(new NCacheSchedulerState
        {
            SchedulerName = SchedulerName,
            InstanceName = "instance-2",
            LastCheckinTimeUtc = DateTimeOffset.UtcNow,
            CheckinInterval = TimeSpan.FromSeconds(10)
        });

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = "fire-2",
            TriggerName = "running-trigger",
            TriggerGroup = "tests",
            InstanceName = "instance-2",
            FiredTimeUtc = DateTimeOffset.UtcNow.AddSeconds(-10),
            ScheduledTimeUtc = DateTimeOffset.UtcNow.AddSeconds(-10),
            Priority = 5,
            State = FiredTriggerStates.Acquired,
            JobName = "job-1",
            JobGroup = "tests",
            IsNonConcurrent = false,
            RequestsRecovery = false,
            ExecutionGroup = "shared"
        });

        var limits = ExecutionLimitsBuilder.Create()
            .ForGroup("shared", 1, ExecutionLimitScope.Cluster)
            .Build();

        var request = new TriggerAcquisitionRequest
        {
            NoLaterThan = DateTimeOffset.UtcNow.AddMinutes(1),
            MaxCount = 1,
            TimeWindow = TimeSpan.Zero,
            ExecutionLimits = limits
        };

        var acquired = await store.AcquireNextTriggers(request);

        Assert.Empty(acquired);
    }
    [Fact]
    public async Task AcquireNextTriggers_does_not_count_completed_firings_toward_cluster_limit()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");

        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));
        trigger.ExecutionGroup = "shared";

        await StoreJobAndTriggerAsync(job, trigger);

        await Storage.CheckInAsync(new NCacheSchedulerState
        {
            SchedulerName = SchedulerName,
            InstanceName = "instance-2",
            LastCheckinTimeUtc = DateTimeOffset.UtcNow,
            CheckinInterval = TimeSpan.FromSeconds(10)
        });

        await Storage.StoreFiredTriggerAsync(new NCacheFiredTriggerData
        {
            SchedulerName = SchedulerName,
            EntryId = "completed-fire",
            TriggerName = "completed-trigger",
            TriggerGroup = "tests",
            InstanceName = "instance-2",
            FiredTimeUtc = DateTimeOffset.UtcNow.AddSeconds(-20),
            ScheduledTimeUtc = DateTimeOffset.UtcNow.AddSeconds(-20),
            Priority = 5,
            State = FiredTriggerStates.Complete,
            JobName = "job-1",
            JobGroup = "tests",
            IsNonConcurrent = false,
            RequestsRecovery = false,
            ExecutionGroup = "shared"
        });

        var limits = ExecutionLimitsBuilder.Create()
            .ForGroup("shared", 1, ExecutionLimitScope.Cluster)
            .Build();

        var request = new TriggerAcquisitionRequest
        {
            NoLaterThan = DateTimeOffset.UtcNow.AddMinutes(1),
            MaxCount = 1,
            TimeWindow = TimeSpan.Zero,
            ExecutionLimits = limits
        };

        var acquired = await store.AcquireNextTriggers(request);

        Assert.Single(acquired);
        Assert.Equal("trigger-1", acquired[0].Key.Name);
    }
    [Fact]
    public async Task AcquireNextTriggers_does_not_consume_execution_slot_for_skipped_trigger()
    {
        var store = CreateStore("instance-1");

        var excludedJob = CreateJob("excluded-job", jobClassName: "ExcludedJobType");
        var validJob = CreateJob("valid-job");

        var excludedTrigger = CreateWaitingTrigger("excluded-trigger", "excluded-job", DateTimeOffset.UtcNow.AddSeconds(10));
        var validTrigger = CreateWaitingTrigger("valid-trigger", "valid-job", DateTimeOffset.UtcNow.AddSeconds(20));

        excludedTrigger.ExecutionGroup = "batch";
        validTrigger.ExecutionGroup = "batch";

        await StoreJobAndTriggerAsync(excludedJob, excludedTrigger);
        await StoreJobAndTriggerAsync(validJob, validTrigger);

        var limits = ExecutionLimitsBuilder.Create().ForGroup("batch", 1).Build();

        var request = new TriggerAcquisitionRequest { NoLaterThan = DateTimeOffset.UtcNow.AddMinutes(1), MaxCount = 1, TimeWindow = TimeSpan.Zero, ExecutionLimits = limits, ExcludedJobTypeNames = new[] { excludedJob.JobClassName } };

        var acquired = await store.AcquireNextTriggers(request);

        Assert.Single(acquired);
        Assert.Equal("valid-trigger", acquired[0].Key.Name);

        var excludedStored = await Storage.GetTriggerAsync("tests", "excluded-trigger");
        var validStored = await Storage.GetTriggerAsync("tests", "valid-trigger");

        Assert.NotNull(excludedStored);
        Assert.NotNull(validStored);
        Assert.Equal(TriggerStates.Waiting, excludedStored.TriggerState);
        Assert.Equal(TriggerStates.Acquired, validStored.TriggerState);
    }
    [Fact]
    public async Task AcquireNextTriggers_does_not_acquire_trigger_when_execution_group_limit_is_zero()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1");
        var trigger = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        trigger.ExecutionGroup = "forbidden";

        await StoreJobAndTriggerAsync(job, trigger);

        var limits = ExecutionLimitsBuilder.Create()
            .ForGroup("forbidden", 0)
            .Build();

        var request = new TriggerAcquisitionRequest
        {
            NoLaterThan = DateTimeOffset.UtcNow.AddMinutes(1),
            MaxCount = 1,
            TimeWindow = TimeSpan.Zero,
            ExecutionLimits = limits
        };

        var acquired = await store.AcquireNextTriggers(request);

        Assert.Empty(acquired);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
    }

    [Fact]
    public async Task AcquireNextTriggers_keeps_due_triggers_it_did_not_acquire_in_the_index_when_their_rows_carry_a_stale_bucket_id()
    {
        var store = CreateStore("instance-1");
        var job = CreateJob("job-1");
        await Storage.StoreJobAsync(job, replaceExisting: false);

        var now = DateTimeOffset.UtcNow;

        var paused = new List<NCacheTriggerData>();
        for (var i = 0; i < 4; i++)
        {
            var trigger = CreateWaitingTrigger($"paused-{i}", "job-1", now.AddSeconds(-40).AddMilliseconds(i));
            await Index.UpsertAsync(trigger);
            trigger.TriggerState = TriggerStates.Paused;
            trigger.IndexBucketId = null;
            await Storage.StoreTriggerAsync(trigger, replaceExisting: false);
            paused.Add(trigger);
        }

        var due = new List<NCacheTriggerData>();
        for (var i = 0; i < 10; i++)
        {
            var trigger = CreateWaitingTrigger($"due-{i:D2}", "job-1", now.AddSeconds(-20).AddMilliseconds(i));
            await Storage.StoreTriggerAsync(trigger, replaceExisting: false);
            due.Add(trigger);
        }

        await Index.UpsertManyAsync(due);

        var acquired = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, maxCount: 4));

        Assert.Equal(4, acquired.Count);
        Assert.All(acquired, t => Assert.StartsWith("due-", t.Key.Name));

        var acquiredNames = acquired.Select(t => t.Key.Name).ToHashSet();
        var stillDue = due.Where(t => !acquiredNames.Contains(t.TriggerName)).Select(t => t.TriggerName).OrderBy(n => n).ToList();

        Assert.Equal(6, stillDue.Count);

        foreach (var name in stillDue)
        {
            var row = await Storage.GetTriggerAsync("tests", name);
            Assert.NotNull(row);
            Assert.Equal(TriggerStates.Waiting, row.TriggerState);
        }

        var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);

        Assert.Equal(stillDue, candidates.Select(c => c.TriggerName).OrderBy(n => n).ToList());

        var second = await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, maxCount: 10));

        Assert.Equal(stillDue, second.Select(t => t.Key.Name).OrderBy(n => n).ToList());
    }
}

