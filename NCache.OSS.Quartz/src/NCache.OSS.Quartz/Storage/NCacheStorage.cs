using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Exceptions;
using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Model;
using NCache.OSS.Quartz.Serialization;
using Quartz;
using System.Collections;

namespace NCache.OSS.Quartz.Storage;

public sealed class NCacheStorage
{
    private readonly ICache _cache;
    private readonly string _prefix;
    private readonly string _schedName;

    public NCacheStorage(ICache cache, string prefix, string schedName)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
        _schedName = schedName ?? throw new ArgumentNullException(nameof(schedName));
    }

    public string SchedulerName => _schedName;

    private static CacheItem CreateItem(byte[] bytes) => new(bytes)
    {
        Priority = Alachisoft.NCache.Runtime.CacheItemPriority.NotRemovable
    };

    private void Insert(string key, byte[] bytes) => _cache.Insert(key, CreateItem(bytes));

    private void Add(string key, byte[] bytes) => _cache.Add(key, CreateItem(bytes));

    private Task<T?> GetAsync<T>(string key, CancellationToken ct) where T : class
        => Task.Run(() =>
        {
            var bytes = _cache.Get<byte[]>(key);
            return bytes is null ? null : NCacheSerializer.Deserialize<T>(bytes);
        }, ct);

    private Task InsertAsync<T>(string key, T value, CancellationToken ct)
        => Task.Run(() => Insert(key, NCacheSerializer.Serialize(value)), ct);

    private Task<bool> RemoveAsync(string key, CancellationToken ct)
        => Task.Run(() => _cache.Remove<byte[]>(key, out _), ct);

    private Task<T?> RemoveAndGetAsync<T>(string key, CancellationToken ct) where T : class
        => Task.Run(() =>
        {
            return _cache.Remove<byte[]>(key, out var bytes) && bytes is not null
                ? NCacheSerializer.Deserialize<T>(bytes)
                : null;
        }, ct);

    private Task<bool> ContainsAsync(string key, CancellationToken ct)
        => Task.Run(() => _cache.Contains(key), ct);
     
    private const int BulkChunkSize = 500;

    private static IEnumerable<List<string>> Chunk(IEnumerable<string> keys)
    {
        var chunk = new List<string>(BulkChunkSize);

        foreach (var key in keys)
        {
            chunk.Add(key);

            if (chunk.Count == BulkChunkSize)
            {
                yield return chunk;
                chunk = new List<string>(BulkChunkSize);
            }
        }

        if (chunk.Count > 0)
        {
            yield return chunk;
        }
    }

    private Task<IDictionary<string, byte[]>> GetManyBytesAsync(IEnumerable<string> keys, CancellationToken ct)
        => Task.Run(() =>
        {
            IDictionary<string, byte[]> result = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            foreach (var chunk in Chunk(keys))
            {
                ct.ThrowIfCancellationRequested();

                foreach (var (key, bytes) in _cache.GetBulk<byte[]>(chunk))
                {
                    if (bytes is not null)
                    {
                        result[key] = bytes;
                    }
                }
            }

            return result;
        }, ct);

    private Task<IDictionary<string, byte[]>> RemoveManyAndGetAsync(IEnumerable<string> keys, CancellationToken ct)
        => Task.Run(() =>
        {
            IDictionary<string, byte[]> removed = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            foreach (var chunk in Chunk(keys))
            {
                ct.ThrowIfCancellationRequested();
                _cache.RemoveBulk<byte[]>(chunk, out var payloads);

                foreach (var (key, bytes) in payloads)
                {
                    if (bytes is not null)
                    {
                        removed[key] = bytes;
                    }
                }
            }

            return removed;
        }, ct);

    private Task InsertManyAsync(IReadOnlyDictionary<string, byte[]> items, CancellationToken ct)
        => items.Count == 0
            ? Task.CompletedTask
            : Task.Run(() =>
            {
                foreach (var chunk in Chunk(items.Keys))
                {
                    ct.ThrowIfCancellationRequested();

                    var failed = _cache.InsertBulk(chunk.ToDictionary(k => k, k => CreateItem(items[k]), StringComparer.Ordinal));

                    if (failed.Count > 0)
                    {
                        var (key, error) = failed.First();
                        throw new JobPersistenceException($"Bulk insert failed for {failed.Count} key(s), e.g. '{key}': {error}", error as Exception);
                    }
                }
            }, ct);

    private Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken ct)
        => Task.Run(() =>
        {
            foreach (var chunk in Chunk(keys))
            {
                ct.ThrowIfCancellationRequested();
                _cache.RemoveBulk(chunk);
            }
        }, ct);

    private Task<IDictionary<string, bool>> ContainsManyAsync(IEnumerable<string> keys, CancellationToken ct)
        => Task.Run(() =>
        {
            IDictionary<string, bool> result = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (var chunk in Chunk(keys))
            {
                ct.ThrowIfCancellationRequested();

                foreach (var (key, exists) in _cache.ContainsBulk(chunk))
                {
                    result[key] = exists;
                }
            }

            return result;
        }, ct);

    private async Task RemoveFromListsAsync(IReadOnlyDictionary<string, HashSet<string>> valuesToRemoveByKey, CancellationToken ct)
    {
        if (valuesToRemoveByKey.Count == 0)
        {
            return;
        }

        var current = await GetManyBytesAsync(valuesToRemoveByKey.Keys, ct);
        var toWrite = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var toDelete = new List<string>();

        foreach (var (key, values) in valuesToRemoveByKey)
        {
            if (!current.TryGetValue(key, out var bytes) || bytes is null)
            {
                continue;
            }

            var list = NCacheSerializer.Deserialize<List<string>>(bytes);

            if (list.RemoveAll(values.Contains) == 0)
            {
                continue;
            }

            if (list.Count == 0)
            {
                toDelete.Add(key);
            }
            else
            {
                toWrite[key] = NCacheSerializer.Serialize(list);
            }
        }

        if (toWrite.Count > 0)
        {
            await InsertManyAsync(toWrite, ct);
        }

        if (toDelete.Count > 0)
        {
            await RemoveManyAsync(toDelete, ct);
        }
    }

    private async Task AddToListsAsync(IReadOnlyDictionary<string, IReadOnlyCollection<string>> valuesToAddByKey, CancellationToken ct)
    {
        if (valuesToAddByKey.Count == 0)
        {
            return;
        }

        var current = await GetManyBytesAsync(valuesToAddByKey.Keys, ct);
        var toWrite = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var (key, values) in valuesToAddByKey)
        {
            var list = current.TryGetValue(key, out var bytes) && bytes is not null
                ? NCacheSerializer.Deserialize<List<string>>(bytes)
                : new List<string>();

            var changed = false;

            foreach (var value in values)
            {
                if (!list.Contains(value, StringComparer.Ordinal))
                {
                    list.Add(value);
                    changed = true;
                }
            }

            if (changed)
            {
                toWrite[key] = NCacheSerializer.Serialize(list);
            }
        }

        if (toWrite.Count > 0)
        {
            await InsertManyAsync(toWrite, ct);
        }
    }

    public Task<NCacheJobData?> GetJobAsync(string jobGroup, string jobName, CancellationToken ct = default)
        => GetAsync<NCacheJobData>(NCacheKeys.Job(_prefix, _schedName, jobGroup, jobName), ct);

    public async Task<IReadOnlyDictionary<(string JobGroup, string JobName), NCacheJobData>> GetJobsAsync(IEnumerable<(string JobGroup, string JobName)> keys, CancellationToken ct = default)
    {
        var byKey = keys.Distinct().ToDictionary(k => NCacheKeys.Job(_prefix, _schedName, k.JobGroup, k.JobName), k => k, StringComparer.Ordinal);
        var result = new Dictionary<(string, string), NCacheJobData>();

        if (byKey.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return result;
        }

        foreach (var (cacheKey, bytes) in await GetManyBytesAsync(byKey.Keys, ct))
        {
            if (bytes is not null && byKey.TryGetValue(cacheKey, out var key))
            {
                result[key] = NCacheSerializer.Deserialize<NCacheJobData>(bytes);
            }
        }

        return result;
    }

    public async Task StoreJobAsync(NCacheJobData job, bool replaceExisting, CancellationToken ct = default)
    {
        NCacheJobData? previousJob = replaceExisting
            ? await GetJobAsync(job.JobGroup, job.JobName, ct)
            : null;

        var key = NCacheKeys.Job(_prefix, _schedName, job.JobGroup, job.JobName);

        await Task.Run(() =>
        {
            var bytes = NCacheSerializer.Serialize(job);

            try
            {
                if (replaceExisting) Insert(key, bytes);
                else Add(key, bytes);
            }
            catch (OperationFailedException ex) when (ex.ErrorCode == NCacheErrorCodes.KEY_ALREADY_EXISTS)
            {
                throw new ObjectAlreadyExistsException(
                    $"Job '{job.JobGroup}.{job.JobName}' already exists in scheduler '{_schedName}'.");
            }
        }, ct);

        var headerStored = false;
        var addedToGroupList = false;

        try
        {
            await StoreJobHeaderAsync(ToHeader(job), replaceExisting, ct);
            headerStored = true;

            await AddJobToGroupRegistryAsync(job.JobGroup, job.JobName, ct);
            addedToGroupList = true;

            await AddJobGroupRegistryAsync(job.JobGroup, ct);
        }
        catch
        {
            try
            {
                if (addedToGroupList)
                    await RemoveJobFromGroupRegistryAsync(job.JobGroup, job.JobName, ct);

                if (headerStored)
                    await RemoveJobHeaderAsync(job.JobGroup, job.JobName, ct);

                if (previousJob is not null)
                {
                    await InsertAsync(key, previousJob, ct);
                }
                else
                {
                    await RemoveAsync(key, ct);
                }
            }
            catch
            {
            }

            throw;
        }
    }

    public async Task UpdateJobAsync(NCacheJobData job, CancellationToken ct = default)
    {
        await InsertAsync(NCacheKeys.Job(_prefix, _schedName, job.JobGroup, job.JobName), job, ct);
        await StoreJobHeaderAsync(ToHeader(job), replaceExisting: true, ct);
    }

    public async Task<bool> RemoveJobAsync(string jobGroup, string jobName, CancellationToken ct = default)
    {
        var removed = await RemoveAsync(NCacheKeys.Job(_prefix, _schedName, jobGroup, jobName), ct);

        if (!removed)
        {
            return false;
        }

        await RemoveJobHeaderAsync(jobGroup, jobName, ct);
        await RemoveJobFromGroupRegistryAsync(jobGroup, jobName, ct);

        return true;
    }

    public Task<bool> JobExistsAsync(string jobGroup, string jobName, CancellationToken ct = default)
        => ContainsAsync(NCacheKeys.Job(_prefix, _schedName, jobGroup, jobName), ct);

    public async Task<IReadOnlyList<JobKey>> GetJobsInGroupAsync(string jobGroup, CancellationToken ct = default)
    {
        var jobNames = await GetRegisteredJobNamesInGroupAsync(jobGroup, ct);

        if (jobNames.Count == 0)
        {
            return Array.Empty<JobKey>();
        }

        var existence = await ContainsManyAsync(jobNames.Select(n => NCacheKeys.Job(_prefix, _schedName, jobGroup, n)), ct);

        return jobNames
            .Where(n => existence.TryGetValue(NCacheKeys.Job(_prefix, _schedName, jobGroup, n), out var exists) && exists)
            .Select(n => new JobKey(n, jobGroup))
            .ToList();
    }

    public Task<IReadOnlyCollection<string>> GetAllJobGroupsAsync(CancellationToken ct = default)
        => GetStringCollectionAsync(NCacheKeys.JobGroups(_prefix, _schedName), ct);

    public Task<NCacheTriggerData?> GetTriggerAsync(string triggerGroup, string triggerName, CancellationToken ct = default)
        => GetAsync<NCacheTriggerData>(NCacheKeys.Trigger(_prefix, _schedName, triggerGroup, triggerName), ct);

    public async Task<IReadOnlyDictionary<(string TriggerGroup, string TriggerName), NCacheTriggerData>> GetTriggersAsync(IEnumerable<(string TriggerGroup, string TriggerName)> keys, CancellationToken ct = default)
    {
        var byKey = keys.Distinct().ToDictionary(k => NCacheKeys.Trigger(_prefix, _schedName, k.TriggerGroup, k.TriggerName), k => k, StringComparer.Ordinal);
        var result = new Dictionary<(string, string), NCacheTriggerData>();

        if (byKey.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return result;
        }

        foreach (var (cacheKey, bytes) in await GetManyBytesAsync(byKey.Keys, ct))
        {
            if (bytes is not null && byKey.TryGetValue(cacheKey, out var key))
            {
                result[key] = NCacheSerializer.Deserialize<NCacheTriggerData>(bytes);
            }
        }

        return result;
    }

    public async Task UpdateTriggersAsync(IReadOnlyCollection<NCacheTriggerData> triggers, CancellationToken ct = default)
        => await WriteAsync(await PrepareTriggerUpdatesAsync(triggers, ct), Array.Empty<string>(), ct);

    public async Task<IReadOnlyDictionary<string, byte[]>> PrepareTriggerUpdatesAsync(IReadOnlyCollection<NCacheTriggerData> triggers, CancellationToken ct = default, bool verifyExistence = true)
    {
        ArgumentNullException.ThrowIfNull(triggers);

        var items = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        if (triggers.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return items;
        }

        foreach (var trigger in triggers)
        {
            items[NCacheKeys.Trigger(_prefix, _schedName, trigger.TriggerGroup, trigger.TriggerName)] = NCacheSerializer.Serialize(trigger);
        }

        if (!verifyExistence)
        {
            ct.ThrowIfCancellationRequested();
            return items;
        }

        var existence = await ContainsManyAsync(items.Keys, ct);
        var missing = items.Keys.FirstOrDefault(k => !existence.TryGetValue(k, out var exists) || !exists);

        if (missing is not null)
        {
            throw new JobPersistenceException($"Trigger '{missing}' no longer exists; refusing to update it.");
        }

        return items;
    }

    public async Task<IReadOnlyDictionary<string, byte[]>> PrepareFiredTriggersAsync(IReadOnlyCollection<NCacheFiredTriggerData> fired, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fired);

        var items = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        if (fired.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return items;
        }

        foreach (var record in fired)
        {
            items[NCacheKeys.FiredTrigger(_prefix, _schedName, record.EntryId)] = NCacheSerializer.Serialize(record);
        }

        var registries = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

        foreach (var byInstance in fired.GroupBy(f => f.InstanceName, StringComparer.Ordinal))
        {
            registries[NCacheKeys.InstanceFiredTriggers(_prefix, _schedName, byInstance.Key)] = byInstance.Select(f => f.EntryId).Distinct(StringComparer.Ordinal).ToList();
        }

        foreach (var byJob in fired.Where(KeepsJobFiringRegistry).GroupBy(f => NCacheKeys.JobFiredTriggers(_prefix, _schedName, f.JobGroup!, f.JobName!), StringComparer.Ordinal))
        {
            registries[byJob.Key] = byJob.Select(f => f.EntryId).Distinct(StringComparer.Ordinal).ToList();
        }

        foreach (var (key, bytes) in await PrepareListAdditionsAsync(registries, ct))
        {
            items[key] = bytes;
        }

        return items;
    }

    public IReadOnlyDictionary<string, byte[]> PrepareFiredTriggerUpdates(IReadOnlyCollection<NCacheFiredTriggerData> fired)
    {
        ArgumentNullException.ThrowIfNull(fired);

        var items = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var record in fired)
        {
            items[NCacheKeys.FiredTrigger(_prefix, _schedName, record.EntryId)] = NCacheSerializer.Serialize(record);
        }

        return items;
    }

    public async Task WriteAsync(IReadOnlyDictionary<string, byte[]> inserts, IReadOnlyCollection<string> removes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inserts);
        ArgumentNullException.ThrowIfNull(removes);

        if (inserts.Count > 0)
        {
            await InsertManyAsync(inserts, ct);
        }

        if (removes.Count > 0)
        {
            await RemoveManyAsync(removes, ct);
        }
    }

    private async Task<IReadOnlyDictionary<string, byte[]>> PrepareListAdditionsAsync(IReadOnlyDictionary<string, IReadOnlyCollection<string>> valuesToAddByKey, CancellationToken ct)
    {
        var toWrite = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        if (valuesToAddByKey.Count == 0)
        {
            return toWrite;
        }

        var current = await GetManyBytesAsync(valuesToAddByKey.Keys, ct);

        foreach (var (key, values) in valuesToAddByKey)
        {
            var list = current.TryGetValue(key, out var bytes) && bytes is not null
                ? NCacheSerializer.Deserialize<List<string>>(bytes)
                : new List<string>();

            var changed = false;

            foreach (var value in values)
            {
                if (!list.Contains(value, StringComparer.Ordinal))
                {
                    list.Add(value);
                    changed = true;
                }
            }

            if (changed)
            {
                toWrite[key] = NCacheSerializer.Serialize(list);
            }
        }

        return toWrite;
    }

    public async Task StoreTriggerAsync(NCacheTriggerData trigger, bool replaceExisting, CancellationToken ct = default)
    {
        NCacheTriggerData? previousTrigger = replaceExisting
            ? await GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName, ct)
            : null;

        var key = NCacheKeys.Trigger(_prefix, _schedName, trigger.TriggerGroup, trigger.TriggerName);

        await Task.Run(() =>
        {
            var bytes = NCacheSerializer.Serialize(trigger);

            try
            {
                if (replaceExisting) Insert(key, bytes);
                else Add(key, bytes);
            }
            catch (OperationFailedException ex) when (ex.ErrorCode == NCacheErrorCodes.KEY_ALREADY_EXISTS)
            {
                throw new ObjectAlreadyExistsException(
                    $"Trigger '{trigger.TriggerGroup}.{trigger.TriggerName}' already exists in scheduler '{_schedName}'.");
            }
        }, ct);

        var addedToJobRegistry = false;
        var addedToGroupRegistry = false;

        try
        {
            if (previousTrigger is not null &&
                (previousTrigger.JobGroup != trigger.JobGroup || previousTrigger.JobName != trigger.JobName))
            {
                await RemoveTriggerFromJobRegistryAsync(previousTrigger.JobGroup, previousTrigger.JobName, trigger.TriggerGroup, trigger.TriggerName, ct);
            }

            await AddTriggerToJobRegistryAsync(trigger.JobGroup, trigger.JobName, trigger.TriggerGroup, trigger.TriggerName, ct);
            addedToJobRegistry = true;

            await AddTriggerToGroupRegistryAsync(trigger.TriggerGroup, trigger.TriggerName, ct);
            addedToGroupRegistry = true;

            await AddTriggerGroupRegistryAsync(trigger.TriggerGroup, ct);

            if (previousTrigger is not null)
            {
                await UnregisterContinuationAsync(previousTrigger, ct);
            }

            await RegisterContinuationAsync(trigger, ct);
        }
        catch
        {
            try
            {
                if (addedToGroupRegistry)
                    await RemoveTriggerFromGroupRegistryAsync(trigger.TriggerGroup, trigger.TriggerName, ct);

                if (addedToJobRegistry)
                    await RemoveTriggerFromJobRegistryAsync(trigger.JobGroup, trigger.JobName, trigger.TriggerGroup, trigger.TriggerName, ct);

                if (previousTrigger is not null)
                {
                    await InsertAsync(key, previousTrigger, ct);
                }
                else
                {
                    await RemoveAsync(key, ct);
                }
            }
            catch
            {
            }

            throw;
        }
    }

    public async Task StoreJobsAsync(IReadOnlyCollection<NCacheJobData> jobs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        if (jobs.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return;
        }

        var items = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var job in jobs)
        {
            items[NCacheKeys.Job(_prefix, _schedName, job.JobGroup, job.JobName)] = NCacheSerializer.Serialize(job);
            items[NCacheKeys.JobHeader(_prefix, _schedName, job.JobGroup, job.JobName)] = NCacheSerializer.Serialize(ToHeader(job));
        }

        await InsertManyAsync(items, ct);

        var byGroup = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        var groups = new List<string>();

        foreach (var group in jobs.GroupBy(j => j.JobGroup, StringComparer.Ordinal))
        {
            groups.Add(group.Key);
            byGroup[NCacheKeys.JobsInGroup(_prefix, _schedName, group.Key)] = group.Select(j => j.JobName).Distinct(StringComparer.Ordinal).ToList();
        }

        await AddToListsAsync(byGroup, ct);
        await AddStringsToSetAsync(NCacheKeys.JobGroups(_prefix, _schedName), groups, ct);
    }

    public async Task StoreTriggersAsync(IReadOnlyCollection<NCacheTriggerData> triggers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(triggers);

        if (triggers.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return;
        }

        var items = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var trigger in triggers)
        {
            items[NCacheKeys.Trigger(_prefix, _schedName, trigger.TriggerGroup, trigger.TriggerName)] = NCacheSerializer.Serialize(trigger);
        }

        await InsertManyAsync(items, ct);
        await AddTriggersToJobRegistriesAsync(triggers, ct);

        var byGroup = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        var groups = new List<string>();

        foreach (var group in triggers.GroupBy(t => t.TriggerGroup, StringComparer.Ordinal))
        {
            groups.Add(group.Key);
            byGroup[NCacheKeys.TriggersInGroup(_prefix, _schedName, group.Key)] = group.Select(t => t.TriggerName).Distinct(StringComparer.Ordinal).ToList();
        }

        await AddToListsAsync(byGroup, ct);
        await AddStringsToSetAsync(NCacheKeys.TriggerGroups(_prefix, _schedName), groups, ct);
        await AddTriggerReferencesAsync(ContinuationRegistryEntries(triggers.Where(IsAwaitingContinuation)), ct);
    }

    private static bool IsAwaitingContinuation(NCacheTriggerData trigger)
        => trigger.TriggerState == TriggerStates.Awaiting && trigger.HasContinuationParent;

    private Dictionary<string, List<(string TriggerGroup, string TriggerName)>> ContinuationRegistryEntries(IEnumerable<NCacheTriggerData> children)
    {
        var byParent = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);

        foreach (var child in children)
        {
            var key = NCacheKeys.TriggerContinuations(_prefix, _schedName, child.ContinuesAfterTriggerGroup ?? TriggerKey.DefaultGroup, child.ContinuesAfterTriggerName!);

            if (!byParent.TryGetValue(key, out var list))
            {
                byParent[key] = list = new List<(string, string)>();
            }

            list.Add((child.TriggerGroup, child.TriggerName));
        }

        return byParent;
    }

    private Task RegisterContinuationAsync(NCacheTriggerData child, CancellationToken ct)
        => IsAwaitingContinuation(child)
            ? AddTriggerReferencesAsync(ContinuationRegistryEntries(new[] { child }), ct)
            : Task.CompletedTask;

    private Task UnregisterContinuationAsync(NCacheTriggerData child, CancellationToken ct)
        => child.HasContinuationParent
            ? UnregisterContinuationsAsync(new[] { child }, ct)
            : Task.CompletedTask;

    public Task UnregisterContinuationsAsync(IEnumerable<NCacheTriggerData> children, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(children);

        var byParent = new Dictionary<string, HashSet<(string, string)>>(StringComparer.Ordinal);

        foreach (var (key, list) in ContinuationRegistryEntries(children.Where(c => c.HasContinuationParent)))
        {
            byParent[key] = list.ToHashSet();
        }

        return RemoveTriggerReferencesAsync(byParent, ct);
    }

    public async Task<IReadOnlyDictionary<(string TriggerGroup, string TriggerName), IReadOnlyList<NCacheTriggerData>>> GetAwaitingContinuationsAsync(
        IEnumerable<(string TriggerGroup, string TriggerName)> parents,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parents);

        var result = new Dictionary<(string, string), IReadOnlyList<NCacheTriggerData>>();
        var byRegistry = parents.Distinct().ToDictionary(p => NCacheKeys.TriggerContinuations(_prefix, _schedName, p.TriggerGroup, p.TriggerName), p => p, StringComparer.Ordinal);

        if (byRegistry.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return result;
        }

        var referencesByParent = new Dictionary<(string TriggerGroup, string TriggerName), List<NCacheTriggerReference>>();

        foreach (var (key, bytes) in await GetManyBytesAsync(byRegistry.Keys, ct))
        {
            if (bytes is not null && byRegistry.TryGetValue(key, out var parent))
            {
                referencesByParent[parent] = NCacheSerializer.Deserialize<List<NCacheTriggerReference>>(bytes);
            }
        }

        if (referencesByParent.Count == 0)
        {
            return result;
        }

        var rows = await GetTriggersAsync(referencesByParent.Values.SelectMany(r => r).Select(r => (r.TriggerGroup, r.TriggerName)), ct);

        foreach (var (parent, references) in referencesByParent)
        {
            var awaiting = new List<NCacheTriggerData>(references.Count);

            foreach (var reference in references)
            {
                if (rows.TryGetValue((reference.TriggerGroup, reference.TriggerName), out var row) &&
                    IsAwaitingContinuation(row) &&
                    string.Equals(row.ContinuesAfterTriggerName, parent.TriggerName, StringComparison.Ordinal) &&
                    string.Equals(row.ContinuesAfterTriggerGroup ?? TriggerKey.DefaultGroup, parent.TriggerGroup, StringComparison.Ordinal))
                {
                    awaiting.Add(row);
                }
            }

            if (awaiting.Count > 0)
            {
                result[parent] = awaiting;
            }
        }

        return result;
    }

    private async Task AddTriggerReferencesAsync(IReadOnlyDictionary<string, List<(string TriggerGroup, string TriggerName)>> byRegistryKey, CancellationToken ct)
    {
        if (byRegistryKey.Count == 0)
        {
            return;
        }

        var current = await GetManyBytesAsync(byRegistryKey.Keys, ct);
        var toWrite = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var (key, list) in byRegistryKey)
        {
            var references = current.TryGetValue(key, out var bytes) && bytes is not null
                ? NCacheSerializer.Deserialize<List<NCacheTriggerReference>>(bytes)
                : new List<NCacheTriggerReference>();

            var changed = false;

            foreach (var (group, name) in list)
            {
                if (references.Any(r => r.TriggerGroup == group && r.TriggerName == name))
                {
                    continue;
                }

                references.Add(new NCacheTriggerReference { TriggerGroup = group, TriggerName = name });
                changed = true;
            }

            if (changed)
            {
                toWrite[key] = NCacheSerializer.Serialize(references);
            }
        }

        if (toWrite.Count > 0)
        {
            await InsertManyAsync(toWrite, ct);
        }
    }

    private async Task RemoveTriggerReferencesAsync(IReadOnlyDictionary<string, HashSet<(string TriggerGroup, string TriggerName)>> byRegistryKey, CancellationToken ct)
    {
        if (byRegistryKey.Count == 0)
        {
            return;
        }

        var toWrite = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var toDelete = new List<string>();

        foreach (var (key, bytes) in await GetManyBytesAsync(byRegistryKey.Keys, ct))
        {
            var references = NCacheSerializer.Deserialize<List<NCacheTriggerReference>>(bytes);

            if (references.RemoveAll(r => byRegistryKey[key].Contains((r.TriggerGroup, r.TriggerName))) == 0)
            {
                continue;
            }

            if (references.Count == 0)
            {
                toDelete.Add(key);
            }
            else
            {
                toWrite[key] = NCacheSerializer.Serialize(references);
            }
        }

        await InsertManyAsync(toWrite, ct);
        await RemoveManyAsync(toDelete, ct);
    }

    public Task UnlinkTriggerFromJobAsync(string jobGroup, string jobName, string triggerGroup, string triggerName, CancellationToken ct = default)
        => RemoveTriggerFromJobRegistryAsync(jobGroup, jobName, triggerGroup, triggerName, ct);

    private Task AddTriggersToJobRegistriesAsync(IEnumerable<NCacheTriggerData> triggers, CancellationToken ct)
    {
        var byJob = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);

        foreach (var trigger in triggers)
        {
            var key = NCacheKeys.JobTriggers(_prefix, _schedName, trigger.JobGroup, trigger.JobName);

            if (!byJob.TryGetValue(key, out var list))
            {
                byJob[key] = list = new List<(string, string)>();
            }

            list.Add((trigger.TriggerGroup, trigger.TriggerName));
        }

        return AddTriggerReferencesAsync(byJob, ct);
    }

    public sealed class FiringReadSet
    {
        public required IReadOnlyDictionary<(string TriggerGroup, string TriggerName), NCacheTriggerData> Triggers { get; init; }
        public required IReadOnlyDictionary<string, NCacheFiredTriggerData> Firings { get; init; }
        public required IReadOnlyDictionary<(string JobGroup, string JobName), NCacheJobData> Jobs { get; init; }
        public required IReadOnlyCollection<string> PausedTriggerGroups { get; init; }
    }

    public async Task<FiringReadSet> ReadForFiringAsync(
        IEnumerable<(string TriggerGroup, string TriggerName)> triggerKeys,
        IEnumerable<string> fireInstanceIds,
        IEnumerable<(string JobGroup, string JobName)> jobKeys,
        CancellationToken ct = default)
    {
        var triggerByKey = triggerKeys.Distinct().ToDictionary(k => NCacheKeys.Trigger(_prefix, _schedName, k.TriggerGroup, k.TriggerName), k => k, StringComparer.Ordinal);
        var firingByKey = fireInstanceIds.Distinct(StringComparer.Ordinal).ToDictionary(id => NCacheKeys.FiredTrigger(_prefix, _schedName, id), id => id, StringComparer.Ordinal);
        var jobByKey = jobKeys.Distinct().ToDictionary(k => NCacheKeys.Job(_prefix, _schedName, k.JobGroup, k.JobName), k => k, StringComparer.Ordinal);
        var pausedKey = NCacheKeys.PausedTriggerGroups(_prefix, _schedName);

        var found = await GetManyBytesAsync(triggerByKey.Keys.Concat(firingByKey.Keys).Concat(jobByKey.Keys).Append(pausedKey), ct);

        var triggers = new Dictionary<(string, string), NCacheTriggerData>();
        var firings = new Dictionary<string, NCacheFiredTriggerData>(StringComparer.Ordinal);
        var jobs = new Dictionary<(string, string), NCacheJobData>();
        IReadOnlyCollection<string> paused = Array.Empty<string>();

        foreach (var (cacheKey, bytes) in found)
        {
            if (bytes is null)
            {
                continue;
            }

            if (triggerByKey.TryGetValue(cacheKey, out var triggerKey))
            {
                triggers[triggerKey] = NCacheSerializer.Deserialize<NCacheTriggerData>(bytes);
            }
            else if (firingByKey.TryGetValue(cacheKey, out var entryId))
            {
                firings[entryId] = NCacheSerializer.Deserialize<NCacheFiredTriggerData>(bytes);
            }
            else if (jobByKey.TryGetValue(cacheKey, out var jobKey))
            {
                jobs[jobKey] = NCacheSerializer.Deserialize<NCacheJobData>(bytes);
            }
            else if (cacheKey == pausedKey)
            {
                paused = NCacheSerializer.Deserialize<List<string>>(bytes);
            }
        }

        return new FiringReadSet { Triggers = triggers, Firings = firings, Jobs = jobs, PausedTriggerGroups = paused };
    }

    public Task UpdateTriggerAsync(NCacheTriggerData trigger, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var key = NCacheKeys.Trigger(_prefix, _schedName, trigger.TriggerGroup, trigger.TriggerName);

            if (!_cache.Contains(key))
            {
                throw new JobPersistenceException(
                    $"Trigger '{trigger.TriggerGroup}.{trigger.TriggerName}' no longer exists; refusing to update it.");
            }

            Insert(key, NCacheSerializer.Serialize(trigger));
        }, ct);

    public async Task<bool> RemoveTriggerAsync(string triggerGroup, string triggerName, CancellationToken ct = default)
    {
        var trigger = await GetTriggerAsync(triggerGroup, triggerName, ct);

        if (trigger is null)
        {
            return false;
        }

        var removed = await RemoveAsync(NCacheKeys.Trigger(_prefix, _schedName, triggerGroup, triggerName), ct);

        if (!removed)
        {
            return false;
        }

        await RemoveTriggerFromJobRegistryAsync(trigger.JobGroup, trigger.JobName, triggerGroup, triggerName, ct);
        await RemoveTriggerFromGroupRegistryAsync(triggerGroup, triggerName, ct);
        await UnregisterContinuationAsync(trigger, ct);

        return true;
    }

    public async Task RemoveTriggersAsync(IReadOnlyCollection<NCacheTriggerData> triggers, CancellationToken ct = default)
    {
        if (triggers.Count == 0)
        {
            return;
        }

        await RemoveManyAsync(triggers.Select(t => NCacheKeys.Trigger(_prefix, _schedName, t.TriggerGroup, t.TriggerName)), ct);

        var byJob = triggers
            .GroupBy(t => NCacheKeys.JobTriggers(_prefix, _schedName, t.JobGroup, t.JobName), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(t => (t.TriggerGroup, t.TriggerName)).ToHashSet(), StringComparer.Ordinal);

        await RemoveTriggerReferencesAsync(byJob, ct);
        await UnregisterContinuationsAsync(triggers, ct);

        var groups = triggers.Select(t => t.TriggerGroup).Distinct(StringComparer.Ordinal).ToList();

        await RemoveFromListsAsync(
            triggers.GroupBy(t => t.TriggerGroup, StringComparer.Ordinal).ToDictionary(
                g => NCacheKeys.TriggersInGroup(_prefix, _schedName, g.Key),
                g => g.Select(t => t.TriggerName).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal),
            ct);

        await RemoveEmptyGroupsFromSetAsync(NCacheKeys.TriggerGroups(_prefix, _schedName), groups, g => NCacheKeys.TriggersInGroup(_prefix, _schedName, g), ct);
    }

    public async Task<IReadOnlyList<(string JobGroup, string JobName)>> GetJobsWithoutTriggersAsync(IReadOnlyCollection<(string JobGroup, string JobName)> jobs, CancellationToken ct = default)
    {
        var result = new List<(string JobGroup, string JobName)>();

        if (jobs.Count == 0)
        {
            return result;
        }

        var byRegistry = jobs.Distinct().ToDictionary(j => NCacheKeys.JobTriggers(_prefix, _schedName, j.JobGroup, j.JobName), j => j, StringComparer.Ordinal);
        var referencesByRegistry = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (key, bytes) in await GetManyBytesAsync(byRegistry.Keys, ct))
        {
            referencesByRegistry[key] = NCacheSerializer.Deserialize<List<NCacheTriggerReference>>(bytes)
                .Select(r => NCacheKeys.Trigger(_prefix, _schedName, r.TriggerGroup, r.TriggerName))
                .ToList();
        }

        var rows = await GetManyBytesAsync(referencesByRegistry.Values.SelectMany(r => r).Distinct(StringComparer.Ordinal), ct);

        foreach (var (registryKey, job) in byRegistry)
        {
            var hasTrigger = referencesByRegistry.TryGetValue(registryKey, out var references) && references.Any(r =>
                rows.TryGetValue(r, out var bytes) &&
                NCacheSerializer.Deserialize<NCacheTriggerData>(bytes) is { } row &&
                row.JobGroup == job.JobGroup && row.JobName == job.JobName);

            if (!hasTrigger)
            {
                result.Add(job);
            }
        }

        return result;
    }

    public async Task RemoveJobsAsync(IReadOnlyCollection<(string JobGroup, string JobName)> jobs, CancellationToken ct = default)
    {
        if (jobs.Count == 0)
        {
            return;
        }

        var removed = await RemoveManyAndGetAsync(jobs.Select(j => NCacheKeys.Job(_prefix, _schedName, j.JobGroup, j.JobName)), ct);
        var gone = jobs.Where(j => removed.ContainsKey(NCacheKeys.Job(_prefix, _schedName, j.JobGroup, j.JobName))).ToList();

        if (gone.Count == 0)
        {
            return;
        }

        await RemoveManyAsync(gone.Select(j => NCacheKeys.JobHeader(_prefix, _schedName, j.JobGroup, j.JobName)), ct);

        await RemoveFromListsAsync(
            gone.GroupBy(j => j.JobGroup, StringComparer.Ordinal).ToDictionary(
                g => NCacheKeys.JobsInGroup(_prefix, _schedName, g.Key),
                g => g.Select(j => j.JobName).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal),
            ct);

        await RemoveEmptyGroupsFromSetAsync(NCacheKeys.JobGroups(_prefix, _schedName), gone.Select(j => j.JobGroup).Distinct(StringComparer.Ordinal).ToList(), g => NCacheKeys.JobsInGroup(_prefix, _schedName, g), ct);
    }

    private async Task RemoveEmptyGroupsFromSetAsync(string setKey, IReadOnlyCollection<string> groups, Func<string, string> listKey, CancellationToken ct)
    {
        var byListKey = groups.ToDictionary(listKey, g => g, StringComparer.Ordinal);
        var exists = await ContainsManyAsync(byListKey.Keys, ct);
        var empty = byListKey.Where(kv => !exists.TryGetValue(kv.Key, out var e) || !e).Select(kv => kv.Value).ToHashSet(StringComparer.Ordinal);

        if (empty.Count == 0)
        {
            return;
        }

        var values = new List<string>(await GetStringCollectionAsync(setKey, ct));

        if (values.RemoveAll(empty.Contains) > 0)
        {
            await SaveListOrRemoveAsync(setKey, values, ct);
        }
    }

    public Task<bool> TriggerExistsAsync(string triggerGroup, string triggerName, CancellationToken ct = default)
        => ContainsAsync(NCacheKeys.Trigger(_prefix, _schedName, triggerGroup, triggerName), ct);

    public async Task<IReadOnlyList<NCacheTriggerData>> GetTriggersForJobAsync(string jobGroup, string jobName, CancellationToken ct = default)
    {
        var references = await GetRegisteredTriggerKeysForJobAsync(jobGroup, jobName, ct);

        if (references.Count == 0)
        {
            return Array.Empty<NCacheTriggerData>();
        }

        var rows = await GetTriggersAsync(references.Select(r => (r.TriggerGroup, r.TriggerName)), ct);
        var results = new List<NCacheTriggerData>(references.Count);

        foreach (var reference in references)
        {
            if (rows.TryGetValue((reference.TriggerGroup, reference.TriggerName), out var trigger) &&
                trigger.JobGroup == jobGroup && trigger.JobName == jobName)
            {
                results.Add(trigger);
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<TriggerKey>> GetTriggersInGroupAsync(string triggerGroup, CancellationToken ct = default)
    {
        var triggerNames = await GetRegisteredTriggerNamesInGroupAsync(triggerGroup, ct);

        if (triggerNames.Count == 0)
        {
            return Array.Empty<TriggerKey>();
        }

        var existence = await ContainsManyAsync(triggerNames.Select(n => NCacheKeys.Trigger(_prefix, _schedName, triggerGroup, n)), ct);

        return triggerNames
            .Where(n => existence.TryGetValue(NCacheKeys.Trigger(_prefix, _schedName, triggerGroup, n), out var exists) && exists)
            .Select(n => new TriggerKey(n, triggerGroup))
            .ToList();
    }

    public Task<IReadOnlyCollection<string>> GetAllTriggerGroupsAsync(CancellationToken ct = default)
        => GetStringCollectionAsync(NCacheKeys.TriggerGroups(_prefix, _schedName), ct);

    public async Task<IReadOnlyList<TriggerKey>> GetAllTriggerKeysAsync(CancellationToken ct = default)
    {
        var result = new List<TriggerKey>();

        foreach (var group in await GetAllTriggerGroupsAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            result.AddRange(await GetTriggersInGroupAsync(group, ct));
        }

        return result;
    }

    public async Task<IReadOnlyList<NCacheTriggerData>> GetAllTriggersAsync(CancellationToken ct = default)
    {
        var keys = await GetAllTriggerKeysAsync(ct);
        var rows = await GetTriggersAsync(keys.Select(k => (k.Group, k.Name)), ct);
        var results = new List<NCacheTriggerData>(rows.Count);

        foreach (var key in keys)
        {
            if (rows.TryGetValue((key.Group, key.Name), out var trigger))
            {
                results.Add(trigger);
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<NCacheTriggerData>> GetTriggersForCalendarAsync(string calendarName, CancellationToken ct = default)
    {
        var all = await GetAllTriggersAsync(ct);
        return all.Where(t => string.Equals(t.CalendarName, calendarName, StringComparison.Ordinal)).ToList();
    }

    public Task<NCacheCalendarData?> GetCalendarAsync(string calendarName, CancellationToken ct = default)
        => GetAsync<NCacheCalendarData>(NCacheKeys.Calendar(_prefix, _schedName, calendarName), ct);

    public Task<bool> CalendarExistsAsync(string calendarName, CancellationToken ct = default)
        => ContainsAsync(NCacheKeys.Calendar(_prefix, _schedName, calendarName), ct);

    public async Task StoreCalendarAsync(NCacheCalendarData calendar, bool replaceExisting, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            var key = NCacheKeys.Calendar(_prefix, _schedName, calendar.CalendarName);
            var bytes = NCacheSerializer.Serialize(calendar);

            try
            {
                if (replaceExisting) Insert(key, bytes);
                else Add(key, bytes);
            }
            catch (OperationFailedException ex) when (ex.ErrorCode == NCacheErrorCodes.KEY_ALREADY_EXISTS)
            {
                throw new ObjectAlreadyExistsException(
                    $"Calendar '{calendar.CalendarName}' already exists in scheduler '{_schedName}'.");
            }
        }, ct);

        await AddStringToSetAsync(NCacheKeys.CalendarRegistry(_prefix, _schedName), calendar.CalendarName, ct);
    }

    public async Task<bool> RemoveCalendarAsync(string calendarName, CancellationToken ct = default)
    {
        var removed = await RemoveAsync(NCacheKeys.Calendar(_prefix, _schedName, calendarName), ct);

        if (removed)
        {
            await RemoveStringFromSetAsync(NCacheKeys.CalendarRegistry(_prefix, _schedName), calendarName, ct);
        }

        return removed;
    }

    public Task<IReadOnlyCollection<string>> GetRegisteredCalendarNamesAsync(CancellationToken ct = default)
        => GetStringCollectionAsync(NCacheKeys.CalendarRegistry(_prefix, _schedName), ct);

    public Task<NCacheFiredTriggerData?> GetFiredTriggerAsync(string entryId, CancellationToken ct = default)
        => GetAsync<NCacheFiredTriggerData>(NCacheKeys.FiredTrigger(_prefix, _schedName, entryId), ct);

    private static bool KeepsJobFiringRegistry(NCacheFiredTriggerData fired)
        => fired.IsNonConcurrent && !string.IsNullOrWhiteSpace(fired.JobGroup) && !string.IsNullOrWhiteSpace(fired.JobName);

    public async Task StoreFiredTriggerAsync(NCacheFiredTriggerData fired, CancellationToken ct = default)
    {
        await InsertAsync(NCacheKeys.FiredTrigger(_prefix, _schedName, fired.EntryId), fired, ct);
        await AddFiredTriggerToInstanceRegistryAsync(fired.InstanceName, fired.EntryId, ct);

        if (KeepsJobFiringRegistry(fired))
        {
            await AddFiredTriggerToJobRegistryAsync(fired.JobGroup!, fired.JobName!, fired.EntryId, ct);
        }
    }

    public async Task StoreFiredTriggersAsync(IReadOnlyCollection<NCacheFiredTriggerData> fired, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fired);

        if (fired.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return;
        }

        var items = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var record in fired)
        {
            items[NCacheKeys.FiredTrigger(_prefix, _schedName, record.EntryId)] = NCacheSerializer.Serialize(record);
        }

        await InsertManyAsync(items, ct);

        foreach (var byInstance in fired.GroupBy(f => f.InstanceName, StringComparer.Ordinal))
        {
            var registryKey = NCacheKeys.InstanceFiredTriggers(_prefix, _schedName, byInstance.Key);
            var entryIds = await GetListAsync<string>(registryKey, ct);
            var changed = false;

            foreach (var record in byInstance)
            {
                if (!entryIds.Contains(record.EntryId, StringComparer.Ordinal))
                {
                    entryIds.Add(record.EntryId);
                    changed = true;
                }
            }

            if (changed)
            {
                await InsertAsync(registryKey, entryIds, ct);
            }
        }

        var byJob = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

        foreach (var group in fired
                     .Where(KeepsJobFiringRegistry)
                     .GroupBy(f => NCacheKeys.JobFiredTriggers(_prefix, _schedName, f.JobGroup!, f.JobName!), StringComparer.Ordinal))
        {
            byJob[group.Key] = group.Select(f => f.EntryId).Distinct(StringComparer.Ordinal).ToList();
        }

        await AddToListsAsync(byJob, ct);
    }

    public async Task<IReadOnlyDictionary<string, NCacheFiredTriggerData>> GetFiredTriggersAsync(IEnumerable<string> entryIds, CancellationToken ct = default)
    {
        var byKey = entryIds.Distinct(StringComparer.Ordinal).ToDictionary(id => NCacheKeys.FiredTrigger(_prefix, _schedName, id), id => id, StringComparer.Ordinal);
        var result = new Dictionary<string, NCacheFiredTriggerData>(StringComparer.Ordinal);

        if (byKey.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return result;
        }

        foreach (var (cacheKey, bytes) in await GetManyBytesAsync(byKey.Keys, ct))
        {
            if (bytes is not null && byKey.TryGetValue(cacheKey, out var entryId))
            {
                result[entryId] = NCacheSerializer.Deserialize<NCacheFiredTriggerData>(bytes);
            }
        }

        return result;
    }

    public Task UpdateFiredTriggerAsync(NCacheFiredTriggerData fired, CancellationToken ct = default)
        => InsertAsync(NCacheKeys.FiredTrigger(_prefix, _schedName, fired.EntryId), fired, ct);

    public Task UpdateFiredTriggersAsync(IReadOnlyCollection<NCacheFiredTriggerData> fired, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fired);

        if (fired.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        var items = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var record in fired)
        {
            items[NCacheKeys.FiredTrigger(_prefix, _schedName, record.EntryId)] = NCacheSerializer.Serialize(record);
        }

        return InsertManyAsync(items, ct);
    }

    public async Task<bool> RemoveFiredTriggerAsync(string entryId, CancellationToken ct = default)
        => await RemoveFiredTriggerAndGetAsync(entryId, ct) is not null;

    public async Task<NCacheFiredTriggerData?> RemoveFiredTriggerAndGetAsync(string entryId, CancellationToken ct = default)
    {
        var fired = await RemoveAndGetAsync<NCacheFiredTriggerData>(NCacheKeys.FiredTrigger(_prefix, _schedName, entryId), ct);

        if (fired is null)
        {
            return null;
        }

        await RemoveFiredTriggerFromInstanceRegistryAsync(fired.InstanceName, fired.EntryId, ct);

        if (KeepsJobFiringRegistry(fired))
        {
            await RemoveFiredTriggerFromJobRegistryAsync(fired.JobGroup!, fired.JobName!, fired.EntryId, ct);
        }

        return fired;
    }

    public async Task<IReadOnlyDictionary<string, NCacheFiredTriggerData>> RemoveFiredTriggersAsync(IReadOnlyCollection<string> entryIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entryIds);

        var keyToEntry = entryIds
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(id => NCacheKeys.FiredTrigger(_prefix, _schedName, id), id => id, StringComparer.Ordinal);

        if (keyToEntry.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return new Dictionary<string, NCacheFiredTriggerData>(StringComparer.Ordinal);
        }

        var removedBytes = await RemoveManyAndGetAsync(keyToEntry.Keys, ct);
        var removed = new Dictionary<string, NCacheFiredTriggerData>(StringComparer.Ordinal);

        foreach (var (key, bytes) in removedBytes)
        {
            if (bytes is null)
            {
                continue;
            }

            var fired = NCacheSerializer.Deserialize<NCacheFiredTriggerData>(bytes);
            removed[keyToEntry.TryGetValue(key, out var entryId) ? entryId : fired.EntryId] = fired;
        }

        if (removed.Count == 0)
        {
            return removed;
        }

        foreach (var byInstance in removed.Values.GroupBy(f => f.InstanceName, StringComparer.Ordinal))
        {
            var ids = new HashSet<string>(byInstance.Select(f => f.EntryId), StringComparer.Ordinal);
            var registryKey = NCacheKeys.InstanceFiredTriggers(_prefix, _schedName, byInstance.Key);
            var entryList = await GetListAsync<string>(registryKey, ct);

            if (entryList.RemoveAll(ids.Contains) > 0)
            {
                await SaveListOrRemoveAsync(registryKey, entryList, ct);
            }
        }

        var idsByJobKey = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var fired in removed.Values)
        {
            if (!KeepsJobFiringRegistry(fired))
            {
                continue;
            }

            var registryKey = NCacheKeys.JobFiredTriggers(_prefix, _schedName, fired.JobGroup!, fired.JobName!);

            if (!idsByJobKey.TryGetValue(registryKey, out var ids))
            {
                idsByJobKey[registryKey] = ids = new HashSet<string>(StringComparer.Ordinal);
            }

            ids.Add(fired.EntryId);
        }

        await RemoveFromListsAsync(idsByJobKey, ct);

        return removed;
    }

    public async Task<IReadOnlyList<NCacheFiredTriggerData>> GetFiredTriggersForJobAsync(string jobGroup, string jobName, CancellationToken ct = default)
    {
        var entryIds = await GetRegisteredFiredTriggerIdsForJobAsync(jobGroup, jobName, ct);
        return await LoadFiredTriggersAsync(entryIds, ct);
    }

    public async Task<IReadOnlyList<NCacheFiredTriggerData>> GetFiredTriggersForInstanceAsync(string instanceName, CancellationToken ct = default)
    {
        var entryIds = await GetRegisteredFiredTriggerIdsForInstanceAsync(instanceName, ct);
        return await LoadFiredTriggersAsync(entryIds, ct);
    }

    public async Task<IReadOnlyList<NCacheFiredTriggerData>> GetAllFiredTriggersAsync(CancellationToken ct = default)
    {
        var instanceNames = await GetRegisteredSchedulerInstanceNamesAsync(ct);

        if (instanceNames.Count == 0)
        {
            return Array.Empty<NCacheFiredTriggerData>();
        }

        var registries = await GetManyBytesAsync(instanceNames.Select(n => NCacheKeys.InstanceFiredTriggers(_prefix, _schedName, n)), ct);
        var entryIds = new List<string>();

        foreach (var bytes in registries.Values)
        {
            entryIds.AddRange(NCacheSerializer.Deserialize<List<string>>(bytes));
        }

        return await LoadFiredTriggersAsync(entryIds, ct);
    }

    private async Task<IReadOnlyList<NCacheFiredTriggerData>> LoadFiredTriggersAsync(List<string> entryIds, CancellationToken ct)
    {
        if (entryIds.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return Array.Empty<NCacheFiredTriggerData>();
        }

        var records = await GetFiredTriggersAsync(entryIds, ct);
        var results = new List<NCacheFiredTriggerData>(records.Count);

        foreach (var entryId in entryIds)
        {
            if (records.TryGetValue(entryId, out var fired))
            {
                results.Add(fired);
            }
        }

        return results;
    }

    public async Task CheckInAsync(NCacheSchedulerState state, CancellationToken ct = default)
    {
        await WriteSchedulerStateAsync(state, ct);
        await RegisterSchedulerInstanceAsync(state.InstanceName, ct);
    }

    public Task WriteSchedulerStateAsync(NCacheSchedulerState state, CancellationToken ct = default)
        => InsertAsync(NCacheKeys.SchedulerState(_prefix, _schedName, state.InstanceName), state, ct);

    public Task RegisterSchedulerInstanceAsync(string instanceName, CancellationToken ct = default)
        => AddStringToSetAsync(NCacheKeys.SchedulerInstances(_prefix, _schedName), instanceName, ct);

    public Task UnregisterSchedulerInstanceAsync(string instanceName, CancellationToken ct = default)
        => RemoveStringFromSetAsync(NCacheKeys.SchedulerInstances(_prefix, _schedName), instanceName, ct);

    public Task<NCacheSchedulerState?> GetSchedulerStateAsync(string instanceName, CancellationToken ct = default)
        => GetAsync<NCacheSchedulerState>(NCacheKeys.SchedulerState(_prefix, _schedName, instanceName), ct);

    public async Task<bool> RemoveSchedulerStateAsync(string instanceName, CancellationToken ct = default)
    {
        var removed = await RemoveSchedulerStateRecordAsync(instanceName, ct);
        await UnregisterSchedulerInstanceAsync(instanceName, ct);
        return removed;
    }

    public Task<bool> RemoveSchedulerStateRecordAsync(string instanceName, CancellationToken ct = default)
        => RemoveAsync(NCacheKeys.SchedulerState(_prefix, _schedName, instanceName), ct);

    public Task<IReadOnlyCollection<string>> GetRegisteredSchedulerInstanceNamesAsync(CancellationToken ct = default)
        => GetStringCollectionAsync(NCacheKeys.SchedulerInstances(_prefix, _schedName), ct);

    public async Task<IReadOnlyCollection<NCacheSchedulerState>> GetAllSchedulerStatesAsync(IEnumerable<string> knownInstanceNames, CancellationToken ct = default)
    {
        var keys = knownInstanceNames.Select(n => NCacheKeys.SchedulerState(_prefix, _schedName, n)).ToList();

        if (keys.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return Array.Empty<NCacheSchedulerState>();
        }

        var found = await GetManyBytesAsync(keys, ct);

        return found.Values.Select(bytes => NCacheSerializer.Deserialize<NCacheSchedulerState>(bytes)).ToList();
    }

    public Task<IReadOnlyCollection<string>> GetPausedTriggerGroupsAsync(CancellationToken ct = default)
        => GetStringCollectionAsync(NCacheKeys.PausedTriggerGroups(_prefix, _schedName), ct);

    public Task<IReadOnlyCollection<string>> GetPausedJobGroupsAsync(CancellationToken ct = default)
        => GetStringCollectionAsync(NCacheKeys.PausedJobGroups(_prefix, _schedName), ct);

    public Task AddPausedTriggerGroupAsync(string group, CancellationToken ct = default)
        => AddStringToSetAsync(NCacheKeys.PausedTriggerGroups(_prefix, _schedName), group, ct);

    public Task RemovePausedTriggerGroupAsync(string group, CancellationToken ct = default)
        => RemoveStringFromSetAsync(NCacheKeys.PausedTriggerGroups(_prefix, _schedName), group, ct);

    public Task AddPausedJobGroupAsync(string group, CancellationToken ct = default)
        => AddStringToSetAsync(NCacheKeys.PausedJobGroups(_prefix, _schedName), group, ct);

    public Task RemovePausedJobGroupAsync(string group, CancellationToken ct = default)
        => RemoveStringFromSetAsync(NCacheKeys.PausedJobGroups(_prefix, _schedName), group, ct);

    public Task ClearPausedJobGroupsAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            _cache.Remove<byte[]>(NCacheKeys.PausedJobGroups(_prefix, _schedName), out _);
            _cache.Remove<byte[]>(NCacheKeys.PausedJobGroupRecords(_prefix, _schedName), out _);
        }, ct);

    public Task ClearPausedGroupsAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            _cache.Remove<byte[]>(NCacheKeys.PausedTriggerGroups(_prefix, _schedName), out _);
            _cache.Remove<byte[]>(NCacheKeys.PausedJobGroups(_prefix, _schedName), out _);
            _cache.Remove<byte[]>(NCacheKeys.PausedTriggerGroupRecords(_prefix, _schedName), out _);
            _cache.Remove<byte[]>(NCacheKeys.PausedJobGroupRecords(_prefix, _schedName), out _);
        }, ct);

    public Task<NCachePauseData?> GetTriggerGroupPauseAsync(string group, CancellationToken ct = default)
        => GetGroupPauseAsync(NCacheKeys.PausedTriggerGroupRecords(_prefix, _schedName), group, ct);

    public Task<NCachePauseData?> GetJobGroupPauseAsync(string group, CancellationToken ct = default)
        => GetGroupPauseAsync(NCacheKeys.PausedJobGroupRecords(_prefix, _schedName), group, ct);

    public Task SetTriggerGroupPauseAsync(string group, NCachePauseData? pause, CancellationToken ct = default)
        => SetGroupPauseAsync(NCacheKeys.PausedTriggerGroupRecords(_prefix, _schedName), group, pause, ct);

    public Task SetJobGroupPauseAsync(string group, NCachePauseData? pause, CancellationToken ct = default)
        => SetGroupPauseAsync(NCacheKeys.PausedJobGroupRecords(_prefix, _schedName), group, pause, ct);

    public Task ClearTriggerGroupPausesAsync(CancellationToken ct = default)
        => RemoveAsync(NCacheKeys.PausedTriggerGroupRecords(_prefix, _schedName), ct);

    private async Task<NCachePauseData?> GetGroupPauseAsync(string key, string group, CancellationToken ct)
    {
        var map = await GetAsync<Dictionary<string, NCachePauseData>>(key, ct);
        return map is not null && map.TryGetValue(group, out var pause) ? pause : null;
    }

    private async Task SetGroupPauseAsync(string key, string group, NCachePauseData? pause, CancellationToken ct)
    {
        var map = await GetAsync<Dictionary<string, NCachePauseData>>(key, ct);

        if (pause is null)
        {
            if (map is null || !map.Remove(group))
            {
                return;
            }
        }
        else
        {
            map ??= new Dictionary<string, NCachePauseData>(StringComparer.Ordinal);
            map[group] = pause;
        }

        if (map.Count == 0)
        {
            await RemoveAsync(key, ct);
        }
        else
        {
            await InsertAsync(key, map, ct);
        }
    }

    public Task<NCacheJobHeaderData?> GetJobHeaderAsync(string jobGroup, string jobName, CancellationToken ct = default)
        => GetAsync<NCacheJobHeaderData>(NCacheKeys.JobHeader(_prefix, _schedName, jobGroup, jobName), ct);

    public async Task<IReadOnlyDictionary<(string JobGroup, string JobName), NCacheJobHeaderData>> GetJobHeadersAsync(IEnumerable<(string JobGroup, string JobName)> keys, CancellationToken ct = default)
    {
        var byKey = keys.Distinct().ToDictionary(k => NCacheKeys.JobHeader(_prefix, _schedName, k.JobGroup, k.JobName), k => k, StringComparer.Ordinal);
        var result = new Dictionary<(string, string), NCacheJobHeaderData>();

        if (byKey.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return result;
        }

        foreach (var (cacheKey, bytes) in await GetManyBytesAsync(byKey.Keys, ct))
        {
            if (bytes is not null && byKey.TryGetValue(cacheKey, out var key))
            {
                result[key] = NCacheSerializer.Deserialize<NCacheJobHeaderData>(bytes);
            }
        }

        return result;
    }

    public Task StoreJobHeaderAsync(NCacheJobHeaderData header, bool replaceExisting, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var key = NCacheKeys.JobHeader(_prefix, _schedName, header.JobGroup, header.JobName);
            Insert(key, NCacheSerializer.Serialize(header));
        }, ct);

    public Task<bool> RemoveJobHeaderAsync(string jobGroup, string jobName, CancellationToken ct = default)
        => RemoveAsync(NCacheKeys.JobHeader(_prefix, _schedName, jobGroup, jobName), ct);

    private static NCacheJobHeaderData ToHeader(NCacheJobData job) => new()
    {
        SchedulerName = job.SchedulerName,
        JobName = job.JobName,
        JobGroup = job.JobGroup,
        Description = job.Description,
        JobClassName = job.JobClassName,
        IsDurable = job.IsDurable,
        IsNonConcurrent = job.IsNonConcurrent,
        IsUpdateData = job.IsUpdateData,
        RequestsRecovery = job.RequestsRecovery
    };

    private Task<List<NCacheTriggerReference>> GetRegisteredTriggerKeysForJobAsync(string jobGroup, string jobName, CancellationToken ct)
        => GetListAsync<NCacheTriggerReference>(NCacheKeys.JobTriggers(_prefix, _schedName, jobGroup, jobName), ct);

    private async Task AddTriggerToJobRegistryAsync(string jobGroup, string jobName, string triggerGroup, string triggerName, CancellationToken ct)
    {
        var references = await GetRegisteredTriggerKeysForJobAsync(jobGroup, jobName, ct);

        if (references.Any(x => x.TriggerGroup == triggerGroup && x.TriggerName == triggerName))
        {
            return;
        }

        references.Add(new NCacheTriggerReference { TriggerGroup = triggerGroup, TriggerName = triggerName });

        await InsertAsync(NCacheKeys.JobTriggers(_prefix, _schedName, jobGroup, jobName), references, ct);
    }

    private async Task RemoveTriggerFromJobRegistryAsync(string jobGroup, string jobName, string triggerGroup, string triggerName, CancellationToken ct)
    {
        var references = await GetRegisteredTriggerKeysForJobAsync(jobGroup, jobName, ct);

        if (references.RemoveAll(x => x.TriggerGroup == triggerGroup && x.TriggerName == triggerName) == 0)
        {
            return;
        }

        await SaveListOrRemoveAsync(NCacheKeys.JobTriggers(_prefix, _schedName, jobGroup, jobName), references, ct);
    }

    private Task<List<string>> GetRegisteredJobNamesInGroupAsync(string jobGroup, CancellationToken ct)
        => GetListAsync<string>(NCacheKeys.JobsInGroup(_prefix, _schedName, jobGroup), ct);

    private async Task AddJobToGroupRegistryAsync(string jobGroup, string jobName, CancellationToken ct)
    {
        var names = await GetRegisteredJobNamesInGroupAsync(jobGroup, ct);

        if (names.Contains(jobName, StringComparer.Ordinal))
        {
            return;
        }

        names.Add(jobName);

        await InsertAsync(NCacheKeys.JobsInGroup(_prefix, _schedName, jobGroup), names, ct);
    }

    private async Task RemoveJobFromGroupRegistryAsync(string jobGroup, string jobName, CancellationToken ct)
    {
        var names = await GetRegisteredJobNamesInGroupAsync(jobGroup, ct);

        if (!names.Remove(jobName))
        {
            return;
        }

        await SaveListOrRemoveAsync(NCacheKeys.JobsInGroup(_prefix, _schedName, jobGroup), names, ct);

        if (names.Count == 0)
        {
            await RemoveJobGroupRegistryAsync(jobGroup, ct);
        }
    }

    private Task AddJobGroupRegistryAsync(string jobGroup, CancellationToken ct)
        => AddStringToSetAsync(NCacheKeys.JobGroups(_prefix, _schedName), jobGroup, ct);

    private Task RemoveJobGroupRegistryAsync(string jobGroup, CancellationToken ct)
        => RemoveStringFromSetAsync(NCacheKeys.JobGroups(_prefix, _schedName), jobGroup, ct);

    private Task<List<string>> GetRegisteredTriggerNamesInGroupAsync(string triggerGroup, CancellationToken ct)
        => GetListAsync<string>(NCacheKeys.TriggersInGroup(_prefix, _schedName, triggerGroup), ct);

    private async Task AddTriggerToGroupRegistryAsync(string triggerGroup, string triggerName, CancellationToken ct)
    {
        var names = await GetRegisteredTriggerNamesInGroupAsync(triggerGroup, ct);

        if (names.Contains(triggerName, StringComparer.Ordinal))
        {
            return;
        }

        names.Add(triggerName);

        await InsertAsync(NCacheKeys.TriggersInGroup(_prefix, _schedName, triggerGroup), names, ct);
    }

    private async Task RemoveTriggerFromGroupRegistryAsync(string triggerGroup, string triggerName, CancellationToken ct)
    {
        var names = await GetRegisteredTriggerNamesInGroupAsync(triggerGroup, ct);

        if (!names.Remove(triggerName))
        {
            return;
        }

        await SaveListOrRemoveAsync(NCacheKeys.TriggersInGroup(_prefix, _schedName, triggerGroup), names, ct);

        if (names.Count == 0)
        {
            await RemoveTriggerGroupRegistryAsync(triggerGroup, ct);
        }
    }

    private Task AddTriggerGroupRegistryAsync(string triggerGroup, CancellationToken ct)
        => AddStringToSetAsync(NCacheKeys.TriggerGroups(_prefix, _schedName), triggerGroup, ct);

    private Task RemoveTriggerGroupRegistryAsync(string triggerGroup, CancellationToken ct)
        => RemoveStringFromSetAsync(NCacheKeys.TriggerGroups(_prefix, _schedName), triggerGroup, ct);

    private Task<List<string>> GetRegisteredFiredTriggerIdsForInstanceAsync(string instanceName, CancellationToken ct)
        => GetListAsync<string>(NCacheKeys.InstanceFiredTriggers(_prefix, _schedName, instanceName), ct);

    private async Task AddFiredTriggerToInstanceRegistryAsync(string instanceName, string entryId, CancellationToken ct)
    {
        var entryIds = await GetRegisteredFiredTriggerIdsForInstanceAsync(instanceName, ct);

        if (entryIds.Contains(entryId, StringComparer.Ordinal))
        {
            return;
        }

        entryIds.Add(entryId);

        await InsertAsync(NCacheKeys.InstanceFiredTriggers(_prefix, _schedName, instanceName), entryIds, ct);
    }

    private async Task RemoveFiredTriggerFromInstanceRegistryAsync(string instanceName, string entryId, CancellationToken ct)
    {
        var entryIds = await GetRegisteredFiredTriggerIdsForInstanceAsync(instanceName, ct);

        if (!entryIds.Remove(entryId))
        {
            return;
        }

        await SaveListOrRemoveAsync(NCacheKeys.InstanceFiredTriggers(_prefix, _schedName, instanceName), entryIds, ct);
    }

    private Task<List<string>> GetRegisteredFiredTriggerIdsForJobAsync(string jobGroup, string jobName, CancellationToken ct)
        => GetListAsync<string>(NCacheKeys.JobFiredTriggers(_prefix, _schedName, jobGroup, jobName), ct);

    private async Task AddFiredTriggerToJobRegistryAsync(string jobGroup, string jobName, string entryId, CancellationToken ct)
    {
        var entryIds = await GetRegisteredFiredTriggerIdsForJobAsync(jobGroup, jobName, ct);

        if (entryIds.Contains(entryId, StringComparer.Ordinal))
        {
            return;
        }

        entryIds.Add(entryId);

        await InsertAsync(NCacheKeys.JobFiredTriggers(_prefix, _schedName, jobGroup, jobName), entryIds, ct);
    }

    private async Task RemoveFiredTriggerFromJobRegistryAsync(string jobGroup, string jobName, string entryId, CancellationToken ct)
    {
        var entryIds = await GetRegisteredFiredTriggerIdsForJobAsync(jobGroup, jobName, ct);

        if (!entryIds.Remove(entryId))
        {
            return;
        }

        await SaveListOrRemoveAsync(NCacheKeys.JobFiredTriggers(_prefix, _schedName, jobGroup, jobName), entryIds, ct);
    }

    private sealed class NCacheTriggerReference
    {
        public required string TriggerGroup { get; set; }
        public required string TriggerName { get; set; }
    }

    public Task RebuildRegistriesAsync(CancellationToken ct = default)
        => RebuildRegistriesAsync(includeSchedulerRegistry: true, ct);

    public Task RebuildRegistriesAsync(bool includeSchedulerRegistry, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var scan = ScanPrimaryRecords(ct);

            RebuildJobRegistries(scan.Jobs, scan.JobHeaderKeys);
            RebuildTriggerRegistries(scan.Triggers, scan.JobTriggerRegistryKeys);
            RebuildContinuationRegistries(scan.Triggers, scan.TriggerContinuationRegistryKeys);
            RebuildCalendarRegistry(scan.CalendarNames);
            RebuildFiredTriggerRegistries(scan.FiredTriggers, scan.InstanceFiredRegistryKeys, scan.JobFiredRegistryKeys);

            if (includeSchedulerRegistry)
            {
                RebuildSchedulerRegistry(scan.SchedulerStates, scan.FiredTriggers);
            }
        }, ct);

    private sealed class ScanResult
    {
        public List<NCacheJobData> Jobs { get; } = new();
        public List<NCacheTriggerData> Triggers { get; } = new();
        public List<string> CalendarNames { get; } = new();
        public List<NCacheFiredTriggerData> FiredTriggers { get; } = new();
        public List<NCacheSchedulerState> SchedulerStates { get; } = new();
        public List<string> JobTriggerRegistryKeys { get; } = new();
        public List<string> JobFiredRegistryKeys { get; } = new();
        public List<string> JobHeaderKeys { get; } = new();
        public List<string> TriggerContinuationRegistryKeys { get; } = new();

        public int ReadRetriesLeft { get; set; } = 20;
        public List<string> InstanceFiredRegistryKeys { get; } = new();
    }

    private ScanResult ScanPrimaryRecords(CancellationToken ct)
    {
        var result = new ScanResult();

        var jobPrefix = $"{_prefix}{_schedName}:job:";
        var triggerPrefix = $"{_prefix}{_schedName}:trigger:";
        var calendarPrefix = $"{_prefix}{_schedName}:calendar:";
        var firedPrefix = $"{_prefix}{_schedName}:fired:";
        var statePrefix = $"{_prefix}{_schedName}:state:";
        var instancePrefix = $"{_prefix}{_schedName}:instance:";

        var keys = new List<string>();

        foreach (DictionaryEntry entry in _cache)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.Key is string key && key.StartsWith(_prefix, StringComparison.Ordinal))
            {
                keys.Add(key);
            }
        }

        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();

            if (key.StartsWith(jobPrefix, StringComparison.Ordinal))
            {
                var colon = key.IndexOf(':', jobPrefix.Length);

                if (colon < 0)
                {
                    TryCollect(result, key, result.Jobs, j => j.SchedulerName, j => NCacheKeys.Job(_prefix, _schedName, j.JobGroup, j.JobName));
                }
                else
                {
                    switch (key.Substring(colon))
                    {
                        case ":triggers":
                            result.JobTriggerRegistryKeys.Add(key);
                            break;
                        case ":fired":
                            result.JobFiredRegistryKeys.Add(key);
                            break;
                        case ":header":
                            result.JobHeaderKeys.Add(key);
                            break;
                    }
                }

                continue;
            }

            if (key.StartsWith(triggerPrefix, StringComparison.Ordinal))
            {
                var colon = key.IndexOf(':', triggerPrefix.Length);

                if (colon < 0)
                {
                    TryCollect(result, key, result.Triggers, t => t.SchedulerName, t => NCacheKeys.Trigger(_prefix, _schedName, t.TriggerGroup, t.TriggerName));
                }
                else if (key.Substring(colon) == ":continuations")
                {
                    result.TriggerContinuationRegistryKeys.Add(key);
                }

                continue;
            }

            if (key.StartsWith(calendarPrefix, StringComparison.Ordinal))
            {
                var calendars = new List<NCacheCalendarData>();
                TryCollect(result, key, calendars, c => c.SchedulerName);
                result.CalendarNames.AddRange(calendars.Select(c => c.CalendarName));
                continue;
            }

            if (key.StartsWith(firedPrefix, StringComparison.Ordinal))
            {
                TryCollect(result, key, result.FiredTriggers, f => f.SchedulerName);
                continue;
            }

            if (key.StartsWith(statePrefix, StringComparison.Ordinal))
            {
                TryCollect(result, key, result.SchedulerStates, s => s.SchedulerName);
                continue;
            }

            if (key.StartsWith(instancePrefix, StringComparison.Ordinal) && key.EndsWith(":fired", StringComparison.Ordinal))
            {
                result.InstanceFiredRegistryKeys.Add(key);
            }
        }

        return result;
    }

    private void TryCollect<T>(ScanResult scan, string key, List<T> into, Func<T, string> schedulerName, Func<T, string>? expectedKey = null)
    {
        byte[]? bytes;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                bytes = _cache.Get<byte[]>(key);
                break;
            }
            catch (Exception) when (attempt < ScanReadAttempts && scan.ReadRetriesLeft > 0)
            {
                scan.ReadRetriesLeft--;
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }

        if (bytes is null)
        {
            return;
        }

        try
        {
            var item = NCacheSerializer.Deserialize<T>(bytes);

            if (string.Equals(schedulerName(item), _schedName, StringComparison.Ordinal) &&
                (expectedKey is null || string.Equals(expectedKey(item), key, StringComparison.Ordinal)))
            {
                into.Add(item);
            }
        }
        catch
        {
        }
    }

    private const int ScanReadAttempts = 3;

    private void RebuildJobRegistries(IReadOnlyCollection<NCacheJobData> jobs, IReadOnlyCollection<string> headerKeys)
    {
        var existingHeaders = new HashSet<string>(headerKeys, StringComparer.Ordinal);
        var liveHeaders = new HashSet<string>(StringComparer.Ordinal);

        foreach (var job in jobs)
        {
            var headerKey = NCacheKeys.JobHeader(_prefix, _schedName, job.JobGroup, job.JobName);
            liveHeaders.Add(headerKey);

            if (!existingHeaders.Contains(headerKey))
            {
                Insert(headerKey, NCacheSerializer.Serialize(ToHeader(job)));
            }
        }

        foreach (var staleHeader in existingHeaders.Where(k => !liveHeaders.Contains(k)))
        {
            _cache.Remove<byte[]>(staleHeader, out _);
        }

        var existingGroups = GetRegisteredStringList(NCacheKeys.JobGroups(_prefix, _schedName));
        var jobGroups = jobs.Select(x => x.JobGroup).Distinct(StringComparer.Ordinal).ToList();

        foreach (var staleGroup in existingGroups.Except(jobGroups, StringComparer.Ordinal))
        {
            _cache.Remove<byte[]>(NCacheKeys.JobsInGroup(_prefix, _schedName, staleGroup), out _);
        }

        foreach (var group in jobGroups)
        {
            var jobNames = jobs
                .Where(x => string.Equals(x.JobGroup, group, StringComparison.Ordinal))
                .Select(x => x.JobName)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Insert(NCacheKeys.JobsInGroup(_prefix, _schedName, group), NCacheSerializer.Serialize(jobNames));
        }

        SaveListOrRemove(NCacheKeys.JobGroups(_prefix, _schedName), jobGroups);
    }

    private void RebuildTriggerRegistries(IReadOnlyCollection<NCacheTriggerData> triggers, IReadOnlyCollection<string> jobTriggerRegistryKeys)
    {
        var existingGroups = GetRegisteredStringList(NCacheKeys.TriggerGroups(_prefix, _schedName));
        var triggerGroups = triggers.Select(x => x.TriggerGroup).Distinct(StringComparer.Ordinal).ToList();

        foreach (var staleGroup in existingGroups.Except(triggerGroups, StringComparer.Ordinal))
        {
            _cache.Remove<byte[]>(NCacheKeys.TriggersInGroup(_prefix, _schedName, staleGroup), out _);
        }

        foreach (var group in triggerGroups)
        {
            var triggerNames = triggers
                .Where(x => string.Equals(x.TriggerGroup, group, StringComparison.Ordinal))
                .Select(x => x.TriggerName)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Insert(NCacheKeys.TriggersInGroup(_prefix, _schedName, group), NCacheSerializer.Serialize(triggerNames));
        }

        var liveJobTriggerKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var jobGroup in triggers.GroupBy(x => new { x.JobGroup, x.JobName }))
        {
            var references = jobGroup
                .Select(x => new NCacheTriggerReference { TriggerGroup = x.TriggerGroup, TriggerName = x.TriggerName })
                .ToList();

            var key = NCacheKeys.JobTriggers(_prefix, _schedName, jobGroup.Key.JobGroup, jobGroup.Key.JobName);
            liveJobTriggerKeys.Add(key);
            Insert(key, NCacheSerializer.Serialize(references));
        }

        foreach (var staleKey in jobTriggerRegistryKeys.Where(k => !liveJobTriggerKeys.Contains(k)))
        {
            _cache.Remove<byte[]>(staleKey, out _);
        }

        SaveListOrRemove(NCacheKeys.TriggerGroups(_prefix, _schedName), triggerGroups);
    }

    private void RebuildContinuationRegistries(IReadOnlyCollection<NCacheTriggerData> triggers, IReadOnlyCollection<string> registryKeys)
    {
        var live = ContinuationRegistryEntries(triggers.Where(IsAwaitingContinuation));

        foreach (var (key, children) in live)
        {
            var references = children.Select(c => new NCacheTriggerReference { TriggerGroup = c.TriggerGroup, TriggerName = c.TriggerName }).ToList();
            Insert(key, NCacheSerializer.Serialize(references));
        }

        foreach (var staleKey in registryKeys.Where(k => !live.ContainsKey(k)))
        {
            _cache.Remove<byte[]>(staleKey, out _);
        }
    }

    private void RebuildCalendarRegistry(IReadOnlyCollection<string> calendars)
    {
        SaveListOrRemove(NCacheKeys.CalendarRegistry(_prefix, _schedName), calendars.Distinct(StringComparer.Ordinal).ToList());
    }

    private void RebuildFiredTriggerRegistries(
        IReadOnlyCollection<NCacheFiredTriggerData> firedTriggers,
        IReadOnlyCollection<string> instanceRegistryKeys,
        IReadOnlyCollection<string> jobRegistryKeys)
    {
        var liveKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in firedTriggers.GroupBy(x => x.InstanceName, StringComparer.Ordinal))
        {
            var entryIds = group.Select(x => x.EntryId).Distinct(StringComparer.Ordinal).ToList();
            var key = NCacheKeys.InstanceFiredTriggers(_prefix, _schedName, group.Key);
            liveKeys.Add(key);
            Insert(key, NCacheSerializer.Serialize(entryIds));
        }

        foreach (var group in firedTriggers
                     .Where(KeepsJobFiringRegistry)
                     .GroupBy(x => new { x.JobGroup, x.JobName }))
        {
            var entryIds = group.Select(x => x.EntryId).Distinct(StringComparer.Ordinal).ToList();
            var key = NCacheKeys.JobFiredTriggers(_prefix, _schedName, group.Key.JobGroup!, group.Key.JobName!);
            liveKeys.Add(key);
            Insert(key, NCacheSerializer.Serialize(entryIds));
        }

        foreach (var staleKey in instanceRegistryKeys.Concat(jobRegistryKeys).Where(k => !liveKeys.Contains(k)))
        {
            _cache.Remove<byte[]>(staleKey, out _);
        }
    }

    private void RebuildSchedulerRegistry(IReadOnlyCollection<NCacheSchedulerState> states, IReadOnlyCollection<NCacheFiredTriggerData> firedTriggers)
    {
        var registryKey = NCacheKeys.SchedulerInstances(_prefix, _schedName);
        var instanceNames = states.Select(x => x.InstanceName)
            .Concat(firedTriggers.Select(x => x.InstanceName))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        List<string> registered;

        try
        {
            registered = GetRegisteredStringList(registryKey);
        }
        catch
        {
            registered = new List<string>();
        }

        foreach (var name in registered.Except(instanceNames, StringComparer.Ordinal))
        {
            if (_cache.Contains(NCacheKeys.SchedulerState(_prefix, _schedName, name)))
            {
                instanceNames.Add(name);
            }
        }

        SaveListOrRemove(registryKey, instanceNames);
    }

    private Task<List<T>> GetListAsync<T>(string key, CancellationToken ct)
        => Task.Run(() =>
        {
            var bytes = _cache.Get<byte[]>(key);
            return bytes is null ? new List<T>() : NCacheSerializer.Deserialize<List<T>>(bytes);
        }, ct);

    private Task SaveListOrRemoveAsync<T>(string key, List<T> values, CancellationToken ct)
        => Task.Run(() => SaveListOrRemove(key, values), ct);

    private void SaveListOrRemove<T>(string key, List<T> values)
    {
        if (values.Count == 0)
        {
            _cache.Remove<byte[]>(key, out _);
        }
        else
        {
            Insert(key, NCacheSerializer.Serialize(values));
        }
    }

    private List<string> GetRegisteredStringList(string key)
    {
        var bytes = _cache.Get<byte[]>(key);
        return bytes is null ? new List<string>() : NCacheSerializer.Deserialize<List<string>>(bytes);
    }

    private Task<IReadOnlyCollection<string>> GetStringCollectionAsync(string key, CancellationToken ct)
        => Task.Run(() =>
        {
            var bytes = _cache.Get<byte[]>(key);

            return bytes is null
                ? (IReadOnlyCollection<string>)Array.Empty<string>()
                : NCacheSerializer.Deserialize<List<string>>(bytes);
        }, ct);

    private async Task AddStringToSetAsync(string key, string value, CancellationToken ct)
    {
        var values = new List<string>(await GetStringCollectionAsync(key, ct));

        if (values.Contains(value, StringComparer.Ordinal))
        {
            return;
        }

        values.Add(value);

        await InsertAsync(key, values, ct);
    }

    private async Task AddStringsToSetAsync(string key, IEnumerable<string> newValues, CancellationToken ct)
    {
        var values = new List<string>(await GetStringCollectionAsync(key, ct));
        var changed = false;

        foreach (var value in newValues)
        {
            if (!values.Contains(value, StringComparer.Ordinal))
            {
                values.Add(value);
                changed = true;
            }
        }

        if (changed)
        {
            await InsertAsync(key, values, ct);
        }
    }

    private async Task RemoveStringFromSetAsync(string key, string value, CancellationToken ct)
    {
        var values = new List<string>(await GetStringCollectionAsync(key, ct));

        if (!values.Remove(value))
        {
            return;
        }

        await SaveListOrRemoveAsync(key, values, ct);
    }
}

