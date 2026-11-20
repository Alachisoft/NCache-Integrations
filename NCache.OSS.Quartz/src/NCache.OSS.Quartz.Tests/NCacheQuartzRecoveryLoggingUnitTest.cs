using NCache.OSS.Quartz.Model;
using Quartz;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzRecoveryLoggingUnitTest : NCacheQuartzScenarioTestBase
{
    private const string CreatedText = "Created recovery trigger";
    private const string PendingText = "already exists and is pending";
    private const string DeadNode = "dead-node";

    private NCacheFiredTriggerData ExecutingFiring(string entryId, string jobName, bool requestsRecovery = true) => new()
    {
        SchedulerName = SchedulerName,
        EntryId = entryId,
        TriggerName = "t",
        TriggerGroup = "tests",
        InstanceName = DeadNode,
        FiredTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        ScheduledTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        State = FiredTriggerStates.Executing,
        JobName = jobName,
        JobGroup = "tests",
        RequestsRecovery = requestsRecovery
    };

    private async Task RecoverAsync(NCacheJobStore survivor, NCacheFiredTriggerData firing)
    {
        await Storage.RegisterSchedulerInstanceAsync(DeadNode);
        await Storage.StoreFiredTriggerAsync(firing);
        await survivor.RecoverStaleSchedulerInstancesAsync();
    }

    private async Task<(NCacheJobStore Store, CapturingLoggerProvider Log)> SurvivorAsync()
    {
        var log = new CapturingLoggerProvider();
        var store = await NewStoreAsync("survivor", logger: log.CreateLogger<NCacheJobStore>());
        return (store, log);
    }

    private async Task<List<TriggerKey>> RecoveryTriggersAsync(NCacheJobStore store)
        => (await store.QueryTriggers(new TriggerQuery())).Items
            .Where(t => t.Key.Group == SchedulerConstants.DefaultRecoveryGroup)
            .Select(t => t.Key)
            .ToList();

    private static int Count(CapturingLoggerProvider log, string text)
        => log.Lines.Count(l => l.Contains(text, StringComparison.Ordinal));

    [Fact]
    public async Task B11_recovering_a_firing_whose_recovery_trigger_is_still_pending_does_not_log_a_creation()
    {
        var (store, log) = await SurvivorAsync();
        await store.AddJob(Job("job-1", requestsRecovery: true));

        await RecoverAsync(store, ExecutingFiring("e1", "job-1"));
        await RecoverAsync(store, ExecutingFiring("e1", "job-1"));

        var recoveryTriggers = await RecoveryTriggersAsync(store);
        Assert.Single(recoveryTriggers);
        Assert.Equal(1, Count(log, CreatedText));
        Assert.Equal(1, Count(log, PendingText));
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(recoveryTriggers[0]));
    }

    [Fact]
    public async Task B11_a_first_recovery_logs_exactly_one_creation_for_the_trigger_it_creates()
    {
        var (store, log) = await SurvivorAsync();
        await store.AddJob(Job("job-1", requestsRecovery: true));

        await RecoverAsync(store, ExecutingFiring("e1", "job-1"));

        var recoveryTrigger = Assert.Single(await RecoveryTriggersAsync(store));
        Assert.Equal("recover_e1", recoveryTrigger.Name);
        Assert.Equal(1, Count(log, CreatedText));
        Assert.Equal(0, Count(log, PendingText));
        Assert.Empty(await Storage.GetFiredTriggersForInstanceAsync(DeadNode));
    }

    [Fact]
    public async Task B11_reviving_a_recovery_trigger_that_had_ended_in_error_is_logged_as_a_creation()
    {
        var (store, log) = await SurvivorAsync();
        await store.AddJob(Job("job-1", requestsRecovery: true));

        await RecoverAsync(store, ExecutingFiring("e1", "job-1"));

        var key = Assert.Single(await RecoveryTriggersAsync(store));
        var row = (await Storage.GetTriggerAsync(key.Group, key.Name))!;
        row.TriggerState = TriggerStates.Error;
        await Storage.UpdateTriggerAsync(row);

        await RecoverAsync(store, ExecutingFiring("e1", "job-1"));

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(key));
        Assert.Equal(2, Count(log, CreatedText));
        Assert.Equal(0, Count(log, PendingText));
    }

    [Fact]
    public async Task B11_a_firing_that_did_not_request_recovery_creates_nothing_and_logs_no_creation()
    {
        var (store, log) = await SurvivorAsync();
        await store.AddJob(Job("job-1", requestsRecovery: false));

        await RecoverAsync(store, ExecutingFiring("e1", "job-1", requestsRecovery: false));

        Assert.Empty(await RecoveryTriggersAsync(store));
        Assert.Equal(0, Count(log, CreatedText));
        Assert.Empty(await Storage.GetFiredTriggersForInstanceAsync(DeadNode));
    }

    [Fact]
    public async Task B11_a_firing_whose_job_was_deleted_creates_nothing_and_logs_no_creation()
    {
        var (store, log) = await SurvivorAsync();

        await RecoverAsync(store, ExecutingFiring("e1", "deleted-job"));

        Assert.Empty(await RecoveryTriggersAsync(store));
        Assert.Equal(0, Count(log, CreatedText));
        Assert.Empty(await Storage.GetFiredTriggersForInstanceAsync(DeadNode));
    }

    [Fact]
    public async Task B11_two_distinct_firings_of_the_dead_node_log_one_creation_each()
    {
        var (store, log) = await SurvivorAsync();
        await store.AddJob(Job("job-1", requestsRecovery: true));

        await Storage.RegisterSchedulerInstanceAsync(DeadNode);
        await Storage.StoreFiredTriggerAsync(ExecutingFiring("e1", "job-1"));
        await Storage.StoreFiredTriggerAsync(ExecutingFiring("e2", "job-1"));
        await store.RecoverStaleSchedulerInstancesAsync();

        Assert.Equal(2, (await RecoveryTriggersAsync(store)).Count);
        Assert.Equal(2, Count(log, CreatedText));
    }
}

