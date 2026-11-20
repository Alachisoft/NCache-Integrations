using System.Runtime.CompilerServices;
using Medallion.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NCache.OSS.Quartz.Index;
using NCache.OSS.Quartz.Locking;
using NCache.OSS.Quartz.Mapping;
using NCache.OSS.Quartz.Model;
using NCache.OSS.Quartz.Serialization;
using NCache.OSS.Quartz.Storage;
using Quartz;
using Quartz.Extensibility;
using Quartz.Impl.AdoJobStore;
using Quartz.Impl.Triggers;

namespace NCache.OSS.Quartz;

public sealed class NCacheJobStore : IJobStore
{
    internal const string AllGroupsPausedMarker = "_$_ALL_GROUPS_PAUSED_$_";

    private readonly NCacheStorage _storage;
    private readonly NextFireTimeIndex _index;
    private readonly NCacheDistributedLocks _locks;
    private readonly ILogger _logger;

    private string _schedulerName = string.Empty;
    private string _instanceId = string.Empty;

    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;
    private int _maintenanceRunning;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string JobGroup, string JobName)> _acquiredJobKeys = new(StringComparer.Ordinal);

    private TimeSpan _misfireThreshold = TimeSpan.FromSeconds(60);
    private TimeSpan _clusterCheckinInterval = TimeSpan.FromSeconds(10);
    private TimeSpan _clusterCheckinMisfireThreshold = TimeSpan.FromMilliseconds(7500);
    private TimeSpan? _staleAcquiredTriggerThreshold;
    private TimeSpan? _lockAcquireTimeout;

    private readonly ISchedulerSignaler? _signaler;

    private readonly AsyncLocal<List<Func<ISchedulerSignaler, CancellationToken, ValueTask>>?> _pendingSignals = new();

    private readonly IObjectSerializer? _calendarSerializer;

    private readonly QuartzObjectSerialization _serialization;

    private CancellationTokenSource _retryStop = new();

    public NCacheJobStore(
        NCacheStorage storage,
        NextFireTimeIndex index,
        NCacheDistributedLocks locks,
        ILogger<NCacheJobStore>? logger = null,
        ISchedulerSignaler? signaler = null,
        IObjectSerializer? calendarSerializer = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _logger = logger ?? NullLogger<NCacheJobStore>.Instance;
        _signaler = signaler;
        _calendarSerializer = calendarSerializer;
        _serialization = new QuartzObjectSerialization(calendarSerializer);
    }

    public bool SupportsPersistence => true;

    public bool Clustered => true;

    // Tells Quartz that AddTrigger and ScheduleJobs store a trigger paused in the same locked write.
    public bool SupportsStoringPaused => true;

    public TimeSpan EstimatedTimeToReleaseAndAcquireTrigger => TimeSpan.FromMilliseconds(70);

    public TimeSpan MisfireThreshold
    {
        get => _misfireThreshold;
        set
        {
            if (value < TimeSpan.FromMilliseconds(1))
            {
                throw new ArgumentException("MisfireThreshold must be greater than zero.", nameof(value));
            }

            _misfireThreshold = value;
        }
    }

    public TimeSpan ClusterCheckinInterval
    {
        get => _clusterCheckinInterval;
        set
        {
            if (value < TimeSpan.FromMilliseconds(100))
            {
                throw new ArgumentException("ClusterCheckinInterval must be at least 100 milliseconds.", nameof(value));
            }

            _clusterCheckinInterval = value;
        }
    }

    public TimeSpan ClusterCheckinMisfireThreshold
    {
        get => _clusterCheckinMisfireThreshold;
        set
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentException("ClusterCheckinMisfireThreshold cannot be negative.", nameof(value));
            }

            _clusterCheckinMisfireThreshold = value;
        }
    }

    public TimeSpan StaleAcquiredTriggerThreshold
    {
        get => _staleAcquiredTriggerThreshold ?? Max(TimeSpan.FromTicks(_misfireThreshold.Ticks * 2), TimeSpan.FromMinutes(2));
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentException("StaleAcquiredTriggerThreshold must be positive.", nameof(value));
            }

            _staleAcquiredTriggerThreshold = value;
        }
    }

    public TimeSpan? LockAcquireTimeout
    {
        get => _lockAcquireTimeout;
        set
        {
            if (value.HasValue && value.Value <= TimeSpan.Zero)
            {
                throw new ArgumentException("LockAcquireTimeout must be positive or null.", nameof(value));
            }

            _lockAcquireTimeout = value;
        }
    }

    public bool RepairRegistriesOnStart { get; set; } = true;

    public string InstanceId => _instanceId;

    public string SchedulerName => _schedulerName;

    public ValueTask Initialize(SchedulerIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(identity.SchedulerName, _storage.SchedulerName, StringComparison.Ordinal))
        {
            throw new SchedulerConfigException(
                $"Quartz initialised this NCache job store as scheduler '{identity.SchedulerName}', but it was created for " +
                $"scheduler '{_storage.SchedulerName}'. Use the same name for both: for a named registration " +
                "(AddQuartz(\"name\", ...)) the scheduler name is that registration name.");
        }

        _schedulerName = identity.SchedulerName;
        _instanceId = identity.InstanceId;

        return ValueTask.CompletedTask;
    }

    public async ValueTask SchedulerStarted(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_heartbeatTask is not null)
        {
            throw new InvalidOperationException("SchedulerStarted was called twice without an intervening Shutdown.");
        }

        if (_retryStop.IsCancellationRequested)
        {
            _retryStop = new CancellationTokenSource();
        }

        await WithStateAccessAsync(lct => _storage.CheckInAsync(CreateStateRecord(), lct), cancellationToken);
         
        _heartbeatCts = new CancellationTokenSource();
        _heartbeatTask = RunHeartbeatAsync(_heartbeatCts.Token);

        try
        {
            await WithBothLocksAsync(async lct =>
            {
                if (RepairRegistriesOnStart)
                {
                    await _storage.RebuildRegistriesAsync(lct);
                }

                await RebuildNextFireTimeIndexAsync(lct);

                if (RepairRegistriesOnStart)
                {
                    await ReleaseOrphanedAcquisitionsAsync(null, lct);
                }

                await RecoverInstanceFiringsAsync(_instanceId, lct);
            }, cancellationToken);

            await RecoverStaleSchedulerInstancesAsync(cancellationToken);
            await RecoverStaleAcquiredFiringsAsync(cancellationToken);
        }
        catch
        {
            await StopHeartbeatAsync();
            throw;
        }
    }

    public ValueTask SchedulerPaused(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask SchedulerResumed(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask Shutdown(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _retryStop.Cancel();
        await StopHeartbeatAsync();

        if (string.IsNullOrWhiteSpace(_instanceId))
        {
            return;
        }

        await WithStateAccessAsync(async lct =>
        {
            await _storage.RemoveSchedulerStateRecordAsync(_instanceId, lct);

            var leftovers = await _storage.GetFiredTriggersForInstanceAsync(_instanceId, lct);

            if (leftovers.Count == 0)
            {
                await _storage.UnregisterSchedulerInstanceAsync(_instanceId, lct);
            }
            else
            {
                _logger.LogWarning(
                    "Scheduler instance {InstanceId} shut down with {Count} firing(s) still in flight; leaving them for cluster recovery.",
                    _instanceId, leftovers.Count);
            }
        }, cancellationToken);
    }

    internal async Task StopHeartbeatAsync()
    {
        var cts = _heartbeatCts;
        var task = _heartbeatTask;

        _heartbeatCts = null;
        _heartbeatTask = null;

        if (cts is null)
        {
            return;
        }

        cts.Cancel();

        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts.Dispose();
    }

    public async ValueTask ScheduleJob(IJobDetail job, IOperableTrigger trigger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(trigger);

        if (trigger.JobKey != job.Key)
        {
            throw new JobPersistenceException($"Trigger '{trigger.Key}' does not reference job '{job.Key}'.");
        }

        await WithTriggerAccessAsync(async lct =>
        {
            if (await _storage.JobExistsAsync(job.Key.Group, job.Key.Name, lct))
            {
                throw new ObjectAlreadyExistsException($"Job '{job.Key}' already exists.");
            }

            if (await _storage.TriggerExistsAsync(trigger.Key.Group, trigger.Key.Name, lct))
            {
                throw new ObjectAlreadyExistsException($"Trigger '{trigger.Key}' already exists.");
            }

            await EnsureContinuationParentExistsAsync(trigger, alsoStored: null, aboutToBeDeleted: null, lct);

            var jobData = JobMapper.ToModel(job, _schedulerName, _serialization);
            var triggerState = trigger.Continuation.IsNone
                ? await ComputeStateForStoredTriggerAsync(null, trigger.Key.Group, jobData, lct)
                : TriggerStates.Awaiting;
            var triggerData = TriggerMapper.ToModel(trigger, _schedulerName, triggerState, _serialization);
            StampJob(triggerData, jobData);

            await _storage.StoreJobAsync(jobData, replaceExisting: false, lct);

            try
            {
                await _index.UpsertAsync(triggerData, lct);
                await _storage.StoreTriggerAsync(triggerData, replaceExisting: false, lct);
            }
            catch
            {
                await BestEffortAsync(() => _index.RemoveAsync(triggerData, lct));
                await BestEffortAsync(() => _storage.RemoveTriggerAsync(triggerData.TriggerGroup, triggerData.TriggerName, lct));
                await BestEffortAsync(() => _storage.RemoveJobAsync(jobData.JobGroup, jobData.JobName, lct));
                throw;
            }
        }, cancellationToken);
    }

    public async ValueTask AddJob(IJobDetail job, AddJobOptions options = default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await WithTriggerAccessAsync(async lct =>
        {
            var jobData = JobMapper.ToModel(job, _schedulerName, _serialization);
            await _storage.StoreJobAsync(jobData, replaceExisting: options.Replace, lct);

            if (options.Replace)
            {
                await RestampJobTriggersAsync(jobData, new HashSet<(string, string)>(), lct);
            }
        }, cancellationToken);
    }

    // Stores the jobs and their triggers under one lock, paused from the start when the options ask for it.
    public async ValueTask ScheduleJobs(IReadOnlyDictionary<IJobDetail, IReadOnlyCollection<IOperableTrigger>> triggersAndJobs, ScheduleJobOptions options = default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggersAndJobs);

        var storedPause = options.Paused ? RecordStoredPause(options.PauseReason, options.PauseRequestedBy) : null;

        await WithTriggerAccessAsync(async lct =>
        {
            var storedJobs = new List<NCacheJobData>();
            var storedTriggers = new List<NCacheTriggerData>();
            var previousJobs = new Dictionary<JobKey, NCacheJobData>();
            var previousTriggers = new Dictionary<(string Group, string Name), NCacheTriggerData>();

            var existingJobs = await _storage.GetJobsAsync(triggersAndJobs.Keys.Select(j => (j.Key.Group, j.Key.Name)), lct);
            var existingTriggers = await _storage.GetTriggersAsync(triggersAndJobs.Values.SelectMany(t => t).Select(t => (t.Key.Group, t.Key.Name)), lct);
            var pausedTriggerGroups = await _storage.GetPausedTriggerGroupsAsync(lct);
            var pausedJobGroups = await _storage.GetPausedJobGroupsAsync(lct);

            var batchKeys = triggersAndJobs.Values.SelectMany(t => t).Select(t => t.Key).ToHashSet();

            foreach (var (job, triggers) in triggersAndJobs)
            {
                lct.ThrowIfCancellationRequested();

                if (existingJobs.TryGetValue((job.Key.Group, job.Key.Name), out var existingJob))
                {
                    if (!options.Replace)
                    {
                        throw new ObjectAlreadyExistsException($"Job '{job.Key}' already exists.");
                    }

                    previousJobs[job.Key] = existingJob;
                }

                var jobData = JobMapper.ToModel(job, _schedulerName, _serialization);
                storedJobs.Add(jobData);

                foreach (var trigger in triggers)
                {
                    if (trigger.JobKey != job.Key)
                    {
                        throw new JobPersistenceException($"Trigger '{trigger.Key}' does not reference job '{job.Key}'.");
                    }

                    existingTriggers.TryGetValue((trigger.Key.Group, trigger.Key.Name), out var existingTrigger);

                    if (existingTrigger is not null)
                    {
                        if (!options.Replace)
                        {
                            throw new ObjectAlreadyExistsException($"Trigger '{trigger.Key}' already exists.");
                        }

                        previousTriggers[(trigger.Key.Group, trigger.Key.Name)] = existingTrigger;
                    }

                    await EnsureContinuationParentExistsAsync(trigger, batchKeys, aboutToBeDeleted: null, lct);

                    var state = trigger.Continuation.IsNone
                        ? await ComputeStateForStoredTriggerAsync(existingTrigger, trigger.Key.Group, jobData, pausedTriggerGroups, pausedJobGroups, lct, options.Paused)
                        : TriggerStates.Awaiting;
                    var mapped = TriggerMapper.ToModel(trigger, _schedulerName, state, _serialization);
                    StampJob(mapped, jobData);

                    if (existingTrigger is not null)
                    {
                        PreserveAutomaticNodeClaim(mapped, existingTrigger);
                        PreservePreviousFireTime(mapped, existingTrigger);
                        PreservePause(mapped, existingTrigger);
                        await StampOverlapSettlementAsync(mapped, existingTrigger, lct);
                    }

                    if (options.Paused)
                    {
                        StampStoredPause(mapped, storedPause);
                    }

                    storedTriggers.Add(mapped);
                }
            }

            try
            {
                await _storage.StoreJobsAsync(storedJobs, lct);

                if (previousJobs.Count > 0)
                {
                    var inBatch = storedTriggers.Select(t => (t.TriggerGroup, t.TriggerName)).ToHashSet();

                    foreach (var jobData in storedJobs.Where(j => previousJobs.ContainsKey(new JobKey(j.JobName, j.JobGroup))))
                    {
                        await RestampJobTriggersAsync(jobData, inBatch, lct);
                    }
                }

                foreach (var (key, previous) in previousTriggers)
                {
                    var replacement = storedTriggers.First(t => t.TriggerGroup == key.Group && t.TriggerName == key.Name);

                    if (previous.JobGroup != replacement.JobGroup || previous.JobName != replacement.JobName)
                    {
                        await _storage.UnlinkTriggerFromJobAsync(previous.JobGroup, previous.JobName, key.Group, key.Name, lct);
                    }
                }

                await _index.UpsertManyAsync(storedTriggers, lct);
                await _storage.StoreTriggersAsync(storedTriggers, lct);
            }
            catch
            {
                foreach (var triggerData in storedTriggers)
                {
                    var key = (triggerData.TriggerGroup, triggerData.TriggerName);

                    await BestEffortAsync(() => _index.RemoveAsync(triggerData, lct));

                    if (previousTriggers.TryGetValue(key, out var previousTrigger))
                    {
                        await BestEffortAsync(() => _index.UpsertAsync(previousTrigger, lct));
                        await BestEffortAsync(() => _storage.StoreTriggerAsync(previousTrigger, replaceExisting: true, lct));
                    }
                    else
                    {
                        await BestEffortAsync(() => _storage.RemoveTriggerAsync(triggerData.TriggerGroup, triggerData.TriggerName, lct));
                    }
                }

                foreach (var jobData in storedJobs)
                {
                    var key = new JobKey(jobData.JobName, jobData.JobGroup);

                    if (previousJobs.TryGetValue(key, out var previousJob))
                    {
                        await BestEffortAsync(() => _storage.StoreJobAsync(previousJob, replaceExisting: true, lct));
                    }
                    else
                    {
                        await BestEffortAsync(() => _storage.RemoveJobAsync(jobData.JobGroup, jobData.JobName, lct));
                    }
                }

                throw;
            }
        }, cancellationToken);
    }

    public async ValueTask<bool> DeleteJob(JobKey jobKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobKey);

        return await WithTriggerAccessAsync(lct => DeleteJobInternalAsync(jobKey, lct), cancellationToken);
    }

    public async ValueTask<List<JobKey>> DeleteJobs(IReadOnlyCollection<JobKey> jobKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobKeys);

        return await WithTriggerAccessAsync(async lct =>
        {
            var result = new List<JobKey>();

            foreach (var jobKey in jobKeys)
            {
                lct.ThrowIfCancellationRequested();

                if (await DeleteJobInternalAsync(jobKey, lct))
                {
                    result.Add(jobKey);
                }
            }

            return result;
        }, cancellationToken);
    }

    public async ValueTask<List<JobKey>> DeleteJobs(GroupMatcher<JobKey> matcher, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(matcher);

        return await WithTriggerAccessAsync(async lct =>
        {
            var result = new List<JobKey>();

            foreach (var group in await _storage.GetAllJobGroupsAsync(lct))
            {
                lct.ThrowIfCancellationRequested();

                if (!MatchesGroup(matcher, group))
                {
                    continue;
                }

                foreach (var jobKey in await _storage.GetJobsInGroupAsync(group, lct))
                {
                    lct.ThrowIfCancellationRequested();

                    if (await DeleteJobInternalAsync(jobKey, lct))
                    {
                        result.Add(jobKey);
                    }
                }
            }

            return result;
        }, cancellationToken);
    }

    private async Task<bool> DeleteJobInternalAsync(JobKey jobKey, CancellationToken lct)
    {
        var job = await _storage.GetJobAsync(jobKey.Group, jobKey.Name, lct);

        if (job is null)
        {
            return false;
        }

        foreach (var trigger in await _storage.GetTriggersForJobAsync(jobKey.Group, jobKey.Name, lct))
        {
            lct.ThrowIfCancellationRequested();
            await DeleteTriggerInternalAsync(trigger, deleteOrphanJob: false, lct);
        }

        return await _storage.RemoveJobAsync(jobKey.Group, jobKey.Name, lct);
    }

    public async ValueTask<IJobDetail?> GetJob(JobKey jobKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobKey);
        cancellationToken.ThrowIfCancellationRequested();

        var jobData = await _storage.GetJobAsync(jobKey.Group, jobKey.Name, cancellationToken);
        return jobData is null ? null : JobMapper.ToQuartzJob(jobData);
    }

    // Stores one trigger under the lock, paused from the start when the options ask for it.
    public async ValueTask AddTrigger(IOperableTrigger trigger, AddTriggerOptions options = default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        var storedPause = options.Paused ? RecordStoredPause(options.PauseReason, options.PauseRequestedBy) : null;

        await WithTriggerAccessAsync(async lct =>
        {
            var existing = await _storage.GetTriggerAsync(trigger.Key.Group, trigger.Key.Name, lct);

            if (existing is not null && !options.Replace)
            {
                throw new ObjectAlreadyExistsException($"Trigger '{trigger.Key}' already exists.");
            }

            await StoreTriggerUnderLockAsync(trigger, existing, lct, options.Paused, storedPause);
        }, cancellationToken);
    }

    public async ValueTask<ScheduleTriggerResult> StoreTrigger(IOperableTrigger trigger, TriggerConflict onConflict, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        if (onConflict is not (TriggerConflict.Throw or TriggerConflict.Replace or TriggerConflict.Keep or TriggerConflict.KeepEarlier))
        {
            throw new ArgumentOutOfRangeException(nameof(onConflict), onConflict, "Use Throw, Replace, Keep or KeepEarlier.");
        }

        return await WithTriggerAccessAsync(async lct =>
        {
            var existing = await _storage.GetTriggerAsync(trigger.Key.Group, trigger.Key.Name, lct);

            if (existing is not null)
            {
                if (onConflict == TriggerConflict.Throw)
                {
                    throw new ObjectAlreadyExistsException($"Trigger '{trigger.Key}' already exists.");
                }

                if (existing.NextFireTimeUtc is { } existingNext && existingNext != DateTimeOffset.MinValue)
                {
                    var keep = onConflict == TriggerConflict.Keep ||
                               (onConflict == TriggerConflict.KeepEarlier && !(trigger.NextFireTimeUtc < existingNext));

                    if (keep)
                    {
                        return new ScheduleTriggerResult(existingNext, ScheduleOutcome.Kept);
                    }
                }
            }

            await StoreTriggerUnderLockAsync(trigger, existing, lct);

            return new ScheduleTriggerResult(
                trigger.NextFireTimeUtc ?? trigger.StartTimeUtc,
                existing is null ? ScheduleOutcome.Created : ScheduleOutcome.Replaced);
        }, cancellationToken);
    }

    // Writes a trigger and its index entry, optionally paused with the given record, rolling both back on failure.
    private async Task StoreTriggerUnderLockAsync(IOperableTrigger trigger, NCacheTriggerData? existing, CancellationToken lct, bool storePaused = false, NCachePauseData? storedPause = null)
    {
        var job = await _storage.GetJobAsync(trigger.JobKey.Group, trigger.JobKey.Name, lct);

        if (job is null)
        {
            throw new JobPersistenceException($"Job '{trigger.JobKey}' does not exist.");
        }

        await EnsureContinuationParentExistsAsync(trigger, alsoStored: null, aboutToBeDeleted: existing is null ? null : trigger.Key, lct);

        var state = trigger.Continuation.IsNone
            ? await ComputeStateForStoredTriggerAsync(existing, trigger.Key.Group, job, lct, storePaused)
            : TriggerStates.Awaiting;
        var triggerData = TriggerMapper.ToModel(trigger, _schedulerName, state, _serialization);
        StampJob(triggerData, job);

        if (existing is not null)
        {
            PreserveAutomaticNodeClaim(triggerData, existing);
            PreservePreviousFireTime(triggerData, existing);
            PreservePause(triggerData, existing);
            await StampOverlapSettlementAsync(triggerData, existing, lct);
        }

        if (storePaused)
        {
            StampStoredPause(triggerData, storedPause);
        }

        try
        {
            await _index.UpsertAsync(triggerData, lct);
            await _storage.StoreTriggerAsync(triggerData, replaceExisting: existing is not null, lct);
        }
        catch
        {
            await BestEffortAsync(() => _index.RemoveAsync(triggerData, lct));

            if (existing is not null)
            {
                await BestEffortAsync(() => _index.UpsertAsync(existing, lct));
                await BestEffortAsync(() => _storage.StoreTriggerAsync(existing, replaceExisting: true, lct));
            }
            else
            {
                await BestEffortAsync(() => _storage.RemoveTriggerAsync(triggerData.TriggerGroup, triggerData.TriggerName, lct));
            }

            throw;
        }
    }

    public async ValueTask<bool> DeleteTrigger(TriggerKey triggerKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggerKey);

        return await WithTriggerAccessAsync(async lct =>
        {
            var trigger = await _storage.GetTriggerAsync(triggerKey.Group, triggerKey.Name, lct);

            if (trigger is null)
            {
                return false;
            }

            await DeleteTriggerInternalAsync(trigger, deleteOrphanJob: true, lct);
            return true;
        }, cancellationToken);
    }

    public async ValueTask<List<TriggerKey>> DeleteTriggers(IReadOnlyCollection<TriggerKey> triggerKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggerKeys);

        return await WithTriggerAccessAsync(async lct =>
        {
            var result = new List<TriggerKey>();

            foreach (var triggerKey in triggerKeys)
            {
                lct.ThrowIfCancellationRequested();

                var trigger = await _storage.GetTriggerAsync(triggerKey.Group, triggerKey.Name, lct);

                if (trigger is null)
                {
                    continue;
                }

                await DeleteTriggerInternalAsync(trigger, deleteOrphanJob: true, lct);
                result.Add(triggerKey);
            }

            return result;
        }, cancellationToken);
    }

    public async ValueTask<List<TriggerKey>> DeleteTriggers(GroupMatcher<TriggerKey> matcher, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(matcher);

        return await WithTriggerAccessAsync(async lct =>
        {
            var result = new List<TriggerKey>();

            foreach (var group in await _storage.GetAllTriggerGroupsAsync(lct))
            {
                lct.ThrowIfCancellationRequested();

                if (!MatchesGroup(matcher, group))
                {
                    continue;
                }

                foreach (var triggerKey in await _storage.GetTriggersInGroupAsync(group, lct))
                {
                    lct.ThrowIfCancellationRequested();

                    var trigger = await _storage.GetTriggerAsync(triggerKey.Group, triggerKey.Name, lct);

                    if (trigger is null)
                    {
                        continue;
                    }

                    await DeleteTriggerInternalAsync(trigger, deleteOrphanJob: true, lct);
                    result.Add(triggerKey);
                }
            }

            return result;
        }, cancellationToken);
    }

    public async ValueTask<bool> ReplaceTrigger(TriggerKey triggerKey, IOperableTrigger trigger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggerKey);
        ArgumentNullException.ThrowIfNull(trigger);

        return await WithTriggerAccessAsync(async lct =>
        {
            var existing = await _storage.GetTriggerAsync(triggerKey.Group, triggerKey.Name, lct);

            if (existing is null)
            {
                return false;
            }

            if (existing.JobGroup != trigger.JobKey.Group || existing.JobName != trigger.JobKey.Name)
            {
                throw new JobPersistenceException($"Replacement trigger '{trigger.Key}' must reference the same job as '{triggerKey}'.");
            }

            if (trigger.Key != triggerKey && await _storage.TriggerExistsAsync(trigger.Key.Group, trigger.Key.Name, lct))
            {
                throw new ObjectAlreadyExistsException($"Trigger '{trigger.Key}' already exists.");
            }

            var job = await _storage.GetJobAsync(trigger.JobKey.Group, trigger.JobKey.Name, lct);

            if (job is null)
            {
                throw new JobPersistenceException($"Job '{trigger.JobKey}' does not exist.");
            }

            await EnsureContinuationParentExistsAsync(trigger, alsoStored: null, aboutToBeDeleted: triggerKey, lct);

            var state = trigger.Continuation.IsNone
                ? await ComputeStateForStoredTriggerAsync(existing, trigger.Key.Group, job, lct)
                : TriggerStates.Awaiting;
            var replacement = TriggerMapper.ToModel(trigger, _schedulerName, state, _serialization);
            StampJob(replacement, job);

            if (!replacement.PreviousFireTimeUtc.HasValue)
            {
                replacement.PreviousFireTimeUtc = existing.PreviousFireTimeUtc;
            }

            PreserveAutomaticNodeClaim(replacement, existing);
            PreservePause(replacement, existing);
            await StampOverlapSettlementAsync(replacement, existing, lct);

            try
            {
                await DeleteTriggerInternalAsync(existing, deleteOrphanJob: false, lct, settleContinuations: false);
                await _index.UpsertAsync(replacement, lct);
                await _storage.StoreTriggerAsync(replacement, replaceExisting: false, lct);
            }
            catch
            {
                await BestEffortAsync(() => _index.RemoveAsync(replacement , lct));
                await BestEffortAsync(() => _storage.RemoveTriggerAsync(replacement.TriggerGroup, replacement.TriggerName, lct));
                await BestEffortAsync(() => _index.UpsertAsync(existing, lct));
                await BestEffortAsync(() => _storage.StoreTriggerAsync(existing, replaceExisting: true, lct));
                throw;
            }

            return true;
        }, cancellationToken);
    }

    public ValueTask<bool> UpdateTriggerDetails(TriggerKey triggerKey, TriggerDetailsUpdate update, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "UpdateTriggerDetails is not supported by NCache.OSS.Quartz: Quartz 4's TriggerDetailsUpdate exposes its " +
            "fields through internal getters that a third-party job store cannot read. Use ReplaceTrigger instead.");
    }

    public async ValueTask<IOperableTrigger?> GetTrigger(TriggerKey triggerKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggerKey);
        cancellationToken.ThrowIfCancellationRequested();

        var triggerData = await _storage.GetTriggerAsync(triggerKey.Group, triggerKey.Name, cancellationToken);
        return triggerData is null ? null : TriggerMapper.ToQuartzTrigger(triggerData, _serialization);
    }

    public async ValueTask<bool> Exists(JobKey jobKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobKey);
        cancellationToken.ThrowIfCancellationRequested();

        return await _storage.JobExistsAsync(jobKey.Group, jobKey.Name, cancellationToken);
    }

    public async ValueTask<bool> Exists(TriggerKey triggerKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggerKey);
        cancellationToken.ThrowIfCancellationRequested();

        return await _storage.TriggerExistsAsync(triggerKey.Group, triggerKey.Name, cancellationToken);
    }

    public async ValueTask<bool> Exists(string calendarName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarName);
        cancellationToken.ThrowIfCancellationRequested();

        return await _storage.CalendarExistsAsync(calendarName, cancellationToken);
    }

    public async ValueTask Clear(CancellationToken cancellationToken = default)
    {
        await WithBothLocksAsync(async lct =>
        {
            foreach (var triggerKey in await _storage.GetAllTriggerKeysAsync(lct))
            {
                lct.ThrowIfCancellationRequested();

                var trigger = await _storage.GetTriggerAsync(triggerKey.Group, triggerKey.Name, lct);

                if (trigger is not null)
                {
                    await DeleteTriggerInternalAsync(trigger, deleteOrphanJob: false, lct, settleContinuations: false);
                }
            }

            foreach (var jobGroup in await _storage.GetAllJobGroupsAsync(lct))
            {
                lct.ThrowIfCancellationRequested();

                foreach (var jobKey in await _storage.GetJobsInGroupAsync(jobGroup, lct))
                {
                    lct.ThrowIfCancellationRequested();
                    await _storage.RemoveJobAsync(jobKey.Group, jobKey.Name, lct);
                }
            }

            foreach (var calendarName in await _storage.GetRegisteredCalendarNamesAsync(lct))
            {
                lct.ThrowIfCancellationRequested();
                await _storage.RemoveCalendarAsync(calendarName, lct);
            }

            foreach (var firing in await _storage.GetAllFiredTriggersAsync(lct))
            {
                lct.ThrowIfCancellationRequested();
                await _storage.RemoveFiredTriggerAsync(firing.EntryId, lct);
            }

            await _storage.ClearPausedGroupsAsync(lct);
            await _index.RebuildAsync(Array.Empty<NCacheTriggerData>(), lct);
        }, cancellationToken);
    }

    public async ValueTask AddCalendar(string calendarName, ICalendar calendar, AddCalendarOptions options = default, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarName);
        ArgumentNullException.ThrowIfNull(calendar);
        cancellationToken.ThrowIfCancellationRequested();

        string calendarJson;

        try
        {
            calendarJson = QuartzCalendarSerializer.Serialize(calendar, _calendarSerializer);
        }
        catch (Exception ex) when (ex is not JobPersistenceException)
        {
            throw new JobPersistenceException(
                $"Calendar '{calendarName}' of type '{calendar.GetType().FullName}' cannot be stored: {ex.Message} " +
                "A calendar type of your own needs a CalendarSerializer registered with the scheduler's System.Text.Json " +
                "serializer registry (SystemTextJsonSerializerRegistry.AddCalendarSerializer), or with NCacheJobStoreOptions.ConfigureSerializer.", ex);
        }

        await WithTriggerAccessAsync(async lct =>
        {
            if (!options.Replace && await _storage.CalendarExistsAsync(calendarName, lct))
            {
                throw new ObjectAlreadyExistsException($"Calendar '{calendarName}' already exists.");
            }

            var calendarData = new NCacheCalendarData
            {
                SchedulerName = _schedulerName,
                CalendarName = calendarName,
                CalendarJson = calendarJson
            };

            await _storage.StoreCalendarAsync(calendarData, replaceExisting: options.Replace, lct);

            if (!options.UpdateTriggers)
            {
                return;
            }

            var updated = new List<NCacheTriggerData>();

            foreach (var trigger in await _storage.GetTriggersForCalendarAsync(calendarName, lct))
            {
                lct.ThrowIfCancellationRequested();

                var quartzTrigger = TriggerMapper.ToQuartzTrigger(trigger, _serialization);

                quartzTrigger.UpdateWithNewCalendar(calendar, quartzTrigger.MisfireThreshold ?? _misfireThreshold);
                TriggerMapper.ApplyScheduleState(trigger, quartzTrigger, _serialization);

                if (trigger.TriggerState == TriggerStates.Waiting && !trigger.NextFireTimeUtc.HasValue)
                {
                    trigger.TriggerState = TriggerStates.Complete;
                }

                updated.Add(trigger);
            }

            await _index.UpsertManyAsync(updated, lct);
            await _storage.UpdateTriggersAsync(updated, lct);
        }, cancellationToken);
    }

    public async ValueTask<bool> DeleteCalendar(string calendarName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarName);

        return await WithTriggerAccessAsync(async lct =>
        {
            if (!await _storage.CalendarExistsAsync(calendarName, lct))
            {
                return false;
            }

            var triggers = await _storage.GetTriggersForCalendarAsync(calendarName, lct);

            if (triggers.Count > 0)
            {
                throw new JobPersistenceException($"Calendar '{calendarName}' cannot be deleted because {triggers.Count} trigger(s) still reference it.");
            }

            return await _storage.RemoveCalendarAsync(calendarName, lct);
        }, cancellationToken);
    }

    public async ValueTask<ICalendar?> GetCalendar(string calendarName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarName);
        cancellationToken.ThrowIfCancellationRequested();

        return await GetCalendarAsync(calendarName, cancellationToken);
    }

    public async ValueTask<PagedResult<JobHeader>> QueryJobs(JobQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = new List<JobKey>();

        foreach (var group in await _storage.GetAllJobGroupsAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (query.Group is not null && !query.Group.IsMatch(new JobKey(string.Empty, group)))
            {
                continue;
            }

            foreach (var jobKey in await _storage.GetJobsInGroupAsync(group, cancellationToken))
            {
                if (query.Name is null || query.Name.IsMatch(jobKey))
                {
                    candidates.Add(jobKey);
                }
            }
        }

        var headers = await _storage.GetJobHeadersAsync(candidates.Select(k => (k.Group, k.Name)), cancellationToken);
        var matches = new List<JobHeader>(headers.Count);

        foreach (var jobKey in candidates)
        {
            if (!headers.TryGetValue((jobKey.Group, jobKey.Name), out var header))
            {
                continue;
            }

            matches.Add(new JobHeader(
                new JobKey(header.JobName, header.JobGroup),
                header.Description,
                header.JobClassName,
                header.IsDurable,
                header.IsNonConcurrent,
                header.IsUpdateData,
                header.RequestsRecovery));
        }

        matches.Sort((left, right) => CompareKeys(left.Key.Group, left.Key.Name, right.Key.Group, right.Key.Name));

        return CreatePagedResult(matches, query);
    }

    public async ValueTask<PagedResult<TriggerHeader>> QueryTriggers(TriggerQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = new List<TriggerKey>();

        foreach (var group in await _storage.GetAllTriggerGroupsAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (query.Group is not null && !query.Group.IsMatch(new TriggerKey(string.Empty, group)))
            {
                continue;
            }

            foreach (var triggerKey in await _storage.GetTriggersInGroupAsync(group, cancellationToken))
            {
                if (query.Name is null || query.Name.IsMatch(triggerKey))
                {
                    candidates.Add(triggerKey);
                }
            }
        }

        var rows = await _storage.GetTriggersAsync(candidates.Select(k => (k.Group, k.Name)), cancellationToken);
        var executing = new HashSet<(string, string)>();

        foreach (var firing in await _storage.GetAllFiredTriggersAsync(cancellationToken))
        {
            if (firing.State == FiredTriggerStates.Executing)
            {
                executing.Add((firing.TriggerGroup, firing.TriggerName));
            }
        }

        var matches = new List<TriggerHeader>(rows.Count);

        foreach (var triggerKey in candidates)
        {
            if (!rows.TryGetValue((triggerKey.Group, triggerKey.Name), out var trigger))
            {
                continue;
            }

            var jobKey = new JobKey(trigger.JobName, trigger.JobGroup);

            if (query.Job is not null && !query.Job.Equals(jobKey))
            {
                continue;
            }

            if (query.CalendarName is not null && !string.Equals(trigger.CalendarName, query.CalendarName, StringComparison.Ordinal))
            {
                continue;
            }

            var state = ToTriggerState(trigger, executing.Contains((trigger.TriggerGroup, trigger.TriggerName)));

            if (query.State is not null && state != query.State.Value)
            {
                continue;
            }

            if (query.NextFireTimeBefore is { } before && !(trigger.NextFireTimeUtc < before))
            {
                continue;
            }

            matches.Add(new TriggerHeader(
                triggerKey,
                jobKey,
                trigger.Description,
                ToTriggerTypeDiscriminator(trigger.TriggerKind),
                state,
                trigger.StartTimeUtc,
                trigger.EndTimeUtc,
                trigger.NextFireTimeUtc,
                trigger.PreviousFireTimeUtc,
                trigger.CalendarName,
                trigger.Priority,
                trigger.ExecutionGroup,
                trigger.RetryPolicy,
                trigger.RetryAttempt)
            {
                ContinuesAfter = ContinuationRules.ParentOf(trigger),
                ContinuationCondition = ContinuationRules.ConditionOf(trigger),
                OverlapPolicy = Enum.IsDefined((OverlapPolicy) trigger.OverlapPolicy) ? (OverlapPolicy) trigger.OverlapPolicy : OverlapPolicy.Default,
                Pause = trigger.TriggerState is TriggerStates.Paused or TriggerStates.PausedAndBlocked ? ToPauseInfo(trigger.Pause) : null
            });
        }

        matches.Sort((left, right) => CompareKeys(left.Key.Group, left.Key.Name, right.Key.Group, right.Key.Name));

        return CreatePagedResult(matches, query);
    }

    public async ValueTask<PagedResult<JobGroup>> QueryJobGroups(JobGroupQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var allGroups = await _storage.GetAllJobGroupsAsync(cancellationToken);
        var pausedGroups = await _storage.GetPausedJobGroupsAsync(cancellationToken);
        var matches = new List<JobGroup>();

        var candidates = query.Paused == true ? allGroups.Union(pausedGroups, StringComparer.Ordinal) : allGroups;

        foreach (var group in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (query.Name is not null && !query.Name.IsMatch(group))
            {
                continue;
            }

            var paused = pausedGroups.Contains(group, StringComparer.Ordinal);

            if (query.Paused.HasValue && paused != query.Paused.Value)
            {
                continue;
            }

            matches.Add(new JobGroup(group, paused));
        }

        matches.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));

        return CreatePagedResult(matches, query);
    }

    public async ValueTask<PagedResult<TriggerGroup>> QueryTriggerGroups(TriggerGroupQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var allGroups = await _storage.GetAllTriggerGroupsAsync(cancellationToken);
        var pausedGroups = await _storage.GetPausedTriggerGroupsAsync(cancellationToken);
        var allPaused = pausedGroups.Contains(AllGroupsPausedMarker, StringComparer.Ordinal);
        var matches = new List<TriggerGroup>();

        var candidates = query.Paused == true ? allGroups.Union(pausedGroups, StringComparer.Ordinal) : allGroups;

        foreach (var group in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (group == AllGroupsPausedMarker)
            {
                continue;
            }

            if (query.Name is not null && !query.Name.IsMatch(group))
            {
                continue;
            }

            var paused = allPaused || pausedGroups.Contains(group, StringComparer.Ordinal);

            if (query.Paused.HasValue && paused != query.Paused.Value)
            {
                continue;
            }

            matches.Add(new TriggerGroup(group, paused));
        }

        matches.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));

        return CreatePagedResult(matches, query);
    }

    public async ValueTask<PagedResult<string>> QueryCalendarNames(CalendarQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var matches = new List<string>(await _storage.GetRegisteredCalendarNamesAsync(cancellationToken));

        if (query.Name is not null)
        {
            matches.RemoveAll(name => !query.Name.IsMatch(name));
        }

        matches.Sort(StringComparer.Ordinal);

        return CreatePagedResult(matches, query);
    }

    public async ValueTask<PagedResult<FireInstance>> QueryFireInstances(FireInstanceQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var matches = new List<FireInstance>();

        foreach (var instanceName in await _storage.GetRegisteredSchedulerInstanceNamesAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (query.SchedulerInstanceId is not null && !string.Equals(query.SchedulerInstanceId, instanceName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var firing in await _storage.GetFiredTriggersForInstanceAsync(instanceName, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var triggerKey = new TriggerKey(firing.TriggerName, firing.TriggerGroup);

                if (query.TriggerGroup is not null && !query.TriggerGroup.IsMatch(triggerKey))
                {
                    continue;
                }

                if (query.TriggerName is not null && !query.TriggerName.IsMatch(triggerKey))
                {
                    continue;
                }

                var state = firing.State == FiredTriggerStates.Acquired ? FireInstanceState.Acquired : FireInstanceState.Executing;

                JobKey? jobKey = null;

                if (state == FireInstanceState.Executing && !string.IsNullOrWhiteSpace(firing.JobGroup) && !string.IsNullOrWhiteSpace(firing.JobName))
                {
                    jobKey = new JobKey(firing.JobName, firing.JobGroup);
                }

                if (query.Job is not null && (jobKey is null || jobKey != query.Job))
                {
                    continue;
                }

                if (query.State.HasValue && state != query.State.Value)
                {
                    continue;
                }

                matches.Add(new FireInstance(
                    firing.EntryId,
                    triggerKey,
                    jobKey,
                    firing.InstanceName,
                    state,
                    firing.FiredTimeUtc,
                    firing.ScheduledTimeUtc,
                    firing.ExecutionGroup)
                {
                    Progress = firing.Progress,
                    ProgressMessage = firing.ProgressMessage
                });
            }
        }

        matches.Sort((left, right) =>
        {
            var byKey = CompareKeys(left.TriggerKey.Group, left.TriggerKey.Name, right.TriggerKey.Group, right.TriggerKey.Name);
            return byKey != 0 ? byKey : StringComparer.Ordinal.Compare(left.FireInstanceId, right.FireInstanceId);
        });

        return CreatePagedResult(matches, query);
    }

    public async ValueTask<List<ClusterNode>> QueryClusterNodes(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var instanceNames = await _storage.GetRegisteredSchedulerInstanceNamesAsync(cancellationToken);
        var states = await _storage.GetAllSchedulerStatesAsync(instanceNames, cancellationToken);
        var stateByInstance = states.ToDictionary(x => x.InstanceName, StringComparer.Ordinal);
        var result = new List<ClusterNode>();
        var now = DateTimeOffset.UtcNow;

        if (stateByInstance.TryGetValue(_instanceId, out var currentState))
        {
            result.Add(new ClusterNode(_instanceId, currentState.LastCheckinTimeUtc, currentState.CheckinInterval, ClassifyClusterNode(currentState, now), true));
        }
        else
        {
            result.Add(new ClusterNode(_instanceId, null, null, ClusterNodeState.Alive, true));
        }

        foreach (var instanceName in instanceNames.OrderBy(x => x, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(instanceName, _instanceId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!stateByInstance.TryGetValue(instanceName, out var state))
            {
                result.Add(new ClusterNode(instanceName, null, null, ClusterNodeState.Failed, false));
                continue;
            }

            result.Add(new ClusterNode(state.InstanceName, state.LastCheckinTimeUtc, state.CheckinInterval, ClassifyClusterNode(state, now), false));
        }

        return result;
    }

    private ClusterNodeState ClassifyClusterNode(NCacheSchedulerState state, DateTimeOffset now)
    {
        if (IsStale(state, now))
        {
            return ClusterNodeState.Failed;
        }

        return state.LastCheckinTimeUtc + state.CheckinInterval < now ? ClusterNodeState.Overdue : ClusterNodeState.Alive;
    }

    public async ValueTask<List<IJobDetail>> GetJobs(IReadOnlyCollection<JobKey> jobKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobKeys);
        cancellationToken.ThrowIfCancellationRequested();

        var result = new List<IJobDetail>(jobKeys.Count);

        foreach (var jobKey in jobKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var jobData = await _storage.GetJobAsync(jobKey.Group, jobKey.Name, cancellationToken);

            if (jobData is not null)
            {
                result.Add(JobMapper.ToQuartzJob(jobData));
            }
        }

        return result;
    }

    public async ValueTask<List<IOperableTrigger>> GetTriggers(IReadOnlyCollection<TriggerKey> triggerKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggerKeys);
        cancellationToken.ThrowIfCancellationRequested();

        var result = new List<IOperableTrigger>(triggerKeys.Count);

        foreach (var triggerKey in triggerKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var triggerData = await _storage.GetTriggerAsync(triggerKey.Group, triggerKey.Name, cancellationToken);

            if (triggerData is not null)
            {
                result.Add(TriggerMapper.ToQuartzTrigger(triggerData, _serialization));
            }
        }

        return result;
    }

    public async ValueTask<List<IOperableTrigger>> GetTriggersForJob(JobKey jobKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobKey);
        cancellationToken.ThrowIfCancellationRequested();

        var triggerData = await _storage.GetTriggersForJobAsync(jobKey.Group, jobKey.Name, cancellationToken);
        var result = new List<IOperableTrigger>(triggerData.Count);

        foreach (var trigger in triggerData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(TriggerMapper.ToQuartzTrigger(trigger, _serialization));
        }

        return result;
    }

    public async ValueTask<TriggerState> GetTriggerState(TriggerKey triggerKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggerKey);
        cancellationToken.ThrowIfCancellationRequested();

        var trigger = await _storage.GetTriggerAsync(triggerKey.Group, triggerKey.Name, cancellationToken);

        return trigger is null ? TriggerState.None : await ComputeTriggerStateAsync(trigger, cancellationToken);
    }

    private async Task<TriggerState> ComputeTriggerStateAsync(NCacheTriggerData trigger, CancellationToken ct)
    {
        var executing = false;

        if (trigger.TriggerState is not (TriggerStates.Paused or TriggerStates.PausedAndBlocked or TriggerStates.Awaiting))
        {
            var firings = await _storage.GetAllFiredTriggersAsync(ct);
            executing = firings.Any(f => f.TriggerGroup == trigger.TriggerGroup && f.TriggerName == trigger.TriggerName && f.State == FiredTriggerStates.Executing);
        }

        return ToTriggerState(trigger, executing);
    }

    private static TriggerState ToTriggerState(NCacheTriggerData trigger, bool executing)
        => TriggerStateResolver.Resolve(trigger.TriggerState switch
        {
            TriggerStates.Waiting => StoredTriggerState.Waiting,
            TriggerStates.Acquired => StoredTriggerState.Acquired,
            TriggerStates.Paused => StoredTriggerState.Paused,
            TriggerStates.PausedAndBlocked => StoredTriggerState.PausedBlocked,
            TriggerStates.Blocked => StoredTriggerState.Blocked,
            TriggerStates.Complete => StoredTriggerState.Complete,
            TriggerStates.Error => StoredTriggerState.Error,
            TriggerStates.Awaiting => StoredTriggerState.Awaiting,
            _ => StoredTriggerState.Deleted
        }, executing);

    public async ValueTask<bool> ResetTriggerFromErrorState(TriggerKey triggerKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggerKey);

        return await WithTriggerAccessAsync(async lct =>
        {
            var pausedGroups = await _storage.GetPausedTriggerGroupsAsync(lct);
            return await ResetTriggerFromErrorInternalAsync(triggerKey, pausedGroups, lct);
        }, cancellationToken);
    }

    public async ValueTask<List<TriggerKey>> ResetTriggersFromErrorState(IReadOnlyCollection<TriggerKey> triggerKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggerKeys);

        return await WithTriggerAccessAsync(async lct =>
        {
            var result = new List<TriggerKey>();
            var pausedGroups = await _storage.GetPausedTriggerGroupsAsync(lct);

            foreach (var triggerKey in triggerKeys)
            {
                lct.ThrowIfCancellationRequested();

                if (await ResetTriggerFromErrorInternalAsync(triggerKey, pausedGroups, lct))
                {
                    result.Add(triggerKey);
                }
            }

            return result;
        }, cancellationToken);
    }

    private async Task<bool> ResetTriggerFromErrorInternalAsync(TriggerKey triggerKey, IReadOnlyCollection<string> pausedGroups, CancellationToken lct)
    {
        var trigger = await _storage.GetTriggerAsync(triggerKey.Group, triggerKey.Name, lct);

        if (trigger is null || trigger.TriggerState != TriggerStates.Error)
        {
            return false;
        }

        if (trigger.HasContinuationParent)
        {
            var now = DateTimeOffset.UtcNow;
            trigger.NextFireTimeUtc = trigger.StartTimeUtc > now ? trigger.StartTimeUtc : now;
            await _storage.UnregisterContinuationsAsync(new[] { trigger }, lct);
            ContinuationRules.Forget(trigger);
        }

        trigger.TriggerState = IsTriggerGroupPaused(pausedGroups, trigger.TriggerGroup) ? TriggerStates.Paused : TriggerStates.Waiting;

        await PersistTriggerWithIndexAsync(trigger, lct);
        return true;
    }

    public ValueTask<bool> PauseTrigger(TriggerKey triggerKey, CancellationToken cancellationToken = default)
        => PauseTriggerWith(triggerKey, details: null, cancellationToken);

    public async ValueTask<bool> PauseTriggerWith(TriggerKey triggerKey, PauseDetails? details, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggerKey);

        var pause = RecordPause(details);
        return await WithTriggerAccessAsync(lct => PauseTriggerInternalAsync(triggerKey, lct, pause), cancellationToken);
    }

    public async ValueTask<PauseInfo?> GetTriggerPause(TriggerKey triggerKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggerKey);
        cancellationToken.ThrowIfCancellationRequested();

        var trigger = await _storage.GetTriggerAsync(triggerKey.Group, triggerKey.Name, cancellationToken);

        if (trigger is null || trigger.TriggerState is not (TriggerStates.Paused or TriggerStates.PausedAndBlocked))
        {
            return null;
        }

        var pause = trigger.Pause
                    ?? await _storage.GetTriggerGroupPauseAsync(trigger.TriggerGroup, cancellationToken)
                    ?? await _storage.GetJobGroupPauseAsync(trigger.JobGroup, cancellationToken);

        return ToPauseInfo(pause);
    }

    public async ValueTask<PauseInfo?> GetTriggerGroupPause(string groupName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupName);
        cancellationToken.ThrowIfCancellationRequested();

        return ToPauseInfo(await _storage.GetTriggerGroupPauseAsync(groupName, cancellationToken));
    }

    public async ValueTask<PauseInfo?> GetJobGroupPause(string groupName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupName);
        cancellationToken.ThrowIfCancellationRequested();

        return ToPauseInfo(await _storage.GetJobGroupPauseAsync(groupName, cancellationToken));
    }

    private static NCachePauseData? RecordPause(PauseDetails? details)
    {
        if (details is null)
        {
            return null;
        }

        var reason = CutText(details.Reason, PauseDetails.MaxReasonLength);
        var requestedBy = CutText(details.RequestedBy, PauseDetails.MaxRequestedByLength);

        return reason is null && requestedBy is null
            ? null
            : new NCachePauseData { Reason = reason, RequestedBy = requestedBy, PausedAtUtc = DateTimeOffset.UtcNow };
    }

    // Builds the pause record for a trigger stored paused, from the reason and requester the store call carried.
    private static NCachePauseData? RecordStoredPause(string? reason, string? requestedBy)
        => RecordPause(new PauseDetails { Reason = reason, RequestedBy = requestedBy });

    private static string? CutText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..(char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength)];
    }

    private static PauseInfo? ToPauseInfo(NCachePauseData? pause)
        => pause is null ? null : new PauseInfo(pause.Reason, pause.RequestedBy, pause.PausedAtUtc);

    public ValueTask<List<TriggerKey>> PauseTriggers(IReadOnlyCollection<TriggerKey> triggerKeys, CancellationToken cancellationToken = default)
        => PauseTriggersWith(triggerKeys, details: null, cancellationToken);

    // Pauses the whole trigger set under one lock, stamping each newly paused trigger with the same record.
    public async ValueTask<List<TriggerKey>> PauseTriggersWith(IReadOnlyCollection<TriggerKey> triggerKeys, PauseDetails? details, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggerKeys);

        var pause = RecordPause(details);

        return await WithTriggerAccessAsync(async lct =>
        {
            var result = new List<TriggerKey>();

            foreach (var triggerKey in triggerKeys)
            {
                lct.ThrowIfCancellationRequested();

                if (await PauseTriggerInternalAsync(triggerKey, lct, pause))
                {
                    result.Add(triggerKey);
                }
            }

            return result;
        }, cancellationToken);
    }

    public ValueTask<List<string>> PauseTriggerGroups(GroupMatcher<TriggerKey> matcher, CancellationToken cancellationToken = default)
        => PauseTriggerGroupsWith(matcher, details: null, cancellationToken);

    public async ValueTask<List<string>> PauseTriggerGroupsWith(GroupMatcher<TriggerKey> matcher, PauseDetails? details, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(matcher);

        var pause = RecordPause(details);

        return await WithTriggerAccessAsync(async lct =>
        {
            var result = new List<string>();

            foreach (var group in MatchingGroups(matcher, await _storage.GetAllTriggerGroupsAsync(lct)))
            {
                lct.ThrowIfCancellationRequested();
                await PauseTriggerGroupUnderLockAsync(group, pause, lct);
                result.Add(group);
            }

            return result;
        }, cancellationToken);
    }

    private async Task PauseTriggerGroupUnderLockAsync(string group, NCachePauseData? pause, CancellationToken lct)
    {
        var alreadyPaused = (await _storage.GetPausedTriggerGroupsAsync(lct)).Contains(group, StringComparer.Ordinal);

        await _storage.AddPausedTriggerGroupAsync(group, lct);

        if (!alreadyPaused)
        {
            await _storage.SetTriggerGroupPauseAsync(group, pause, lct);
        }

        foreach (var triggerKey in await _storage.GetTriggersInGroupAsync(group, lct))
        {
            lct.ThrowIfCancellationRequested();
            await PauseTriggerInternalAsync(triggerKey, lct, pause);
        }
    }

    public ValueTask<bool> PauseJob(JobKey jobKey, CancellationToken cancellationToken = default)
        => PauseJobWith(jobKey, details: null, cancellationToken);

    public async ValueTask<bool> PauseJobWith(JobKey jobKey, PauseDetails? details, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobKey);

        var pause = RecordPause(details);
        return await WithTriggerAccessAsync(lct => PauseJobInternalAsync(jobKey, lct, pause), cancellationToken);
    }

    public ValueTask<List<JobKey>> PauseJobs(IReadOnlyCollection<JobKey> jobKeys, CancellationToken cancellationToken = default)
        => PauseJobsWith(jobKeys, details: null, cancellationToken);

    // Pauses every trigger of the whole job set under one lock, stamping each newly paused trigger with the same record.
    public async ValueTask<List<JobKey>> PauseJobsWith(IReadOnlyCollection<JobKey> jobKeys, PauseDetails? details, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobKeys);

        var pause = RecordPause(details);

        return await WithTriggerAccessAsync(async lct =>
        {
            var result = new List<JobKey>();

            foreach (var jobKey in jobKeys)
            {
                lct.ThrowIfCancellationRequested();

                if (await PauseJobInternalAsync(jobKey, lct, pause))
                {
                    result.Add(jobKey);
                }
            }

            return result;
        }, cancellationToken);
    }

    public ValueTask<List<string>> PauseJobGroups(GroupMatcher<JobKey> matcher, CancellationToken cancellationToken = default)
        => PauseJobGroupsWith(matcher, details: null, cancellationToken);

    public async ValueTask<List<string>> PauseJobGroupsWith(GroupMatcher<JobKey> matcher, PauseDetails? details, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(matcher);

        var pause = RecordPause(details);

        return await WithTriggerAccessAsync(async lct =>
        {
            var result = new List<string>();
            var pausedBefore = await _storage.GetPausedJobGroupsAsync(lct);

            foreach (var group in MatchingGroups(matcher, await _storage.GetAllJobGroupsAsync(lct)))
            {
                lct.ThrowIfCancellationRequested();

                await _storage.AddPausedJobGroupAsync(group, lct);

                if (!pausedBefore.Contains(group, StringComparer.Ordinal))
                {
                    await _storage.SetJobGroupPauseAsync(group, pause, lct);
                }

                foreach (var jobKey in await _storage.GetJobsInGroupAsync(group, lct))
                {
                    lct.ThrowIfCancellationRequested();
                    await PauseJobInternalAsync(jobKey, lct, pause);
                }

                result.Add(group);
            }

            return result;
        }, cancellationToken);
    }

    public async ValueTask<bool> ResumeTrigger(TriggerKey triggerKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggerKey);

        return await WithTriggerAccessAsync(lct => ResumeTriggerInternalAsync(triggerKey, lct), cancellationToken);
    }

    public async ValueTask<List<TriggerKey>> ResumeTriggers(IReadOnlyCollection<TriggerKey> triggerKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggerKeys);

        return await WithTriggerAccessAsync(async lct =>
        {
            var result = new List<TriggerKey>();

            foreach (var triggerKey in triggerKeys)
            {
                lct.ThrowIfCancellationRequested();

                if (await ResumeTriggerInternalAsync(triggerKey, lct))
                {
                    result.Add(triggerKey);
                }
            }

            return result;
        }, cancellationToken);
    }

    public async ValueTask<List<string>> ResumeTriggerGroups(GroupMatcher<TriggerKey> matcher, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(matcher);

        return await WithTriggerAccessAsync(async lct =>
        {
            var result = new List<string>();
            var pausedGroups = await _storage.GetPausedTriggerGroupsAsync(lct);
            var candidateGroups = pausedGroups
                .Where(g => g != AllGroupsPausedMarker)
                .Union(await _storage.GetAllTriggerGroupsAsync(lct), StringComparer.Ordinal);

            foreach (var group in MatchingGroups(matcher, candidateGroups, includeEqualityTarget: false))
            {
                lct.ThrowIfCancellationRequested();

                foreach (var triggerKey in await _storage.GetTriggersInGroupAsync(group, lct))
                {
                    lct.ThrowIfCancellationRequested();
                    await ResumeTriggerInternalAsync(triggerKey, lct);
                }

                await _storage.RemovePausedTriggerGroupAsync(group, lct);
                await _storage.SetTriggerGroupPauseAsync(group, pause: null, lct);
                result.Add(group);
            }

            if (MatchesGroup(matcher, AllGroupsPausedMarker))
            {
                await _storage.RemovePausedTriggerGroupAsync(AllGroupsPausedMarker, lct);
            }

            return result;
        }, cancellationToken);
    }

    public async ValueTask<bool> ResumeJob(JobKey jobKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobKey);

        return await WithTriggerAccessAsync(lct => ResumeJobInternalAsync(jobKey, lct), cancellationToken);
    }

    public async ValueTask<List<JobKey>> ResumeJobs(IReadOnlyCollection<JobKey> jobKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobKeys);

        return await WithTriggerAccessAsync(async lct =>
        {
            var result = new List<JobKey>();

            foreach (var jobKey in jobKeys)
            {
                lct.ThrowIfCancellationRequested();

                if (await ResumeJobInternalAsync(jobKey, lct))
                {
                    result.Add(jobKey);
                }
            }

            return result;
        }, cancellationToken);
    }

    public async ValueTask<List<string>> ResumeJobGroups(GroupMatcher<JobKey> matcher, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(matcher);

        return await WithTriggerAccessAsync(async lct =>
        {
            var result = new List<string>();
            var candidateGroups = (await _storage.GetPausedJobGroupsAsync(lct))
                .Union(await _storage.GetAllJobGroupsAsync(lct), StringComparer.Ordinal);

            foreach (var group in MatchingGroups(matcher, candidateGroups, includeEqualityTarget: false))
            {
                lct.ThrowIfCancellationRequested();

                foreach (var jobKey in await _storage.GetJobsInGroupAsync(group, lct))
                {
                    lct.ThrowIfCancellationRequested();
                    await ResumeJobInternalAsync(jobKey, lct);
                }

                await _storage.RemovePausedJobGroupAsync(group, lct);
                await _storage.SetJobGroupPauseAsync(group, pause: null, lct);
                result.Add(group);
            }

            return result;
        }, cancellationToken);
    }

    public ValueTask PauseAll(CancellationToken cancellationToken = default)
        => PauseAllWith(details: null, cancellationToken);

    public async ValueTask PauseAllWith(PauseDetails? details, CancellationToken cancellationToken = default)
    {
        var pause = RecordPause(details);

        await WithTriggerAccessAsync(async lct =>
        {
            foreach (var group in await _storage.GetAllTriggerGroupsAsync(lct))
            {
                lct.ThrowIfCancellationRequested();
                await PauseTriggerGroupUnderLockAsync(group, pause, lct);
            }

            await _storage.AddPausedTriggerGroupAsync(AllGroupsPausedMarker, lct);
        }, cancellationToken);
    }

    public async ValueTask ResumeAll(CancellationToken cancellationToken = default)
    {
        await WithTriggerAccessAsync(async lct =>
        {
            var pausedGroups = await _storage.GetPausedTriggerGroupsAsync(lct);
            var groups = pausedGroups
                .Where(g => g != AllGroupsPausedMarker)
                .Union(await _storage.GetAllTriggerGroupsAsync(lct), StringComparer.Ordinal)
                .ToList();

            foreach (var group in groups)
            {
                lct.ThrowIfCancellationRequested();

                foreach (var triggerKey in await _storage.GetTriggersInGroupAsync(group, lct))
                {
                    lct.ThrowIfCancellationRequested();
                    await ResumeTriggerInternalAsync(triggerKey, lct);
                }

                await _storage.RemovePausedTriggerGroupAsync(group, lct);
            }

            await _storage.RemovePausedTriggerGroupAsync(AllGroupsPausedMarker, lct);
            await _storage.ClearTriggerGroupPausesAsync(lct);

            await _storage.ClearPausedJobGroupsAsync(lct);
        }, cancellationToken);
    }

    private async Task<bool> PauseTriggerInternalAsync(TriggerKey triggerKey, CancellationToken lct, NCachePauseData? pause = null)
    {
        var trigger = await _storage.GetTriggerAsync(triggerKey.Group, triggerKey.Name, lct);

        if (trigger is null)
        {
            return false;
        }

        switch (trigger.TriggerState)
        {
            case TriggerStates.Waiting:
            case TriggerStates.Acquired:
                trigger.TriggerState = TriggerStates.Paused;
                break;

            case TriggerStates.Blocked:
                trigger.TriggerState = TriggerStates.PausedAndBlocked;
                break;

            default:
                return false;
        }

        trigger.Pause = pause;
        await PersistTriggerWithIndexAsync(trigger, lct);
        return true;
    }

    private async Task<bool> PauseJobInternalAsync(JobKey jobKey, CancellationToken lct, NCachePauseData? pause = null)
    {
        if (!await _storage.JobExistsAsync(jobKey.Group, jobKey.Name, lct))
        {
            return false;
        }

        foreach (var trigger in await _storage.GetTriggersForJobAsync(jobKey.Group, jobKey.Name, lct))
        {
            lct.ThrowIfCancellationRequested();
            await PauseTriggerInternalAsync(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup), lct, pause);
        }

        return true;
    }

    private async Task<bool> ResumeTriggerInternalAsync(TriggerKey triggerKey, CancellationToken lct)
    {
        var trigger = await _storage.GetTriggerAsync(triggerKey.Group, triggerKey.Name, lct);

        if (trigger is null)
        {
            return false;
        }

        trigger.Pause = null;

        switch (trigger.TriggerState)
        {
            case TriggerStates.Paused:
                trigger.TriggerState = TriggerStates.Waiting;

                if (IsMisfired(trigger))
                {
                    await ApplyMisfireAsync(trigger, lct);
                    return true;
                }

                break;

            case TriggerStates.PausedAndBlocked:
                trigger.TriggerState = TriggerStates.Blocked;
                break;

            default:
                return false;
        }

        await PersistTriggerWithIndexAsync(trigger, lct);
        return true;
    }

    private async Task<bool> ResumeJobInternalAsync(JobKey jobKey, CancellationToken lct)
    {
        if (!await _storage.JobExistsAsync(jobKey.Group, jobKey.Name, lct))
        {
            return false;
        }

        foreach (var trigger in await _storage.GetTriggersForJobAsync(jobKey.Group, jobKey.Name, lct))
        {
            lct.ThrowIfCancellationRequested();
            await ResumeTriggerInternalAsync(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup), lct);
        }

        return true;
    }

    /// <summary>
    /// Reserves the next due triggers for this node: reads candidates from the next-fire-time index,
    /// re-checks them under TRIGGER_ACCESS, and marks each one Acquired with a firing record.
    /// </summary>
    public async ValueTask<List<IOperableTrigger>> AcquireNextTriggers(TriggerAcquisitionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.MaxCount <= 0)
        {
            return [];
        }

        var cutoff = request.NoLaterThan + request.TimeWindow;
        var candidateLimit = Math.Max(request.MaxCount * 2, request.MaxCount + 10);
        var groupFilter = ExecutionGroupFilter.From(request);

        var hints = await ReadCandidateHintsAsync(cutoff, candidateLimit, cancellationToken);

        if (hints.IsEmpty)
        {
            return [];
        }

        var acquisitions = new List<(NCacheTriggerData Before, NCacheTriggerData After, NCacheFiredTriggerData Fired)>();

        try
        {
            return await WithTriggerAccessAsync(async lct =>
            {
                var acquired = new List<IOperableTrigger>(request.MaxCount);
                var indexUpdates = new Dictionary<(string, string), NCacheTriggerData>();
                var indexRemovals = new List<(string, string, long)>();
                var rowUpdates = new Dictionary<(string, string), NCacheTriggerData>();
                var nodeLiveness = new Dictionary<string, bool>(StringComparer.Ordinal);
                var calendars = new Dictionary<string, ICalendar?>(StringComparer.Ordinal);

                ExecutionSlots? executionSlots = null;

                if (request.ExecutionLimits is not null && !request.ExecutionLimits.IsEmpty)
                {
                    IReadOnlyCollection<ExecutionGroupInFlight>? clusterInFlight = null;

                    if (request.ExecutionLimits.HasClusterScopedLimits)
                    {
                        var allFirings = await _storage.GetAllFiredTriggersAsync(lct);

                        clusterInFlight = allFirings
                            .Where(f => f.State is FiredTriggerStates.Acquired or FiredTriggerStates.Executing)
                            .GroupBy(f => new { f.ExecutionGroup, f.TriggerGroup })
                            .Select(g => new ExecutionGroupInFlight(g.Key.ExecutionGroup, g.Key.TriggerGroup, g.Count()))
                            .ToList();
                    }

                    executionSlots = request.ExecutionLimits.CreateSlots(clusterInFlight);
                }

                var nonConcurrentAcquiredInBatch = new HashSet<(string, string)>();
                var processed = new HashSet<(string, string)>();
                int skippedMissing = 0, skippedNotWaiting = 0, misfired = 0, skippedBeyondCutoff = 0, dueNowSeen = 0, deferredSeen = 0, passes = 0;

                int skippedForeign = 0;

                DateTimeOffset? batchEnd = null;

                async Task ClassifyAsync(IEnumerable<(string TriggerGroup, string TriggerName, long BucketId)> slice, DateTimeOffset now, List<NCacheTriggerData> dueNow, List<NCacheTriggerData> deferred)
                {
                    var sliceList = slice.Where(c => processed.Add((c.TriggerGroup, c.TriggerName))).ToList();

                    if (sliceList.Count == 0)
                    {
                        return;
                    }

                    var rows = await _storage.GetTriggersAsync(sliceList.Select(c => (c.TriggerGroup, c.TriggerName)), lct);

                    foreach (var candidate in sliceList)
                    {
                        lct.ThrowIfCancellationRequested();

                        var candidateKey = (candidate.TriggerGroup, candidate.TriggerName);

                        if (!rows.TryGetValue(candidateKey, out var trigger))
                        {
                            skippedMissing++;
                            indexRemovals.Add((candidate.TriggerGroup, candidate.TriggerName, candidate.BucketId));
                            continue;
                        }

                        if (trigger.IndexBucketId != candidate.BucketId)
                        {
                            indexRemovals.Add((candidate.TriggerGroup, candidate.TriggerName, candidate.BucketId));
                            indexUpdates[candidateKey] = trigger;
                            rowUpdates[candidateKey] = trigger;
                        }

                        if (trigger.TriggerState != TriggerStates.Waiting || !trigger.NextFireTimeUtc.HasValue)
                        {
                            skippedNotWaiting++;
                            indexUpdates[candidateKey] = trigger;
                            continue;
                        }

                        if (groupFilter is not null && !groupFilter.Serves(trigger.ExecutionGroup))
                        {
                            skippedForeign++;
                            continue;
                        }

                        if (IsMisfired(trigger))
                        {
                            misfired++;
                            ApplyMisfire(trigger, await GetCalendarCachedAsync(trigger.CalendarName, calendars, lct));
                            rowUpdates[candidateKey] = trigger;
                            indexUpdates[candidateKey] = trigger;

                            if (trigger.TriggerState != TriggerStates.Waiting || !trigger.NextFireTimeUtc.HasValue || trigger.NextFireTimeUtc.Value > cutoff)
                            {
                                skippedBeyondCutoff++;
                                continue;
                            }
                        }

                        if (trigger.NextFireTimeUtc!.Value > cutoff)
                        {
                            skippedBeyondCutoff++;
                            indexUpdates[candidateKey] = trigger;
                            continue;
                        }

                        if (trigger.NextFireTimeUtc.Value <= now)
                        {
                            dueNow.Add(trigger);
                        }
                        else
                        {
                            deferred.Add(trigger);
                        }
                    }
                }

                async Task AcquireFromAsync(List<NCacheTriggerData> dueNow, List<NCacheTriggerData> deferred)
                {
                    dueNow.Sort(CompareByFireTime);
                    deferred.Sort(CompareByFireTime);

                    var ordered = dueNow.Concat(deferred).ToList();

                    var unstamped = ordered.Where(t => t.Job is null).Select(t => (t.JobGroup, t.JobName)).Distinct().ToList();
                    var jobs = unstamped.Count > 0
                        ? await _storage.GetJobsAsync(unstamped, lct)
                        : new Dictionary<(string, string), NCacheJobData>();

                    foreach (var trigger in ordered)
                    {
                        lct.ThrowIfCancellationRequested();

                        if (acquired.Count >= request.MaxCount)
                        {
                            break;
                        }

                        var candidateKey = (trigger.TriggerGroup, trigger.TriggerName);

                        if (trigger.TriggerState != TriggerStates.Waiting)
                        {
                            continue;
                        }

                        if (batchEnd.HasValue && trigger.NextFireTimeUtc > batchEnd.Value)
                        {
                            continue;
                        }

                        var job = trigger.Job;

                        if (job is null)
                        {
                            if (!jobs.TryGetValue((trigger.JobGroup, trigger.JobName), out var jobRow))
                            {
                                _logger.LogError("Trigger {TriggerKey} references missing job {JobGroup}.{JobName}; setting it to ERROR.",
                                    candidateKey, trigger.JobGroup, trigger.JobName);
                                trigger.TriggerState = TriggerStates.Error;
                                rowUpdates[candidateKey] = trigger;
                                indexUpdates[candidateKey] = trigger;
                                continue;
                            }

                            job = NCacheTriggerJobFacts.From(jobRow);
                            trigger.Job = job;
                        }

                        if (IsExcludedJobType(request, job.JobClassName))
                        {
                            skippedForeign++;
                            continue;
                        }

                        if (!await MayFireOnThisNodeAsync(trigger, nodeLiveness, lct))
                        {
                            skippedForeign++;
                            continue;
                        }

                        var jobKey = (trigger.JobGroup, trigger.JobName);

                        if (job.IsNonConcurrent && (nonConcurrentAcquiredInBatch.Contains(jobKey) || await HasActiveFiringAsync(trigger.JobGroup, trigger.JobName, lct)))
                        {
                            trigger.TriggerState = TriggerStates.Blocked;
                            rowUpdates[candidateKey] = trigger;
                            indexUpdates[candidateKey] = trigger;
                            continue;
                        }

                        if (executionSlots is not null && !executionSlots.TryTake(trigger.ExecutionGroup, trigger.TriggerGroup))
                        {
                            skippedForeign++;
                            continue;
                        }

                        if (job.IsNonConcurrent)
                        {
                            nonConcurrentAcquiredInBatch.Add(jobKey);
                        }

                        var before = CloneTrigger(trigger);
                        var fireInstanceId = Guid.NewGuid().ToString("N");

                        trigger.TriggerState = TriggerStates.Acquired;

                        var fired = new NCacheFiredTriggerData
                        {
                            SchedulerName = _schedulerName,
                            EntryId = fireInstanceId,
                            TriggerName = trigger.TriggerName,
                            TriggerGroup = trigger.TriggerGroup,
                            InstanceName = _instanceId,
                            FiredTimeUtc = DateTimeOffset.UtcNow,
                            ScheduledTimeUtc = trigger.NextFireTimeUtc!.Value,
                            Priority = trigger.Priority,
                            State = FiredTriggerStates.Acquired,
                            JobName = trigger.JobName,
                            JobGroup = trigger.JobGroup,
                            IsNonConcurrent = job.IsNonConcurrent,
                            RequestsRecovery = job.RequestsRecovery,
                            ExecutionGroup = trigger.ExecutionGroup,
                            PersistJobDataAfterExecution = job.IsUpdateData,
                            RetryAttempt = trigger.RetryAttempt
                        };

                        acquisitions.Add((before, trigger, fired));
                        _acquiredJobKeys[fireInstanceId] = (trigger.JobGroup, trigger.JobName);
                        rowUpdates.Remove(candidateKey);
                        indexUpdates[candidateKey] = trigger;

                        var quartzTrigger = TriggerMapper.ToQuartzTrigger(trigger, _serialization);
                        quartzTrigger.FireInstanceId = fireInstanceId;
                        acquired.Add(quartzTrigger);

                        if (acquired.Count == 1)
                        {
                            var firstFireTime = trigger.NextFireTimeUtc!.Value;
                            var acquiredAt = DateTimeOffset.UtcNow;
                            batchEnd = (firstFireTime > acquiredAt ? firstFireTime : acquiredAt) + request.TimeWindow;
                        }
                    }
                }

                async Task RunPassAsync(NextFireTimeIndex.DueCandidates candidates)
                {
                    passes++;

                    var now = DateTimeOffset.UtcNow;
                    var dueNow = new List<NCacheTriggerData>();
                    var deferred = new List<NCacheTriggerData>();

                    await ClassifyAsync(candidates.Due.Take(request.MaxCount), now, dueNow, deferred);

                    if (dueNow.Count < request.MaxCount && candidates.Due.Count > request.MaxCount)
                    {
                        await ClassifyAsync(candidates.Due.Skip(request.MaxCount), now, dueNow, deferred);
                    }

                    await ClassifyAsync(candidates.Misfired, now, dueNow, deferred);

                    dueNowSeen += dueNow.Count;
                    deferredSeen += deferred.Count;

                    if (dueNow.Count + deferred.Count > 0 && acquired.Count < request.MaxCount)
                    {
                        await AcquireFromAsync(dueNow, deferred);
                    }
                }

                await RunPassAsync(hints);

                var lastRead = hints;
                var lastReadLimit = candidateLimit;

                if (acquired.Count == 0)
                {
                    var fresh = await ReadCandidateHintsAsync(cutoff, candidateLimit, lct);

                    if (!fresh.IsEmpty)
                    {
                        await RunPassAsync(fresh);
                        lastRead = fresh;
                    }
                }

                for (var extraRead = 0;
                     extraRead < MaxExtraCandidateReads && acquired.Count == 0 && skippedForeign > 0 && lastRead.Due.Count >= lastReadLimit;
                     extraRead++)
                {
                    var skippedBefore = skippedForeign;
                    lastReadLimit = Math.Min(lastReadLimit * 2 + skippedForeign, MaxCandidateReadLimit);
                    lastRead = await ReadCandidateHintsAsync(cutoff, lastReadLimit, lct);
                    await RunPassAsync(lastRead);

                    if (skippedForeign == skippedBefore)
                    {
                        break;
                    }
                }

                var indexWrite = await _index.PrepareApplyAsync(indexRemovals, indexUpdates.Values, lct);
                var rowsToWrite = rowUpdates.Values.Concat(acquisitions.Select(a => a.After)).ToList();
                var writes = new Dictionary<string, byte[]>(indexWrite.Inserts, StringComparer.Ordinal);

                foreach (var (key, bytes) in await _storage.PrepareTriggerUpdatesAsync(rowsToWrite, lct, verifyExistence: false))
                {
                    writes[key] = bytes;
                }

                foreach (var (key, bytes) in await _storage.PrepareFiredTriggersAsync(acquisitions.Select(a => a.Fired).ToList(), lct))
                {
                    writes[key] = bytes;
                }

                await _storage.WriteAsync(writes, indexWrite.Removes, lct);

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    var firstFireAt = acquired.Count == 0 ? null : acquired[0].NextFireTimeUtc;
                    var firstFireIn = firstFireAt.HasValue ? (long)(firstFireAt.Value - DateTimeOffset.UtcNow).TotalMilliseconds : 0;

                    _logger.LogDebug("AcquireNextTriggers on {InstanceId}: {Acquired} acquired (max {Max}) from {Due} due + {MisfiredHints} misfired hint(s) in {Passes} pass(es); first fires in {FirstFireInMs}ms; dueNow={DueNow} deferred={Deferred} misfired={Misfired}; skipped: missing={Missing} notWaiting={NotWaiting} beyondCutoff={BeyondCutoff}; {Rewritten} other row(s) rewritten, {Removed} stale index entr(ies)",
                        _instanceId, acquired.Count, request.MaxCount, hints.Due.Count, hints.Misfired.Count, passes, firstFireIn, dueNowSeen, deferredSeen, misfired, skippedMissing, skippedNotWaiting, skippedBeyondCutoff, rowUpdates.Count, indexRemovals.Count);
                }

                return acquired;
            }, cancellationToken, urgent: true);
        }
        catch when (acquisitions.Count > 0)
        {
            _logger.LogWarning("AcquireNextTriggers failed after acquiring {Count} trigger(s); releasing them.", acquisitions.Count);

            foreach (var acquisition in acquisitions)
            {
                _acquiredJobKeys.TryRemove(acquisition.Fired.EntryId, out _);
            }

            await BestEffortAsync(() => ReleaseFailedAcquisitionsAsync(acquisitions.Select(a => a.Fired).ToList()));
            throw;
        }
    }

    private async Task ReleaseFailedAcquisitionsAsync(IReadOnlyCollection<NCacheFiredTriggerData> fired)
    {
        using var timeout = new CancellationTokenSource(_locks.LockExpiration);

        await WithTriggerAccessAsync(async lct =>
        {
            await _storage.RemoveFiredTriggersAsync(fired.Select(f => f.EntryId).ToList(), lct);
            await ReleaseOrphanedAcquisitionsAsync(fired.Select(f => (f.TriggerGroup, f.TriggerName)).Distinct().ToList(), lct);

            foreach (var (jobGroup, jobName) in fired
                         .Where(f => f.IsNonConcurrent && !string.IsNullOrWhiteSpace(f.JobGroup) && !string.IsNullOrWhiteSpace(f.JobName))
                         .Select(f => (f.JobGroup!, f.JobName!))
                         .Distinct())
            {
                var job = await _storage.GetJobAsync(jobGroup, jobName, lct);

                if (job is { IsNonConcurrent: true } && !await HasActiveFiringAsync(job, lct))
                {
                    await UnblockJobTriggersAsync(job, lct);
                }
            }
        }, timeout.Token, nameof(AcquireNextTriggers));
    }

    private async Task<int> ReleaseOrphanedAcquisitionsAsync(IReadOnlyCollection<(string TriggerGroup, string TriggerName)>? triggerKeys, CancellationToken lct)
    {
        IEnumerable<NCacheTriggerData> rows = triggerKeys is null
            ? await _storage.GetAllTriggersAsync(lct)
            : (await _storage.GetTriggersAsync(triggerKeys, lct)).Values;

        var acquired = rows.Where(t => t.TriggerState == TriggerStates.Acquired).ToList();

        if (acquired.Count == 0)
        {
            return 0;
        }

        var held = (await _storage.GetAllFiredTriggersAsync(lct))
            .Select(f => (f.TriggerGroup, f.TriggerName))
            .ToHashSet();

        var orphans = acquired.Where(t => !held.Contains((t.TriggerGroup, t.TriggerName))).ToList();

        if (orphans.Count == 0)
        {
            return 0;
        }

        foreach (var trigger in orphans)
        {
            trigger.TriggerState = TriggerStates.Waiting;
        }

        await _index.UpsertManyAsync(orphans, lct);
        await _storage.UpdateTriggersAsync(orphans, lct);

        _logger.LogWarning("Released {Count} trigger(s) left ACQUIRED without a firing record: {Triggers}",
            orphans.Count, string.Join(", ", orphans.Select(t => $"{t.TriggerGroup}.{t.TriggerName}")));

        return orphans.Count;
    }

    private const int MaxExtraCandidateReads = 4;
    private const int MaxCandidateReadLimit = 1000;

    private Task<NextFireTimeIndex.DueCandidates> ReadCandidateHintsAsync(DateTimeOffset cutoff, int candidateLimit, CancellationToken ct)
        => _index.GetDueCandidatesAsync(cutoff, DateTimeOffset.UtcNow - _misfireThreshold, candidateLimit, candidateLimit, ct);

    private static int CompareByFireTime(NCacheTriggerData left, NCacheTriggerData right)
    {
        var byFireTime = Nullable.Compare(left.NextFireTimeUtc, right.NextFireTimeUtc);
        return byFireTime != 0 ? byFireTime : right.Priority.CompareTo(left.Priority);
    }

    private async Task<ICalendar?> GetCalendarCachedAsync(string? calendarName, Dictionary<string, ICalendar?> cache, CancellationToken lct)
    {
        if (string.IsNullOrWhiteSpace(calendarName))
        {
            return null;
        }

        if (!cache.TryGetValue(calendarName, out var calendar))
        {
            calendar = await GetCalendarAsync(calendarName, lct);
            cache[calendarName] = calendar;
        }

        return calendar;
    }

    private async Task<bool> MayFireOnThisNodeAsync(NCacheTriggerData trigger, Dictionary<string, bool> nodeLiveness, CancellationToken lct)
    {
        if (string.IsNullOrWhiteSpace(trigger.PreferredNode))
        {
            if (trigger.PreferredNodeIsAutomatic)
            {
                trigger.PreferredNode = _instanceId;
            }

            return true;
        }

        if (string.Equals(trigger.PreferredNode, _instanceId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!nodeLiveness.TryGetValue(trigger.PreferredNode, out var alive))
        {
            alive = await IsSchedulerNodeAliveAsync(trigger.PreferredNode, lct);
            nodeLiveness[trigger.PreferredNode] = alive;
        }

        if (alive)
        {
            return false;
        }

        if (trigger.PreferredNodeIsAutomatic)
        {
            trigger.PreferredNode = _instanceId;
        }

        return true;
    }

    public async ValueTask ReleaseAcquiredTrigger(IOperableTrigger trigger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        var hasFireInstance = !string.IsNullOrWhiteSpace(trigger.FireInstanceId);

        if (hasFireInstance)
        {
            _acquiredJobKeys.TryRemove(trigger.FireInstanceId!, out _);
        }

        var recordRemoved = false;
        var recordLostMidway = false;
        var attempts = 0;

        await RetryUntilDoneAsync(nameof(ReleaseAcquiredTrigger), cancellationToken, () => WithTriggerAccessAsync(async lct =>
        {
            attempts++;

            if (hasFireInstance && !recordRemoved)
            {
                if (!await _storage.RemoveFiredTriggerAsync(trigger.FireInstanceId!, lct))
                {
                    if (attempts == 1)
                    {
                        return;
                    }

                    recordLostMidway = true;
                }

                recordRemoved = true;
            }

            var storedTrigger = await _storage.GetTriggerAsync(trigger.Key.Group, trigger.Key.Name, lct);

            if (recordLostMidway)
            {
                await ReleaseOrphanedAcquisitionsAsync(new[] { (trigger.Key.Group, trigger.Key.Name) }, lct);
            }
            else if (storedTrigger is not null && storedTrigger.TriggerState == TriggerStates.Acquired)
            {
                storedTrigger.TriggerState = TriggerStates.Waiting;
                await PersistTriggerWithIndexAsync(storedTrigger, lct);
            }

            var jobGroup = storedTrigger?.JobGroup ?? trigger.JobKey?.Group;
            var jobName = storedTrigger?.JobName ?? trigger.JobKey?.Name;

            if (jobGroup is not null && jobName is not null)
            {
                var job = await _storage.GetJobAsync(jobGroup, jobName, lct);

                if (job is { IsNonConcurrent: true } && !await HasActiveFiringAsync(job, lct))
                {
                    await UnblockJobTriggersAsync(job, lct);
                }
            }
        }, cancellationToken), MaxReleaseAttempts);
    }

    private async Task RetryUntilDoneAsync(string operation, CancellationToken cancellationToken, Func<Task> attempt, int? maxAttempts = null)
    {
        for (var attemptNumber = 1; ; attemptNumber++)
        {
            try
            {
                _repairSuppressed.Value = attemptNumber > 1;
                await attempt();
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && !_retryStop.IsCancellationRequested &&
                                       (maxAttempts is null || attemptNumber < maxAttempts.Value))
            {
                var delay = TimeSpan.FromMilliseconds(Math.Min(MaxRetryDelay.TotalMilliseconds, 100 * Math.Pow(2, Math.Min(attemptNumber - 1, 10))));

                _logger.LogWarning(ex, "{Operation} failed (attempt {Attempt}); retrying in {DelayMs} ms.", operation, attemptNumber, (long) delay.TotalMilliseconds);

                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _retryStop.Token);

                try
                {
                    await Task.Delay(delay, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
                }
            }
        }
    }

    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(15);

    private const int MaxReleaseAttempts = 4;

    private readonly AsyncLocal<bool> _repairSuppressed = new();

    /// <summary>
    /// Fires acquired triggers: applies the overlap policy, advances each trigger to its next fire
    /// time and marks its firing record Executing, all in one bulk write.
    /// </summary>
    public async ValueTask<List<TriggerFiredResult>> TriggersFired(IReadOnlyCollection<IOperableTrigger> triggers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggers);

        try
        {
            return await TriggersFiredCoreAsync(triggers, cancellationToken);
        }
        catch (Exception ex) when (ex is not SchedulerException)
        {
            throw new JobPersistenceException($"TriggersFired failed: {ex.Message}", ex);
        }
    }

    private async Task<List<TriggerFiredResult>> TriggersFiredCoreAsync(IReadOnlyCollection<IOperableTrigger> triggers, CancellationToken cancellationToken)
    {
        return await WithTriggerAccessAsync(async lct =>
        {
            var results = new TriggerFiredResult[triggers.Count];
            var indexUpdates = new List<NCacheTriggerData>();
            var calendars = new Dictionary<string, ICalendar?>(StringComparer.Ordinal);

            var input = triggers.ToList();
            var fireInstanceIds = input.Where(t => !string.IsNullOrWhiteSpace(t.FireInstanceId)).Select(t => t.FireInstanceId!).ToList();

            var knownJobKeys = new List<(string, string)>();

            foreach (var id in fireInstanceIds)
            {
                if (_acquiredJobKeys.TryRemove(id, out var jobKey))
                {
                    knownJobKeys.Add(jobKey);
                }
            }

            var read = await _storage.ReadForFiringAsync(input.Select(t => (t.Key.Group, t.Key.Name)), fireInstanceIds, knownJobKeys, lct);
            var pausedGroups = read.PausedTriggerGroups;
            var storedRows = read.Triggers;
            var acquisitionRecords = read.Firings;

            var firing = new List<(int Index, IOperableTrigger Trigger, NCacheTriggerData Stored, string FireInstanceId)>();

            for (var i = 0; i < input.Count; i++)
            {
                lct.ThrowIfCancellationRequested();
                var trigger = input[i];

                if (!storedRows.TryGetValue((trigger.Key.Group, trigger.Key.Name), out var storedTrigger) ||
                    storedTrigger.TriggerState != TriggerStates.Acquired)
                {
                    results[i] = TriggerFiredResult.NotFired;
                    continue;
                }

                var fireInstanceId = trigger.FireInstanceId
                    ?? throw new JobPersistenceException($"Trigger '{trigger.Key}' has no FireInstanceId.");

                if (!acquisitionRecords.TryGetValue(fireInstanceId, out var acquisition) ||
                    acquisition.State != FiredTriggerStates.Acquired ||
                    !string.Equals(acquisition.InstanceName, _instanceId, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Refusing to fire {TriggerKey}: fire instance {FireInstanceId} is no longer owned by {InstanceId}.",
                        trigger.Key, fireInstanceId, _instanceId);
                    results[i] = TriggerFiredResult.NotFired;
                    continue;
                }

                firing.Add((i, trigger, storedTrigger, fireInstanceId));
            }

            IReadOnlyDictionary<(string JobGroup, string JobName), NCacheJobData> jobs = read.Jobs;
            var missingJobKeys = firing.Select(f => (f.Stored.JobGroup, f.Stored.JobName)).Distinct().Where(k => !jobs.ContainsKey(k)).ToList();

            if (missingJobKeys.Count > 0)
            {
                var merged = new Dictionary<(string, string), NCacheJobData>(jobs);

                foreach (var (key, job) in await _storage.GetJobsAsync(missingJobKeys, lct))
                {
                    merged[key] = job;
                }

                jobs = merged;
            }

            var rowWrites = new List<(NCacheTriggerData Before, NCacheTriggerData After)>();
            var recordWrites = new List<NCacheFiredTriggerData>();
            var pendingBundles = new List<(int Index, TriggerFiredBundle Bundle, NCacheJobData Job, TriggerKey Key)>();

            IReadOnlyList<NCacheFiredTriggerData>? allFirings = null;
            IReadOnlyCollection<string>? pausedJobGroups = null;

            var declinedRecordIds = new List<string>();

            foreach (var (i, trigger, storedTrigger, fireInstanceId) in firing)
            {
                lct.ThrowIfCancellationRequested();

                if (!jobs.TryGetValue((storedTrigger.JobGroup, storedTrigger.JobName), out var storedJob))
                {
                    results[i] = TriggerFiredResult.NotFired;
                    continue;
                }

                IJobDetail jobDetail;

                try
                {
                    jobDetail = JobMapper.ToQuartzJob(storedJob);
                }
                catch (JobPersistenceException ex)
                {
                    _logger.LogError(ex, "Job {JobGroup}.{JobName} of trigger {TriggerKey} cannot be loaded; setting the trigger to ERROR.",
                        storedJob.JobGroup, storedJob.JobName, trigger.Key);

                    var failedBefore = CloneTrigger(storedTrigger);
                    storedTrigger.TriggerState = TriggerStates.Error;
                    rowWrites.Add((failedBefore, storedTrigger));
                    indexUpdates.Add(storedTrigger);
                    results[i] = TriggerFiredResult.Failed(ex);

                    var errorKey = trigger.Key;
                    QueueSignal((signaler, ct) => signaler.NotifySchedulerListenersTriggerInError(errorKey, ct));
                    continue;
                }

                ICalendar? calendar = null;

                if (!string.IsNullOrWhiteSpace(storedTrigger.CalendarName))
                {
                    calendar = await GetCalendarCachedAsync(storedTrigger.CalendarName, calendars, lct);

                    if (calendar is null)
                    {
                        results[i] = TriggerFiredResult.NotFired;
                        continue;
                    }
                }

                var overlapPolicy = (OverlapPolicy) storedTrigger.OverlapPolicy;
                List<string>? superseded = null;

                if (overlapPolicy is OverlapPolicy.Skip or OverlapPolicy.CancelPrevious &&
                    storedTrigger.RetryAttempt == 0 &&
                    !storedJob.IsNonConcurrent)
                {
                    allFirings ??= await _storage.GetAllFiredTriggersAsync(lct);

                    var running = allFirings
                        .Where(f => f.State == FiredTriggerStates.Executing &&
                                    f.TriggerGroup == storedTrigger.TriggerGroup &&
                                    f.TriggerName == storedTrigger.TriggerName &&
                                    f.EntryId != fireInstanceId)
                        .ToList();

                    if (running.Count == 0)
                    {
                        storedTrigger.OverlapPolicyUnsettled = false;
                    }
                    else if (overlapPolicy == OverlapPolicy.Skip)
                    {
                        _logger.LogInformation("Firing of trigger {TriggerKey} due at {FireTime} skipped: an earlier firing of it is still running and its overlap policy is Skip.",
                            trigger.Key, trigger.NextFireTimeUtc);

                        var skipped = (ITrigger) trigger.Clone();
                        QueueSignal((signaler, ct) => signaler.NotifyTriggerListenersSkipped(skipped, ct));

                        var skippedBefore = CloneTrigger(storedTrigger);
                        trigger.Triggered(calendar);
                        TriggerMapper.ApplyScheduleState(storedTrigger, trigger, _serialization);

                        if (!storedTrigger.NextFireTimeUtc.HasValue)
                        {
                            storedTrigger.TriggerState = TriggerStates.Complete;
                            var finalized = (ITrigger) trigger.Clone();
                            QueueSignal((signaler, ct) => signaler.NotifySchedulerListenersFinalized(finalized, ct));
                        }
                        else
                        {
                            storedTrigger.TriggerState = IsTriggerGroupPaused(pausedGroups, storedTrigger.TriggerGroup)
                                ? TriggerStates.Paused
                                : TriggerStates.Waiting;
                        }

                        rowWrites.Add((skippedBefore, storedTrigger));
                        indexUpdates.Add(storedTrigger);
                        declinedRecordIds.Add(fireInstanceId);
                        QueueSignal((signaler, ct) => signaler.SignalSchedulingChange(null, ct));
                        results[i] = TriggerFiredResult.Declined;
                        continue;
                    }
                    else
                    {
                        var elsewhere = running.FirstOrDefault(f => !string.Equals(f.InstanceName, _instanceId, StringComparison.Ordinal));

                        if (elsewhere is not null && !storedTrigger.OverlapPolicyUnsettled)
                        {
                            _logger.LogInformation("Firing of trigger {TriggerKey} held until its running firing on {Owner} ends: CancelPrevious cannot interrupt a firing on another node.",
                                trigger.Key, elsewhere.InstanceName);

                            pausedJobGroups ??= await _storage.GetPausedJobGroupsAsync(lct);

                            var heldBefore = CloneTrigger(storedTrigger);
                            storedTrigger.TriggerState =
                                IsTriggerGroupPaused(pausedGroups, storedTrigger.TriggerGroup) ||
                                pausedJobGroups.Contains(storedTrigger.JobGroup, StringComparer.Ordinal)
                                    ? TriggerStates.PausedAndBlocked
                                    : TriggerStates.Blocked;

                            rowWrites.Add((heldBefore, storedTrigger));
                            indexUpdates.Add(storedTrigger);
                            declinedRecordIds.Add(fireInstanceId);
                            results[i] = TriggerFiredResult.Declined;
                            continue;
                        }

                        var local = running
                            .Where(f => string.Equals(f.InstanceName, _instanceId, StringComparison.Ordinal))
                            .Select(f => f.EntryId)
                            .ToList();

                        superseded = local.Count > 0 ? local : null;
                    }
                }

                var before = CloneTrigger(storedTrigger);
                var previousFireTime = trigger.PreviousFireTimeUtc;
                var firingScheduledTime = trigger.NextFireTimeUtc;

                var isRetry = storedTrigger.RetryAttempt > 0 && trigger is TriggerBase;

                if (isRetry)
                {
                    ((TriggerBase)trigger).RetryFired(calendar);
                    firingScheduledTime = trigger.PreviousFireTimeUtc ?? firingScheduledTime;
                }
                else
                {
                    trigger.Triggered(calendar);
                }

                TriggerMapper.ApplyScheduleState(storedTrigger, trigger, _serialization);

                if (!storedTrigger.NextFireTimeUtc.HasValue)
                {
                    storedTrigger.TriggerState = TriggerStates.Complete;
                }
                else if (storedJob.IsNonConcurrent)
                {
                    storedTrigger.TriggerState = TriggerStates.Blocked;
                }
                else if (overlapPolicy == OverlapPolicy.BufferOne)
                {
                    storedTrigger.TriggerState = TriggerStates.Blocked;
                }
                else
                {
                    storedTrigger.TriggerState = IsTriggerGroupPaused(pausedGroups, storedTrigger.TriggerGroup)
                        ? TriggerStates.Paused
                        : TriggerStates.Waiting;
                }

                var fired = new NCacheFiredTriggerData
                {
                    SchedulerName = _schedulerName,
                    EntryId = fireInstanceId,
                    TriggerName = storedTrigger.TriggerName,
                    TriggerGroup = storedTrigger.TriggerGroup,
                    InstanceName = _instanceId,
                    FiredTimeUtc = DateTimeOffset.UtcNow,
                    ScheduledTimeUtc = firingScheduledTime ?? DateTimeOffset.UtcNow,
                    Priority = storedTrigger.Priority,
                    State = FiredTriggerStates.Executing,
                    JobName = storedTrigger.JobName,
                    JobGroup = storedTrigger.JobGroup,
                    IsNonConcurrent = storedJob.IsNonConcurrent,
                    RequestsRecovery = storedJob.RequestsRecovery,
                    ExecutionGroup = storedTrigger.ExecutionGroup,
                    PersistJobDataAfterExecution = storedJob.IsUpdateData,
                    RetryAttempt = storedTrigger.RetryAttempt,
                    OverlapPolicy = storedTrigger.OverlapPolicy
                };

                rowWrites.Add((before, storedTrigger));
                recordWrites.Add(fired);
                indexUpdates.Add(storedTrigger);

                var bundle = new TriggerFiredBundle
                {
                    JobDetail = jobDetail,
                    Trigger = trigger,
                    Calendar = calendar,
                    Recovering = string.Equals(storedTrigger.TriggerGroup, SchedulerConstants.DefaultRecoveryGroup, StringComparison.Ordinal),
                    FireTimeUtc = fired.FiredTimeUtc,
                    ScheduledFireTimeUtc = firingScheduledTime,
                    PreviousFireTimeUtc = previousFireTime,
                    NextFireTimeUtc = trigger.NextFireTimeUtc,
                    SupersededFireInstanceIds = superseded
                };

                pendingBundles.Add((i, bundle, storedJob, trigger.Key));
            }

            var firingKeys = pendingBundles.Select(p => (p.Key.Group, p.Key.Name)).ToHashSet();

            foreach (var nonConcurrentJob in pendingBundles.Where(p => p.Job.IsNonConcurrent).Select(p => p.Job).DistinctBy(j => (j.JobGroup, j.JobName)))
            {
                foreach (var sibling in await _storage.GetTriggersForJobAsync(nonConcurrentJob.JobGroup, nonConcurrentJob.JobName, lct))
                {
                    lct.ThrowIfCancellationRequested();

                    if (firingKeys.Contains((sibling.TriggerGroup, sibling.TriggerName)))
                    {
                        continue;
                    }

                    var blockedState = sibling.TriggerState switch
                    {
                        TriggerStates.Waiting => TriggerStates.Blocked,
                        TriggerStates.Paused => TriggerStates.PausedAndBlocked,
                        _ => null
                    };

                    if (blockedState is null)
                    {
                        continue;
                    }

                    var siblingBefore = CloneTrigger(sibling);
                    sibling.TriggerState = blockedState;
                    rowWrites.Add((siblingBefore, sibling));
                    indexUpdates.Add(sibling);
                }
            }

            var indexWrite = await _index.PrepareUpsertManyAsync(indexUpdates, lct);
            var writes = new Dictionary<string, byte[]>(indexWrite.Inserts, StringComparer.Ordinal);

            foreach (var (key, bytes) in await _storage.PrepareTriggerUpdatesAsync(rowWrites.Select(w => w.After).ToList(), lct, verifyExistence: false))
            {
                writes[key] = bytes;
            }

            foreach (var (key, bytes) in _storage.PrepareFiredTriggerUpdates(recordWrites))
            {
                writes[key] = bytes;
            }

            try
            {
                await _storage.WriteAsync(writes, indexWrite.Removes, lct);
            }
            catch
            {
                await BestEffortAsync(() => _index.RemoveManyAsync(rowWrites.Select(w => w.After), lct));
                await BestEffortAsync(() => _index.UpsertManyAsync(rowWrites.Select(w => w.Before), lct));
                await BestEffortAsync(() => _storage.UpdateTriggersAsync(rowWrites.Select(w => w.Before).ToList(), lct));
                throw;
            }

            if (declinedRecordIds.Count > 0)
            {
                await _storage.RemoveFiredTriggersAsync(declinedRecordIds, lct);
            }

            foreach (var (i, bundle, _, _) in pendingBundles)
            {
                results[i] = TriggerFiredResult.Fired(bundle);
            }

            return results.ToList();
        }, cancellationToken, urgent: true);
    }

    public ValueTask TriggeredJobComplete(IOperableTrigger trigger, IJobDetail jobDetail, SchedulerInstruction triggerInstructionCode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(jobDetail);

        return FiringComplete(new TriggeredJobCompleteContext
        {
            Trigger = trigger,
            JobDetail = jobDetail,
            Instruction = triggerInstructionCode
        }, cancellationToken);
    }

    /// <summary>
    /// Records that a run ended: removes its firing record, settles continuations by the outcome and
    /// applies Quartz's instruction. Batched with other completions and retried until it succeeds.
    /// </summary>
    public async ValueTask FiringComplete(TriggeredJobCompleteContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var trigger = context.Trigger;
        var triggerInstructionCode = context.Instruction;

        if (!Enum.IsDefined(triggerInstructionCode))
        {
            throw new ArgumentOutOfRangeException(nameof(context), triggerInstructionCode, "Unknown Quartz scheduler instruction.");
        }

        EnsureInitialized();

        await RetryUntilDoneAsync(nameof(FiringComplete), cancellationToken, async () =>
        {
            if (triggerInstructionCode is SchedulerInstruction.NoInstruction or SchedulerInstruction.ReExecuteJob or SchedulerInstruction.DeleteTrigger &&
                !string.IsNullOrWhiteSpace(trigger.FireInstanceId))
            {
                await CompleteThroughBatchAsync(new PendingCompletion(context), cancellationToken);
                return;
            }

            await WithTriggerAccessAsync(async lct =>
            {
                NCacheFiredTriggerData? fired = null;

                if (!string.IsNullOrWhiteSpace(trigger.FireInstanceId))
                {
                    fired = await _storage.RemoveFiredTriggerAndGetAsync(trigger.FireInstanceId, lct);
                }

                await FinishCompletionAsync(context, fired, knownAwaiting: null, lct);
            }, cancellationToken);
        });

        if (_signaler is not null)
        {
            try
            {
                if (triggerInstructionCode == SchedulerInstruction.SetTriggerError)
                {
                    await _signaler.NotifySchedulerListenersTriggerInError(trigger.Key, CancellationToken.None);
                }
                else if (triggerInstructionCode == SchedulerInstruction.SetAllJobTriggersError)
                {
                    await _signaler.NotifySchedulerListenersTriggersInError(trigger.JobKey, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A scheduler listener failed while being told {TriggerKey} is in error.", trigger.Key);
            }
        }
    }

    private async Task FinishCompletionAsync(TriggeredJobCompleteContext context, NCacheFiredTriggerData? fired, AwaitingByParent? knownAwaiting, CancellationToken lct)
    {
        var trigger = context.Trigger;
        var jobDetail = context.JobDetail;
        var instruction = context.Instruction;

        NCacheJobData? storedJob = null;
        var jobLoaded = false;

        async Task<NCacheJobData?> LoadJobAsync()
        {
            if (!jobLoaded)
            {
                storedJob = await _storage.GetJobAsync(jobDetail.Key.Group, jobDetail.Key.Name, lct);
                jobLoaded = true;
            }

            return storedJob;
        }

        if (fired is null || fired.PersistJobDataAfterExecution || jobDetail.PersistJobDataAfterExecution)
        {
            var job = await LoadJobAsync();

            if (job is not null && (job.IsUpdateData || jobDetail.PersistJobDataAfterExecution))
            {
                Dictionary<string, object?>? map = null;

                try
                {
                    map = JobDataMapMapper.ToStorageMap(jobDetail.JobDataMap, _serialization);
                }
                catch (JobPersistenceException ex)
                {
                    _logger.LogError(ex, "The JobDataMap of job {JobKey} changed by this run cannot be persisted; keeping the previously stored data.", jobDetail.Key);
                }

                if (map is not null)
                {
                    job.JobDataMap = map;
                    await _storage.UpdateJobAsync(job, lct);
                }
            }
        }

        var continuationsSettled = instruction != SchedulerInstruction.RetryTrigger &&
                                   await SettleContinuationsAsync(trigger.Key, context.Outcome, knownAwaiting, lct);

        await ApplyCompletionInstructionAsync(trigger, instruction, fired, LoadJobAsync, continuationsSettled, lct);

        if (fired is null || fired.IsNonConcurrent)
        {
            var job = await LoadJobAsync();

            if (job is not null && job.IsNonConcurrent && !await HasActiveFiringAsync(job, lct))
            {
                await UnblockJobTriggersAsync(job, lct);
            }
        }

        if (trigger.OverlapPolicy is OverlapPolicy.BufferOne or OverlapPolicy.CancelPrevious &&
            !(fired?.IsNonConcurrent ?? jobDetail.ConcurrentExecutionDisallowed))
        {
            await ReleaseOverlapHoldAsync(trigger.Key, lct);
        }
    }

    private async Task ReleaseOverlapHoldAsync(TriggerKey triggerKey, CancellationToken lct)
    {
        var row = await _storage.GetTriggerAsync(triggerKey.Group, triggerKey.Name, lct);

        if (row is null)
        {
            return;
        }

        if (row.TriggerState == TriggerStates.PausedAndBlocked)
        {
            row.TriggerState = TriggerStates.Paused;
            await PersistTriggerWithIndexAsync(row, lct);
            return;
        }

        if (row.TriggerState != TriggerStates.Blocked)
        {
            return;
        }

        row.TriggerState = TriggerStates.Waiting;

        if (IsMisfired(row))
        {
            var calendar = string.IsNullOrWhiteSpace(row.CalendarName) ? null : await GetCalendarAsync(row.CalendarName, lct);
            ApplyMisfire(row, calendar);

            if (row.TriggerState == TriggerStates.Complete)
            {
                await DeleteTriggerInternalAsync(row, deleteOrphanJob: true, lct);
                return;
            }
        }

        await PersistTriggerWithIndexAsync(row, lct);
        QueueSignal((signaler, ct) => signaler.SignalSchedulingChange(null, ct));
    }

    public async ValueTask UpdateFireInstanceProgress(string fireInstanceId, FireInstanceProgress progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fireInstanceId);
        ArgumentNullException.ThrowIfNull(progress);

        await WithTriggerAccessAsync(async lct =>
        {
            var fired = await _storage.GetFiredTriggerAsync(fireInstanceId, lct);

            if (fired is null || fired.State != FiredTriggerStates.Executing)
            {
                return;
            }

            fired.Progress = Math.Clamp(progress.Percent, 0, 100);
            fired.ProgressMessage = CutText(progress.Message, FireInstanceProgress.MaxMessageLength);
            await _storage.UpdateFiredTriggerAsync(fired, lct);
        }, cancellationToken);
    }

    private async Task ApplyCompletionInstructionAsync(
        IOperableTrigger trigger,
        SchedulerInstruction instruction,
        NCacheFiredTriggerData? fired,
        Func<Task<NCacheJobData?>> loadJobAsync,
        bool continuationsSettled,
        CancellationToken lct)
    {
        NCacheTriggerData? storedTrigger;

        switch (instruction)
        {
            case SchedulerInstruction.NoInstruction:
            case SchedulerInstruction.ReExecuteJob:
                if (!trigger.NextFireTimeUtc.HasValue || trigger.OverlapPolicy == OverlapPolicy.Skip)
                {
                    storedTrigger = await _storage.GetTriggerAsync(trigger.Key.Group, trigger.Key.Name, lct);

                    if (storedTrigger is not null && !storedTrigger.NextFireTimeUtc.HasValue)
                    {
                        await DeleteTriggerInternalAsync(storedTrigger, deleteOrphanJob: true, lct, settleContinuations: !continuationsSettled);
                        break;
                    }
                }

                if (fired is not null && fired.RetryAttempt == trigger.RetryAttempt)
                {
                    break;
                }

                storedTrigger = await _storage.GetTriggerAsync(trigger.Key.Group, trigger.Key.Name, lct);

                if (storedTrigger is not null && storedTrigger.RetryAttempt != trigger.RetryAttempt)
                {
                    storedTrigger.RetryAttempt = trigger.RetryAttempt;
                    await _storage.UpdateTriggerAsync(storedTrigger, lct);
                }

                break;

            case SchedulerInstruction.DeleteTrigger:
                storedTrigger = await _storage.GetTriggerAsync(trigger.Key.Group, trigger.Key.Name, lct);
                if (storedTrigger is null) return;

                if (!trigger.NextFireTimeUtc.HasValue && storedTrigger.NextFireTimeUtc.HasValue)
                {
                    break;
                }

                await DeleteTriggerInternalAsync(storedTrigger, deleteOrphanJob: true, lct, settleContinuations: !continuationsSettled);
                break;

            case SchedulerInstruction.SetTriggerComplete:
                storedTrigger = await _storage.GetTriggerAsync(trigger.Key.Group, trigger.Key.Name, lct);
                if (storedTrigger is null) return;
                storedTrigger.TriggerState = TriggerStates.Complete;
                await PersistTriggerWithIndexAsync(storedTrigger, lct);
                break;

            case SchedulerInstruction.SetTriggerError:
                storedTrigger = await _storage.GetTriggerAsync(trigger.Key.Group, trigger.Key.Name, lct);
                if (storedTrigger is null) return;
                storedTrigger.TriggerState = TriggerStates.Error;
                await PersistTriggerWithIndexAsync(storedTrigger, lct);
                break;

            case SchedulerInstruction.SetAllJobTriggersError:
                await SetAllJobTriggersStateAsync(await loadJobAsync(), TriggerStates.Error, lct);
                break;

            case SchedulerInstruction.SetAllJobTriggersComplete:
                await SetAllJobTriggersStateAsync(await loadJobAsync(), TriggerStates.Complete, lct);
                break;

            case SchedulerInstruction.RetryTrigger:
                storedTrigger = await _storage.GetTriggerAsync(trigger.Key.Group, trigger.Key.Name, lct);
                if (storedTrigger is null) return;
                await ScheduleRetryAsync(storedTrigger, trigger, lct);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(instruction), instruction, "Unknown Quartz scheduler instruction.");
        }
    }

    private sealed class PendingCompletion
    {
        public PendingCompletion(TriggeredJobCompleteContext context)
        {
            Context = context;
            FireInstanceId = context.Trigger.FireInstanceId!;
        }

        public TriggeredJobCompleteContext Context { get; }
        public IOperableTrigger Trigger => Context.Trigger;
        public IJobDetail JobDetail => Context.JobDetail;
        public SchedulerInstruction Instruction => Context.Instruction;
        public ExecutionOutcome Outcome => Context.Outcome;
        public string FireInstanceId { get; }
        public Exception? Failure { get; set; }
        public bool Processed { get; set; }
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<PendingCompletion> _pendingCompletions = new();
    private readonly SemaphoreSlim _completionLeader = new(1, 1);

    private async Task CompleteThroughBatchAsync(PendingCompletion pending, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _pendingCompletions.Enqueue(pending);

        while (true)
        {
            var lead = _completionLeader.WaitAsync();
            var first = await Task.WhenAny(pending.Done.Task, lead).ConfigureAwait(false);

            if (first == pending.Done.Task)
            {
                _ = LeadWhenGrantedAsync(lead);

                await pending.Done.Task.ConfigureAwait(false);
                return;
            }

            await lead.ConfigureAwait(false);

            try
            {
                await DrainPendingCompletionsAsync().ConfigureAwait(false);
            }
            finally
            {
                _completionLeader.Release();
            }

            if (pending.Done.Task.IsCompleted)
            {
                await pending.Done.Task.ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task LeadWhenGrantedAsync(Task lead)
    {
        await lead.ConfigureAwait(false);

        try
        {
            await DrainPendingCompletionsAsync().ConfigureAwait(false);
        }
        finally
        {
            _completionLeader.Release();
        }
    }

    private const int MaxCompletionsPerBatch = 500;

    private async Task DrainPendingCompletionsAsync()
    {
        if (_pendingCompletions.IsEmpty)
        {
            return;
        }

        await Task.Yield();

        var batch = new List<PendingCompletion>();

        while (batch.Count < MaxCompletionsPerBatch && _pendingCompletions.TryDequeue(out var pending))
        {
            batch.Add(pending);
        }

        if (batch.Count == 0)
        {
            return;
        }

        Exception? batchFailure = null;

        try
        {
            await WithTriggerAccessAsync(async lct =>
            {
                var removed = await _storage.RemoveFiredTriggersAsync(batch.Select(p => p.FireInstanceId).ToList(), lct);

                var knownAwaiting = new AwaitingByParent(await _storage.GetAwaitingContinuationsAsync(
                    batch.Select(p => (p.Trigger.Key.Group, p.Trigger.Key.Name)).Distinct(), lct));

                await DeleteSpentTriggersAsync(batch, removed, knownAwaiting, lct);
                Exception? firstUnexpected = null;

                foreach (var pending in batch)
                {
                    if (pending.Processed)
                    {
                        continue;
                    }

                    lct.ThrowIfCancellationRequested();
                    removed.TryGetValue(pending.FireInstanceId, out var fired);

                    try
                    {
                        await FinishCompletionAsync(pending.Context, fired, knownAwaiting, lct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        pending.Failure = ex;
                        firstUnexpected ??= IsUnexpectedFailure(ex) ? ex : null;
                    }

                    pending.Processed = true;
                }

                if (firstUnexpected is not null)
                {
                    throw firstUnexpected;
                }
            }, CancellationToken.None, nameof(FiringComplete));
        }
        catch (Exception ex)
        {
            batchFailure = ex;
        }

        foreach (var pending in batch)
        {
            if (pending.Failure is not null)
            {
                pending.Done.TrySetException(pending.Failure);
            }
            else if (!pending.Processed && batchFailure is not null)
            {
                pending.Done.TrySetException(batchFailure);
            }
            else
            {
                pending.Done.TrySetResult();
            }
        }
    }

    private async Task DeleteSpentTriggersAsync(List<PendingCompletion> batch, IReadOnlyDictionary<string, NCacheFiredTriggerData> removed, AwaitingByParent knownAwaiting, CancellationToken lct)
    {
        var spent = batch.Where(p =>
            p.Instruction == SchedulerInstruction.DeleteTrigger &&
            removed.TryGetValue(p.FireInstanceId, out var fired) &&
            !fired.IsNonConcurrent &&
            !fired.PersistJobDataAfterExecution &&
            !p.JobDetail.PersistJobDataAfterExecution).ToList();

        if (spent.Count == 0)
        {
            return;
        }

        var stored = await _storage.GetTriggersAsync(spent.Select(p => (p.Trigger.Key.Group, p.Trigger.Key.Name)), lct);
        var toDelete = new Dictionary<(string, string), NCacheTriggerData>();

        foreach (var pending in spent)
        {
            if (stored.TryGetValue((pending.Trigger.Key.Group, pending.Trigger.Key.Name), out var row) &&
                (pending.Trigger.NextFireTimeUtc.HasValue || !row.NextFireTimeUtc.HasValue))
            {
                toDelete[(row.TriggerGroup, row.TriggerName)] = row;

                if (!await SettleContinuationsAsync(pending.Trigger.Key, pending.Outcome, knownAwaiting, lct))
                {
                    await SettleContinuationsOfDeletedParentAsync(pending.Trigger.Key, knownAwaiting, lct);
                }
            }
        }

        if (toDelete.Count > 0)
        {
            await _storage.RemoveTriggersAsync(toDelete.Values, lct);
            await _index.RemoveManyAsync(toDelete.Values, lct);

            var jobs = await _storage.GetJobsAsync(toDelete.Values.Select(t => (t.JobGroup, t.JobName)), lct);
            var candidates = jobs.Values.Where(j => !j.IsDurable).Select(j => (j.JobGroup, j.JobName)).ToList();

            await _storage.RemoveJobsAsync(await _storage.GetJobsWithoutTriggersAsync(candidates, lct), lct);
        }

        foreach (var pending in spent)
        {
            pending.Processed = true;
        }
    }

    private async Task ScheduleRetryAsync(NCacheTriggerData storedTrigger, IOperableTrigger trigger, CancellationToken lct)
    {
        if (!trigger.NextFireTimeUtc.HasValue)
        {
            storedTrigger.RetryAttempt = 0;
            storedTrigger.TriggerState = storedTrigger.NextFireTimeUtc.HasValue ? TriggerStates.Waiting : TriggerStates.Complete;
            await PersistTriggerWithIndexAsync(storedTrigger, lct);
            return;
        }

        var pausedGroups = await _storage.GetPausedTriggerGroupsAsync(lct);
        var pausedJobGroups = await _storage.GetPausedJobGroupsAsync(lct);

        storedTrigger.NextFireTimeUtc = trigger.NextFireTimeUtc;
        storedTrigger.RetryAttempt = trigger.RetryAttempt;

        storedTrigger.TriggerState = IsTriggerGroupPaused(pausedGroups, storedTrigger.TriggerGroup) ||
                                     pausedJobGroups.Contains(storedTrigger.JobGroup, StringComparer.Ordinal)
            ? TriggerStates.Paused
            : TriggerStates.Waiting;

        await PersistTriggerWithIndexAsync(storedTrigger, lct);
    }

    private const int IgnoreMisfirePolicy = -1;

    // Whether the trigger is later than its own misfire threshold, or the store's when it has none.
    private bool IsMisfired(NCacheTriggerData trigger)
    {
        if (!trigger.NextFireTimeUtc.HasValue || trigger.MisfireInstruction == IgnoreMisfirePolicy)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - trigger.NextFireTimeUtc.Value >= (trigger.MisfireThreshold ?? _misfireThreshold);
    }

    private async Task<NCacheTriggerData> ApplyMisfireAsync(NCacheTriggerData storedTrigger, CancellationToken lct)
    {
        ICalendar? calendar = null;

        if (!string.IsNullOrWhiteSpace(storedTrigger.CalendarName))
        {
            calendar = await GetCalendarAsync(storedTrigger.CalendarName!, lct);
        }

        ApplyMisfire(storedTrigger, calendar);
        await PersistTriggerWithIndexAsync(storedTrigger, lct);

        return storedTrigger;
    }

    private void ApplyMisfire(NCacheTriggerData storedTrigger, ICalendar? calendar)
    {
        var quartzTrigger = TriggerMapper.ToQuartzTrigger(storedTrigger, _serialization);
        var asMisfired = _signaler is null ? null : (IOperableTrigger) quartzTrigger.Clone();
        quartzTrigger.UpdateAfterMisfire(calendar);

        TriggerMapper.ApplyScheduleState(storedTrigger, quartzTrigger, _serialization);
        storedTrigger.RetryAttempt = 0;
        storedTrigger.TriggerState = storedTrigger.NextFireTimeUtc.HasValue ? TriggerStates.Waiting : TriggerStates.Complete;

        if (asMisfired is not null)
        {
            QueueSignal((signaler, ct) => signaler.NotifyTriggerListenersMisfired(asMisfired, ct));

            if (!storedTrigger.NextFireTimeUtc.HasValue)
            {
                QueueSignal((signaler, ct) => signaler.NotifySchedulerListenersFinalized(quartzTrigger, ct));
            }
        }
    }

    private Task<bool> HasActiveFiringAsync(NCacheJobData job, CancellationToken lct)
        => HasActiveFiringAsync(job.JobGroup, job.JobName, lct);

    private async Task<bool> HasActiveFiringAsync(string jobGroup, string jobName, CancellationToken lct)
    {
        var firings = await _storage.GetFiredTriggersForJobAsync(jobGroup, jobName, lct);
        return firings.Any(f => f.State is FiredTriggerStates.Acquired or FiredTriggerStates.Executing);
    }

    private static void StampJob(NCacheTriggerData trigger, NCacheJobData job)
        => trigger.Job = NCacheTriggerJobFacts.From(job);

    private async Task RestampJobTriggersAsync(NCacheJobData job, IReadOnlySet<(string Group, string Name)> except, CancellationToken lct)
    {
        var changed = new List<NCacheTriggerData>();

        foreach (var trigger in await _storage.GetTriggersForJobAsync(job.JobGroup, job.JobName, lct))
        {
            if (except.Contains((trigger.TriggerGroup, trigger.TriggerName)))
            {
                continue;
            }

            StampJob(trigger, job);
            changed.Add(trigger);
        }

        await _storage.UpdateTriggersAsync(changed, lct);
    }

    private async Task UnblockJobTriggersAsync(NCacheJobData job, CancellationToken lct)
    {
        var changed = new List<NCacheTriggerData>();
        var finalized = new List<NCacheTriggerData>();
        var calendars = new Dictionary<string, ICalendar?>(StringComparer.Ordinal);

        foreach (var trigger in await _storage.GetTriggersForJobAsync(job.JobGroup, job.JobName, lct))
        {
            lct.ThrowIfCancellationRequested();

            if (trigger.TriggerState == TriggerStates.Blocked)
            {
                trigger.TriggerState = TriggerStates.Waiting;

                if (IsMisfired(trigger))
                {
                    ApplyMisfire(trigger, await GetCalendarCachedAsync(trigger.CalendarName, calendars, lct));

                    if (trigger.TriggerState == TriggerStates.Complete)
                    {
                        finalized.Add(trigger);
                    }
                }

                changed.Add(trigger);
            }
            else if (trigger.TriggerState == TriggerStates.PausedAndBlocked)
            {
                trigger.TriggerState = TriggerStates.Paused;
                changed.Add(trigger);
            }
        }

        await _index.UpsertManyAsync(changed, lct);
        await _storage.UpdateTriggersAsync(changed, lct);

        foreach (var trigger in finalized)
        {
            await DeleteTriggerInternalAsync(trigger, deleteOrphanJob: true, lct);
        }
    }

    private NCacheSchedulerState CreateStateRecord() => new()
    {
        SchedulerName = _schedulerName,
        InstanceName = _instanceId,
        LastCheckinTimeUtc = DateTimeOffset.UtcNow,
        CheckinInterval = _clusterCheckinInterval
    };

    private bool IsStale(NCacheSchedulerState state, DateTimeOffset now)
        => now - state.LastCheckinTimeUtc > state.CheckinInterval + _clusterCheckinMisfireThreshold;

    private async Task RunHeartbeatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_clusterCheckinInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await _storage.WriteSchedulerStateAsync(CreateStateRecord(), cancellationToken);
                await EnsureRegisteredAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduler {InstanceId} failed to check in; will retry at the next interval.", _instanceId);
                continue;
            }

            if (Interlocked.CompareExchange(ref _maintenanceRunning, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RecoverStaleSchedulerInstancesAsync(cancellationToken);
                        await RecoverStaleAcquiredFiringsAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Cluster maintenance pass on {InstanceId} failed; will retry at the next interval.", _instanceId);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _maintenanceRunning, 0);
                    }
                }, CancellationToken.None);
            }
        }
    }

    private async Task EnsureRegisteredAsync(CancellationToken cancellationToken)
    {
        var names = await _storage.GetRegisteredSchedulerInstanceNamesAsync(cancellationToken);

        if (names.Contains(_instanceId, StringComparer.Ordinal))
        {
            return;
        }

        _logger.LogWarning("Scheduler {InstanceId} was missing from the instance registry; re-registering.", _instanceId);

        await WithStateAccessAsync(lct => _storage.RegisterSchedulerInstanceAsync(_instanceId, lct), cancellationToken);
    }

    /// <summary>
    /// Takes over the work of nodes that stopped checking in: releases their acquired triggers and
    /// re-runs their RequestsRecovery jobs. Returns the number of recovery triggers created.
    /// </summary>
    public async ValueTask<int> RecoverStaleSchedulerInstancesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = await WithStateAccessAsync(async lct =>
        {
            var found = new List<string>();
            var now = DateTimeOffset.UtcNow;

            foreach (var instanceName in await _storage.GetRegisteredSchedulerInstanceNamesAsync(lct))
            {
                lct.ThrowIfCancellationRequested();

                if (string.Equals(instanceName, _instanceId, StringComparison.Ordinal))
                {
                    continue;
                }

                var state = await _storage.GetSchedulerStateAsync(instanceName, lct);

                if (state is null || IsStale(state, now))
                {
                    found.Add(instanceName);
                }
            }

            return found;
        }, cancellationToken);

        var recovered = 0;

        foreach (var instanceName in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            recovered += await WithBothLocksAsync(async lct =>
            {
                var state = await _storage.GetSchedulerStateAsync(instanceName, lct);
                var now = DateTimeOffset.UtcNow;

                if (state is not null && !IsStale(state, now))
                {
                    return 0;
                }

                var inGracePeriod = state is not null &&
                                    now - state.LastCheckinTimeUtc < state.CheckinInterval + state.CheckinInterval + _clusterCheckinMisfireThreshold;

                _logger.LogWarning(inGracePeriod
                        ? "Scheduler instance {StaleInstance} missed its check-ins; {InstanceId} is recovering its reserved work and leaving its non-concurrent executions for now."
                        : "Scheduler instance {StaleInstance} is considered failed; {InstanceId} is recovering its work.",
                    instanceName, _instanceId);

                var created = await RecoverInstanceFiringsAsync(instanceName, lct, keepNonConcurrentExecutions: inGracePeriod);

                if (!inGracePeriod)
                {
                    await ReleasePreferredNodeClaimsAsync(instanceName, lct);
                    await _storage.RemoveSchedulerStateAsync(instanceName, lct);
                }

                return created;
            }, cancellationToken);
        }

        return recovered;
    }

    public async ValueTask<int> RecoverStaleAcquiredFiringsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await WithTriggerAccessAsync(async lct =>
        {
            var threshold = StaleAcquiredTriggerThreshold;
            var now = DateTimeOffset.UtcNow;
            var recovered = 0;

            foreach (var firing in await _storage.GetAllFiredTriggersAsync(lct))
            {
                lct.ThrowIfCancellationRequested();

                if (firing.State != FiredTriggerStates.Acquired || now - firing.FiredTimeUtc <= threshold)
                {
                    continue;
                }

                _logger.LogWarning("Fired trigger {EntryId} for {TriggerGroup}.{TriggerName} has been Acquired on {Owner} since {FiredAt}; releasing it.",
                    firing.EntryId, firing.TriggerGroup, firing.TriggerName, firing.InstanceName, firing.FiredTimeUtc);

                await RecoverFiringAsync(firing, lct);
                recovered++;
            }

            return recovered;
        }, cancellationToken);
    }

    private async Task<int> RecoverInstanceFiringsAsync(string instanceName, CancellationToken lct, bool keepNonConcurrentExecutions = false)
    {
        var created = 0;

        foreach (var firing in await _storage.GetFiredTriggersForInstanceAsync(instanceName, lct))
        {
            lct.ThrowIfCancellationRequested();

            if (keepNonConcurrentExecutions && firing.State == FiredTriggerStates.Executing && firing.IsNonConcurrent)
            {
                continue;
            }

            if (await RecoverFiringAsync(firing, lct))
            {
                created++;
            }
        }

        return created;
    }

    private async Task<bool> RecoverFiringAsync(NCacheFiredTriggerData firing, CancellationToken lct)
    {
        var recoveryTriggerCreated = false;
        var pausedGroups = await _storage.GetPausedTriggerGroupsAsync(lct);

        NCacheJobData? job = null;

        if (!string.IsNullOrWhiteSpace(firing.JobGroup) && !string.IsNullOrWhiteSpace(firing.JobName))
        {
            job = await _storage.GetJobAsync(firing.JobGroup, firing.JobName, lct);
        }

        if (firing.State == FiredTriggerStates.Executing && firing.RequestsRecovery && job is not null)
        {
            var original = await _storage.GetTriggerAsync(firing.TriggerGroup, firing.TriggerName, lct);
            var recoveryData = TriggerMapper.ToModel(CreateRecoveryTrigger(firing, job, original?.JobDataMap), _schedulerName, TriggerStates.Waiting, _serialization);
            StampJob(recoveryData, job);
            var existing = await _storage.GetTriggerAsync(recoveryData.TriggerGroup, recoveryData.TriggerName, lct);

            if (existing is null)
            {
                await _index.UpsertAsync(recoveryData, lct);
                await _storage.StoreTriggerAsync(recoveryData, replaceExisting: false, lct);
                recoveryTriggerCreated = true;
            }
            else if (existing.TriggerState is TriggerStates.Complete or TriggerStates.Error)
            {
                existing.TriggerState = TriggerStates.Waiting;
                existing.NextFireTimeUtc = DateTimeOffset.UtcNow;
                await PersistTriggerWithIndexAsync(existing, lct);
                recoveryTriggerCreated = true;
            }

            if (recoveryTriggerCreated)
            {
                _logger.LogInformation("Created recovery trigger for job {JobGroup}.{JobName} (original trigger {TriggerGroup}.{TriggerName}, instance {Owner}).",
                    job.JobGroup, job.JobName, firing.TriggerGroup, firing.TriggerName, firing.InstanceName);
            }
            else
            {
                _logger.LogInformation("Recovery trigger {TriggerGroup}.{TriggerName} for job {JobGroup}.{JobName} already exists and is pending; not creating another.",
                    recoveryData.TriggerGroup, recoveryData.TriggerName, job.JobGroup, job.JobName);
            }
        }

        await _storage.RemoveFiredTriggerAsync(firing.EntryId, lct);

        if (firing.State == FiredTriggerStates.Acquired)
        {
            var trigger = await _storage.GetTriggerAsync(firing.TriggerGroup, firing.TriggerName, lct);

            if (trigger is not null && trigger.TriggerState == TriggerStates.Acquired)
            {
                trigger.TriggerState = IsTriggerGroupPaused(pausedGroups, trigger.TriggerGroup) ? TriggerStates.Paused : TriggerStates.Waiting;

                if (trigger.TriggerState == TriggerStates.Waiting && IsMisfired(trigger))
                {
                    await ApplyMisfireAsync(trigger, lct);
                }
                else
                {
                    await PersistTriggerWithIndexAsync(trigger, lct);
                }
            }
        }
        else
        {
            var trigger = await _storage.GetTriggerAsync(firing.TriggerGroup, firing.TriggerName, lct);

            if (trigger is { TriggerState: TriggerStates.Complete } &&
                !(await _storage.GetAllFiredTriggersAsync(lct)).Any(f => f.TriggerGroup == trigger.TriggerGroup && f.TriggerName == trigger.TriggerName))
            {
                await DeleteTriggerInternalAsync(trigger, deleteOrphanJob: true, lct);
            }
            else if (!firing.IsNonConcurrent &&
                     firing.OverlapPolicy is (int) OverlapPolicy.BufferOne or (int) OverlapPolicy.CancelPrevious)
            {
                await ReleaseOverlapHoldAsync(new TriggerKey(firing.TriggerName, firing.TriggerGroup), lct);
            }
        }

        if (firing.IsNonConcurrent && job is not null && !await HasActiveFiringAsync(job, lct))
        {
            await UnblockJobTriggersAsync(job, lct);
        }

        return recoveryTriggerCreated;
    }

    private async Task ReleasePreferredNodeClaimsAsync(string staleInstanceId, CancellationToken lct)
    {
        foreach (var trigger in await _storage.GetAllTriggersAsync(lct))
        {
            lct.ThrowIfCancellationRequested();

            if (!trigger.PreferredNodeIsAutomatic || !string.Equals(trigger.PreferredNode, staleInstanceId, StringComparison.Ordinal))
            {
                continue;
            }

            trigger.PreferredNode = null;
            await _storage.UpdateTriggerAsync(trigger, lct);
        }
    }

    private async Task<bool> IsSchedulerNodeAliveAsync(string instanceId, CancellationToken lct)
    {
        var state = await _storage.GetSchedulerStateAsync(instanceId, lct);
        return state is not null && !IsStale(state, DateTimeOffset.UtcNow);
    }

    private IOperableTrigger CreateRecoveryTrigger(NCacheFiredTriggerData firing, NCacheJobData job, Dictionary<string, object?>? originalTriggerData)
    {
        var recoveryData = new JobDataMap();

        foreach (var pair in originalTriggerData ?? new Dictionary<string, object?>())
        {
            recoveryData[pair.Key] = pair.Value;
        }

        recoveryData[SchedulerConstants.FailedJobOriginalTriggerName] = firing.TriggerName;
        recoveryData[SchedulerConstants.FailedJobOriginalTriggerGroup] = firing.TriggerGroup;
        recoveryData[SchedulerConstants.FailedJobOriginalTriggerFireTime] = firing.FiredTimeUtc;
        recoveryData[SchedulerConstants.FailedJobOriginalTriggerScheduledFireTime] = firing.ScheduledTimeUtc;

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"recover_{firing.EntryId}", SchedulerConstants.DefaultRecoveryGroup)
            .ForJob(new JobKey(job.JobName, job.JobGroup))
            .WithPriority(firing.Priority)
            .StartAt(DateTimeOffset.UtcNow)
            .UsingJobData(recoveryData)
            .WithSimpleSchedule(schedule => schedule.WithRepeatCount(0).WithMisfireInstruction(SimpleTriggerMisfireInstruction.IgnoreMisfires))
            .Build();

        var operable = (IOperableTrigger)trigger;

        operable.ComputeFirstFireTimeUtc(null);

        return operable;
    }

    private async Task RebuildNextFireTimeIndexAsync(CancellationToken lct)
    {
        var triggers = await _storage.GetAllTriggersAsync(lct);
        await _index.RebuildAsync(triggers, lct);

        await _storage.UpdateTriggersAsync(triggers, lct);
    }
    private async Task RepairRegistriesAndIndexAsync(CancellationToken ct, bool includeSchedulerRegistry)
    {
        try
        {
            await _storage.RebuildRegistriesAsync(includeSchedulerRegistry, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registry repair failed.");
        }

        try
        {
            await RebuildNextFireTimeIndexAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Next-fire-time index repair failed.");
        }
    }

    private Task WithTriggerAccessAsync(Func<CancellationToken, Task> body, CancellationToken ct, [CallerMemberName] string operation = "")
        => WithTriggerAccessAsync(async lct => { await body(lct); return true; }, ct, operation);

    private Task<T> WithTriggerAccessAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken ct, [CallerMemberName] string operation = "", bool urgent = false)
        => WithSignalScopeAsync(() => WithTriggerAccessCoreAsync(body, ct, operation, urgent));

    private async Task<T> WithTriggerAccessCoreAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken ct, string operation, bool urgent)
    {
        EnsureInitialized();
        ct.ThrowIfCancellationRequested();

        var timing = _logger.IsEnabled(LogLevel.Debug) ? System.Diagnostics.Stopwatch.StartNew() : null;

        await using var handle = await _locks.AcquireTriggerAccessAsync(_lockAcquireTimeout, ct, urgent);

        var waited = timing?.Elapsed;

        try
        {
            return await RunUnderLockAsync(handle, "TRIGGER_ACCESS", body, ct, operation, repairOnFailure: true, stateAccessHeld: false);
        }
        finally
        {
            if (timing is not null)
            {
                _logger.LogDebug("{Operation} on {InstanceId}: waited {WaitMs}ms for TRIGGER_ACCESS, held it {HeldMs}ms",
                    operation, _instanceId, (long)waited!.Value.TotalMilliseconds, (long)(timing.Elapsed - waited.Value).TotalMilliseconds);
            }
        }
    }

    private Task WithStateAccessAsync(Func<CancellationToken, Task> body, CancellationToken ct, [CallerMemberName] string operation = "")
        => WithStateAccessAsync(async lct => { await body(lct); return true; }, ct, operation);

    private Task<T> WithStateAccessAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken ct, [CallerMemberName] string operation = "")
        => WithSignalScopeAsync(() => WithStateAccessCoreAsync(body, ct, operation));

    private async Task<T> WithStateAccessCoreAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken ct, string operation)
    {
        EnsureInitialized();
        ct.ThrowIfCancellationRequested();

        await using var handle = await _locks.AcquireStateAccessAsync(_lockAcquireTimeout, ct);
        return await RunUnderLockAsync(handle, "STATE_ACCESS", body, ct, operation, repairOnFailure: false, stateAccessHeld: true);
    }

    private Task WithBothLocksAsync(Func<CancellationToken, Task> body, CancellationToken ct, [CallerMemberName] string operation = "")
        => WithBothLocksAsync(async lct => { await body(lct); return true; }, ct, operation);

    private Task<T> WithBothLocksAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken ct, [CallerMemberName] string operation = "")
        => WithSignalScopeAsync(() => WithBothLocksCoreAsync(body, ct, operation));

    private async Task<T> WithBothLocksCoreAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken ct, string operation)
    {
        EnsureInitialized();
        ct.ThrowIfCancellationRequested();

        await using var stateHandle = await _locks.AcquireStateAccessAsync(_lockAcquireTimeout, ct);

        return await RunUnderLockAsync(stateHandle, "STATE_ACCESS", async sct =>
        {
            await using var triggerHandle = await _locks.AcquireTriggerAccessAsync(_lockAcquireTimeout, sct);
            return await RunUnderLockAsync(triggerHandle, "TRIGGER_ACCESS", body, sct, operation, repairOnFailure: true, stateAccessHeld: true);
        }, ct, operation, repairOnFailure: false, stateAccessHeld: true);
    }

    private async Task<T> WithSignalScopeAsync<T>(Func<Task<T>> section)
    {
        if (_signaler is null || _pendingSignals.Value is not null)
        {
            return await section();
        }

        var signals = new List<Func<ISchedulerSignaler, CancellationToken, ValueTask>>();
        _pendingSignals.Value = signals;
        T result;

        try
        {
            result = await section();
        }
        finally
        {
            _pendingSignals.Value = null;
        }

        foreach (var signal in signals)
        {
            try
            {
                await signal(_signaler, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A Quartz listener failed while being notified by the job store.");
            }
        }

        return result;
    }

    private void QueueSignal(Func<ISchedulerSignaler, CancellationToken, ValueTask> signal)
    {
        if (_signaler is not null)
        {
            _pendingSignals.Value?.Add(signal);
        }
    }

    private async Task<T> RunUnderLockAsync<T>(
        IDistributedSynchronizationHandle handle,
        string lockName,
        Func<CancellationToken, Task<T>> body,
        CancellationToken ct,
        string operation,
        bool repairOnFailure,
        bool stateAccessHeld)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, handle.HandleLostToken);

        try
        {
            return await body(linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && handle.HandleLostToken.IsCancellationRequested)
        {
            throw new JobPersistenceException(
                $"{operation}: the {lockName} lock expired while the operation was running and the operation was aborted " +
                $"to avoid corrupting shared scheduling state. The lock expiration ({_locks.LockExpiration}) is too short for this workload.");
        }
        catch (Exception ex) when (repairOnFailure && !_repairSuppressed.Value && IsUnexpectedFailure(ex) && !handle.HandleLostToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "{Operation} failed unexpectedly; rebuilding registries and index while still holding {LockName}.", operation, lockName);
            await RepairRegistriesAndIndexAsync(ct, stateAccessHeld);
            throw;
        }
    }

    private void EnsureInitialized()
    {
        if (string.IsNullOrWhiteSpace(_schedulerName) || string.IsNullOrWhiteSpace(_instanceId))
        {
            throw new InvalidOperationException(
                "NCacheJobStore.Initialize(SchedulerIdentity) must be called before the store is used; " +
                "rows written without a scheduler name would be invisible to registry repair.");
        }
    }

    private static bool IsUnexpectedFailure(Exception ex)
        => ex is not (JobPersistenceException or OperationCanceledException or ArgumentException or NotSupportedException);

    private async Task BestEffortAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Best-effort compensation step failed.");
        }
    }

    private static bool IsExcludedJobType(TriggerAcquisitionRequest request, string jobClassName)
        => request.ExcludedJobTypeNames is { Count: > 0 } && request.ExcludedJobTypeNames.Contains(jobClassName, StringComparer.Ordinal);

    // The execution groups an acquisition request lets this node take, decided the way Quartz's own stores decide.
    private sealed class ExecutionGroupFilter
    {
        private readonly HashSet<string> _groups = new(StringComparer.Ordinal);
        private readonly bool _includesUngrouped;
        private readonly bool _isServeList;

        // Reads the listed groups once, noting whether the ungrouped bucket is among them.
        private ExecutionGroupFilter(IReadOnlyCollection<ExecutionGroupScope> scopes, bool isServeList)
        {
            _isServeList = isServeList;

            foreach (var scope in scopes)
            {
                if (scope.IsDefault)
                {
                    _includesUngrouped = true;
                }
                else if (scope.Name is { } name)
                {
                    _groups.Add(name);
                }
            }
        }

        // Builds the filter from whichever list the request carries, or null when it routes nothing.
        public static ExecutionGroupFilter? From(TriggerAcquisitionRequest request)
        {
            var scopes = request.ServedExecutionGroups ?? request.RefusedExecutionGroups;
            return scopes is null ? null : new ExecutionGroupFilter(scopes, request.ServedExecutionGroups is not null);
        }

        // Whether a trigger in this execution group (null for none) may be acquired by this node.
        public bool Serves(string? executionGroup)
        {
            var listed = executionGroup is null ? _includesUngrouped : _groups.Contains(executionGroup);
            return listed == _isServeList;
        }
    }

    private static bool IsTriggerGroupPaused(IReadOnlyCollection<string> pausedTriggerGroups, string triggerGroup)
        => pausedTriggerGroups.Contains(AllGroupsPausedMarker, StringComparer.Ordinal) ||
           pausedTriggerGroups.Contains(triggerGroup, StringComparer.Ordinal);

    // Reads the paused groups, then works out the state a trigger being stored should start in.
    private async Task<string> ComputeStateForStoredTriggerAsync(NCacheTriggerData? existing, string triggerGroup, NCacheJobData job, CancellationToken lct, bool storePaused = false)
        => await ComputeStateForStoredTriggerAsync(existing, triggerGroup, job, await _storage.GetPausedTriggerGroupsAsync(lct), await _storage.GetPausedJobGroupsAsync(lct), lct, storePaused);

    // Picks the starting state of a stored trigger: paused if asked, already paused or in a paused group; blocked if its job is running.
    private async Task<string> ComputeStateForStoredTriggerAsync(NCacheTriggerData? existing, string triggerGroup, NCacheJobData job, IReadOnlyCollection<string> pausedTriggerGroups, IReadOnlyCollection<string> pausedJobGroups, CancellationToken lct, bool storePaused = false)
    {
        var paused = storePaused || existing?.TriggerState is TriggerStates.Paused or TriggerStates.PausedAndBlocked;

        if (!paused)
        {
            paused = IsTriggerGroupPaused(pausedTriggerGroups, triggerGroup);
        }

        if (!paused)
        {
            paused = pausedJobGroups.Contains(job.JobGroup, StringComparer.Ordinal);
        }

        var blocked = job.IsNonConcurrent && await HasActiveFiringAsync(job, lct);

        return (paused, blocked) switch
        {
            (true, true) => TriggerStates.PausedAndBlocked,
            (true, false) => TriggerStates.Paused,
            (false, true) => TriggerStates.Blocked,
            _ => TriggerStates.Waiting
        };
    }

    private async Task PersistTriggerWithIndexAsync(NCacheTriggerData trigger, CancellationToken lct)
    {
        await _index.UpsertAsync(trigger, lct);
        await _storage.UpdateTriggerAsync(trigger, lct);
    }

    private async Task DeleteTriggerInternalAsync(NCacheTriggerData trigger, bool deleteOrphanJob, CancellationToken lct, bool settleContinuations = true)
    {
        if (settleContinuations)
        {
            await SettleContinuationsOfDeletedParentAsync(new TriggerKey(trigger.TriggerName, trigger.TriggerGroup), known: null, lct);
        }

        await _storage.RemoveTriggerAsync(trigger.TriggerGroup, trigger.TriggerName, lct);
        await _index.RemoveAsync(trigger , lct);

        if (!deleteOrphanJob)
        {
            return;
        }

        var job = await _storage.GetJobAsync(trigger.JobGroup, trigger.JobName, lct);

        if (job is null || job.IsDurable)
        {
            return;
        }

        var remainingTriggers = await _storage.GetTriggersForJobAsync(job.JobGroup, job.JobName, lct);

        if (remainingTriggers.Count == 0)
        {
            await _storage.RemoveJobAsync(job.JobGroup, job.JobName, lct);
        }
    }

    private async Task SetAllJobTriggersStateAsync(NCacheJobData? job, string state, CancellationToken lct)
    {
        if (job is null)
        {
            return;
        }

        var changed = new List<NCacheTriggerData>();

        foreach (var trigger in await _storage.GetTriggersForJobAsync(job.JobGroup, job.JobName, lct))
        {
            lct.ThrowIfCancellationRequested();

            if (trigger.TriggerState == TriggerStates.Awaiting)
            {
                continue;
            }

            trigger.TriggerState = state;
            changed.Add(trigger);
        }

        await _index.UpsertManyAsync(changed, lct);
        await _storage.UpdateTriggersAsync(changed, lct);
    }

    private sealed class AwaitingByParent
    {
        private readonly IReadOnlyDictionary<(string TriggerGroup, string TriggerName), IReadOnlyList<NCacheTriggerData>> _found;

        public AwaitingByParent(IReadOnlyDictionary<(string TriggerGroup, string TriggerName), IReadOnlyList<NCacheTriggerData>> found) => _found = found;

        public bool MayHaveAwaiting(TriggerKey parent) => _found.ContainsKey((parent.Group, parent.Name));
    }

    private async Task EnsureContinuationParentExistsAsync(IOperableTrigger trigger, IReadOnlySet<TriggerKey>? alsoStored, TriggerKey? aboutToBeDeleted, CancellationToken lct)
    {
        if (trigger.Continuation.Parent is not { } parent)
        {
            return;
        }

        if (alsoStored?.Contains(parent) == true)
        {
            return;
        }

        if (parent.Equals(aboutToBeDeleted) || !await _storage.TriggerExistsAsync(parent.Group, parent.Name, lct))
        {
            throw new ObjectDoesNotExistException(trigger, parent);
        }
    }

    private async Task<IReadOnlyList<NCacheTriggerData>> ReadAwaitingAsync(TriggerKey parent, AwaitingByParent? known, CancellationToken lct)
    {
        if (known is not null && !known.MayHaveAwaiting(parent))
        {
            return Array.Empty<NCacheTriggerData>();
        }

        var found = await _storage.GetAwaitingContinuationsAsync(new[] { (parent.Group, parent.Name) }, lct);
        return found.TryGetValue((parent.Group, parent.Name), out var awaiting) ? awaiting : Array.Empty<NCacheTriggerData>();
    }

    private async Task<bool> SettleContinuationsAsync(TriggerKey parent, ExecutionOutcome outcome, AwaitingByParent? known, CancellationToken lct)
    {
        if (ContinuationRules.ConditionFor(outcome) is not { } satisfied)
        {
            return false;
        }

        foreach (var awaiting in await ReadAwaitingAsync(parent, known, lct))
        {
            lct.ThrowIfCancellationRequested();

            if (ContinuationRules.Releases(awaiting, satisfied))
            {
                await ReleaseContinuationAsync(awaiting, lct);
            }
            else
            {
                await DiscardContinuationAsync(awaiting, lct);
            }
        }

        return true;
    }

    private async Task SettleContinuationsOfDeletedParentAsync(TriggerKey parent, AwaitingByParent? known, CancellationToken lct)
    {
        foreach (var awaiting in await ReadAwaitingAsync(parent, known, lct))
        {
            lct.ThrowIfCancellationRequested();

            if (ContinuationRules.ConditionOf(awaiting) == ContinuationCondition.OnAnyOutcome)
            {
                await ReleaseContinuationAsync(awaiting, lct);
            }
            else
            {
                await ParkContinuationAsync(awaiting, lct);
            }
        }
    }

    private async Task ReleaseContinuationAsync(NCacheTriggerData awaiting, CancellationToken lct)
    {
        var calendar = string.IsNullOrWhiteSpace(awaiting.CalendarName) ? null : await GetCalendarAsync(awaiting.CalendarName, lct);

        if (ContinuationRules.ReleaseFireTime(awaiting, calendar, DateTimeOffset.UtcNow) is not { } fireTime)
        {
            await DiscardContinuationAsync(awaiting, lct);
            return;
        }

        var job = await _storage.GetJobAsync(awaiting.JobGroup, awaiting.JobName, lct);

        await _storage.UnregisterContinuationsAsync(new[] { awaiting }, lct);
        ContinuationRules.Forget(awaiting);

        awaiting.NextFireTimeUtc = fireTime;
        awaiting.TriggerState = job is null
            ? TriggerStates.Waiting
            : await ComputeStateForStoredTriggerAsync(null, awaiting.TriggerGroup, job, lct);

        await PersistTriggerWithIndexAsync(awaiting, lct);

        _logger.LogDebug("Released continuation {TriggerGroup}.{TriggerName}; it fires at {FireTime} in state {State}.",
            awaiting.TriggerGroup, awaiting.TriggerName, fireTime, awaiting.TriggerState);

        QueueSignal((signaler, ct) => signaler.SignalSchedulingChange(fireTime, ct));
    }

    private async Task DiscardContinuationAsync(NCacheTriggerData awaiting, CancellationToken lct)
    {
        IOperableTrigger? finalized = null;

        try
        {
            finalized = TriggerMapper.ToQuartzTrigger(awaiting, _serialization);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discarded continuation {TriggerGroup}.{TriggerName} could not be rebuilt for its listeners.", awaiting.TriggerGroup, awaiting.TriggerName);
        }

        await DeleteTriggerInternalAsync(awaiting, deleteOrphanJob: true, lct, settleContinuations: false);

        _logger.LogDebug("Discarded continuation {TriggerGroup}.{TriggerName}.", awaiting.TriggerGroup, awaiting.TriggerName);

        if (finalized is not null)
        {
            QueueSignal((signaler, ct) => signaler.NotifySchedulerListenersFinalized(finalized, ct));
        }

        foreach (var dependant in await ReadAwaitingAsync(new TriggerKey(awaiting.TriggerName, awaiting.TriggerGroup), known: null, lct))
        {
            lct.ThrowIfCancellationRequested();
            await DiscardContinuationAsync(dependant, lct);
        }
    }

    private async Task ParkContinuationAsync(NCacheTriggerData awaiting, CancellationToken lct)
    {
        await _storage.UnregisterContinuationsAsync(new[] { awaiting }, lct);

        awaiting.TriggerState = TriggerStates.Error;
        await PersistTriggerWithIndexAsync(awaiting, lct);

        var key = new TriggerKey(awaiting.TriggerName, awaiting.TriggerGroup);

        _logger.LogWarning("Continuation {TriggerKey} was waiting for {ParentGroup}.{ParentName}, which has been deleted; it is parked in ERROR until it is reset.",
            key, awaiting.ContinuesAfterTriggerGroup, awaiting.ContinuesAfterTriggerName);

        QueueSignal((signaler, ct) => signaler.NotifySchedulerListenersTriggerInError(key, ct));
    }

    private async Task<ICalendar?> GetCalendarAsync(string calendarName, CancellationToken ct)
    {
        var stored = await _storage.GetCalendarAsync(calendarName, ct);
        return stored is null ? null : QuartzCalendarSerializer.Deserialize(stored.CalendarJson, _calendarSerializer);
    }

    private static NCacheTriggerData CloneTrigger(NCacheTriggerData trigger)
        => NCacheSerializer.Deserialize<NCacheTriggerData>(NCacheSerializer.Serialize(trigger));

    private static void PreservePreviousFireTime(NCacheTriggerData replacement, NCacheTriggerData existing)
    {
        if (!replacement.PreviousFireTimeUtc.HasValue && existing.PreviousFireTimeUtc.HasValue)
        {
            replacement.PreviousFireTimeUtc = existing.PreviousFireTimeUtc;
        }
    }

    private static void PreservePause(NCacheTriggerData replacement, NCacheTriggerData existing)
    {
        if (replacement.TriggerState is TriggerStates.Paused or TriggerStates.PausedAndBlocked)
        {
            replacement.Pause = existing.Pause;
        }
    }

    // Gives a trigger stored paused the store call's own pause record, replacing any record kept from the trigger it overwrote.
    private static void StampStoredPause(NCacheTriggerData trigger, NCachePauseData? storedPause)
    {
        if (trigger.TriggerState is TriggerStates.Paused or TriggerStates.PausedAndBlocked)
        {
            trigger.Pause = storedPause;
        }
    }

    private async Task StampOverlapSettlementAsync(NCacheTriggerData replacement, NCacheTriggerData existing, CancellationToken lct)
    {
        replacement.OverlapPolicyUnsettled = false;

        if (replacement.OverlapPolicy != (int) OverlapPolicy.CancelPrevious)
        {
            return;
        }

        if (existing.OverlapPolicy == (int) OverlapPolicy.CancelPrevious && !existing.OverlapPolicyUnsettled)
        {
            return;
        }

        replacement.OverlapPolicyUnsettled = (await RunningFiringsOfAsync(existing.TriggerGroup, existing.TriggerName, lct)).Count > 0;
    }

    private async Task<List<NCacheFiredTriggerData>> RunningFiringsOfAsync(string triggerGroup, string triggerName, CancellationToken lct)
        => (await _storage.GetAllFiredTriggersAsync(lct))
            .Where(f => f.State == FiredTriggerStates.Executing && f.TriggerGroup == triggerGroup && f.TriggerName == triggerName)
            .ToList();

    private static void PreserveAutomaticNodeClaim(NCacheTriggerData replacement, NCacheTriggerData existing)
    {
        if (existing.PreferredNodeIsAutomatic && replacement.PreferredNodeIsAutomatic && replacement.PreferredNode is null)
        {
            replacement.PreferredNode = existing.PreferredNode;
        }

        replacement.IndexBucketId = existing.IndexBucketId;
    }

    private static bool MatchesGroup<TKey>(GroupMatcher<TKey> matcher, string group) where TKey : Key<TKey>
        => matcher.CompareWithOperator.Evaluate(group, matcher.CompareToValue);

    private static IEnumerable<string> MatchingGroups<TKey>(GroupMatcher<TKey> matcher, IEnumerable<string> groups, bool includeEqualityTarget = true) where TKey : Key<TKey>
    {
        var result = new List<string>();

        foreach (var group in groups)
        {
            if (group != AllGroupsPausedMarker && MatchesGroup(matcher, group))
            {
                result.Add(group);
            }
        }

        if (includeEqualityTarget &&
            matcher.CompareWithOperator.Equals(StringOperator.Equality) &&
            !result.Contains(matcher.CompareToValue, StringComparer.Ordinal))
        {
            result.Add(matcher.CompareToValue);
        }

        return result;
    }

    private static string ToTriggerTypeDiscriminator(NCacheTriggerKind kind) => kind switch
    {
        NCacheTriggerKind.Simple => AdoConstants.TriggerTypeSimple,
        NCacheTriggerKind.Cron => AdoConstants.TriggerTypeCron,
        NCacheTriggerKind.CalendarInterval => AdoConstants.TriggerTypeCalendarInterval,
        NCacheTriggerKind.DailyTimeInterval => AdoConstants.TriggerTypeDailyTimeInterval,
        NCacheTriggerKind.Recurrence => AdoConstants.TriggerTypeRecurrence,
        _ => AdoConstants.TriggerTypeBlob
    };

    private static int CompareKeys(string leftGroup, string leftName, string rightGroup, string rightName)
    {
        var byGroup = StringComparer.Ordinal.Compare(leftGroup, rightGroup);
        return byGroup != 0 ? byGroup : StringComparer.Ordinal.Compare(leftName, rightName);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static PagedResult<T> CreatePagedResult<T>(List<T> orderedItems, PagedQuery query)
    {
        var total = orderedItems.Count;

        if (query.Take == 0)
        {
            return new PagedResult<T>(Array.Empty<T>(), total > query.Skip, query.IncludeTotalCount ? total : null);
        }

        if (query.Skip >= total)
        {
            return new PagedResult<T>(Array.Empty<T>(), false, query.IncludeTotalCount ? total : null);
        }

        var start = query.Skip;
        var count = Math.Min(query.Take, total - start);
        var items = orderedItems.GetRange(start, count);
        var hasMore = start + count < total;

        return new PagedResult<T>(items, hasMore, query.IncludeTotalCount ? total : null);
    }

    public TimeSpan GetAcquireRetryDelay(int failureCount)
    {
        if (failureCount <= 0)
        {
            return TimeSpan.FromMilliseconds(50);
        }

        var exponent = Math.Min(failureCount - 1, 20);
        var milliseconds = Math.Min(50 * Math.Pow(2, exponent), 60_000);

        return TimeSpan.FromMilliseconds(milliseconds);
    }
}

