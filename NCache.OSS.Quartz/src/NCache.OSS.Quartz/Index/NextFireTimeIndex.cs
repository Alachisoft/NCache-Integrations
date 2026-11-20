using Alachisoft.NCache.Client;
using NCache.OSS.Quartz.Index;
using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Model;
using NCache.OSS.Quartz.Serialization;

public sealed class NextFireTimeIndex
{
    private const int BucketSeconds = 2;

    private readonly ICache _cache;
    private readonly string _prefix;
    private readonly string _schedName;

    public NextFireTimeIndex(ICache cache, string prefix, string schedName)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _prefix = prefix;
        _schedName = schedName;
    }

    private static long BucketOf(DateTimeOffset t) => t.ToUnixTimeSeconds() / BucketSeconds;

    private string BucketKey(long bucketId) => NCacheKeys.NextFireTimeIndexBucket(_prefix, _schedName, bucketId);

    private string ActiveBucketsKey => NCacheKeys.NextFireTimeActiveBuckets(_prefix, _schedName);

    private static bool BelongsInIndex(NCacheTriggerData trigger) => trigger.TriggerState == TriggerStates.Waiting && trigger.NextFireTimeUtc is not null;

    public Task UpsertAsync(NCacheTriggerData trigger, CancellationToken ct = default) => UpsertManyAsync(new[] { trigger }, ct);

    public Task UpsertManyAsync(IEnumerable<NCacheTriggerData> triggers, CancellationToken ct = default)
        => ApplyAsync(Array.Empty<(string, string, long)>(), triggers, ct);

    public async Task ApplyAsync(IEnumerable<(string TriggerGroup, string TriggerName, long BucketId)> removals, IEnumerable<NCacheTriggerData> upserts, CancellationToken ct = default)
    {
        var pending = await PrepareApplyAsync(removals, upserts, ct);
        await WriteAsync(pending, ct);
    }

    public sealed record PendingWrite(IReadOnlyDictionary<string, byte[]> Inserts, IReadOnlyList<string> Removes)
    {
        public static readonly PendingWrite Empty = new(new Dictionary<string, byte[]>(), Array.Empty<string>());
    }

    public Task<PendingWrite> PrepareUpsertManyAsync(IEnumerable<NCacheTriggerData> triggers, CancellationToken ct = default)
        => PrepareApplyAsync(Array.Empty<(string, string, long)>(), triggers, ct);

    public async Task<PendingWrite> PrepareApplyAsync(IEnumerable<(string TriggerGroup, string TriggerName, long BucketId)> removals, IEnumerable<NCacheTriggerData> upserts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(removals);
        ArgumentNullException.ThrowIfNull(upserts);

        var removeList = removals.ToList();
        var batch = DistinctByKey(upserts);

        if (removeList.Count == 0 && batch.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return PendingWrite.Empty;
        }

        var touched = CollectTouchedBuckets(batch);

        foreach (var removal in removeList)
        {
            touched.Add(removal.BucketId);
        }

        var snapshot = await LoadBucketsAsync(touched, ct);

        foreach (var (group, name, bucketId) in removeList)
        {
            snapshot.Buckets[bucketId].RemoveAll(e => e.TriggerGroup == group && e.TriggerName == name);
        }

        RemoveOldEntries(batch, snapshot.Buckets);

        foreach (var trigger in batch)
        {
            ApplyToBuckets(trigger, snapshot.Buckets);
        }

        return BuildWrite(snapshot, rewriteActive: false);
    }

    public Task WriteAsync(PendingWrite pending, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (pending.Inserts.Count > 0)
            {
                ThrowIfAnyFailed(_cache.InsertBulk(pending.Inserts.ToDictionary(kv => kv.Key, kv => CreateItem(kv.Value), StringComparer.Ordinal)));
            }

            if (pending.Removes.Count > 0)
            {
                _cache.RemoveBulk(pending.Removes);
            }
        }, ct);

    public Task RemoveAsync(NCacheTriggerData trigger, CancellationToken ct = default) => RemoveManyAsync(new[] { trigger }, ct);

    public async Task RemoveManyAsync(IEnumerable<NCacheTriggerData> triggers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(triggers);

        var batch = triggers.Where(t => t.IndexBucketId is not null).ToList();

        if (batch.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return;
        }

        var touched = new HashSet<long>(batch.Select(t => t.IndexBucketId!.Value));
        var snapshot = await LoadBucketsAsync(touched, ct);

        RemoveOldEntries(batch, snapshot.Buckets);

        foreach (var trigger in batch)
        {
            trigger.IndexBucketId = null;
        }

        await SaveTouchedBucketsAsync(snapshot, ct);
    }

    public async Task<IReadOnlyList<(string TriggerGroup, string TriggerName, long BucketId)>> GetDueCandidatesAsync(DateTimeOffset noLaterThan, int maxCount, CancellationToken ct = default)
        => (await GetDueCandidatesAsync(noLaterThan, DateTimeOffset.MinValue, maxCount, 0, ct)).Due;

    public sealed record DueCandidates(
        IReadOnlyList<(string TriggerGroup, string TriggerName, long BucketId)> Due,
        IReadOnlyList<(string TriggerGroup, string TriggerName, long BucketId)> Misfired)
    {
        public bool IsEmpty => Due.Count == 0 && Misfired.Count == 0;
    }

    public async Task<DueCandidates> GetDueCandidatesAsync(DateTimeOffset noLaterThan, DateTimeOffset misfiredBefore, int maxDue, int maxMisfired, CancellationToken ct = default)
    {
        var upTo = BucketOf(noLaterThan);
        var (snapshot, dueBuckets) = await LoadDueBucketsAsync(noLaterThan, upTo, ct);
        var due = new List<(string, string, long)>();
        var misfired = new List<(string, string, long)>();
        var seen = new HashSet<(string, string)>();

        if (dueBuckets.Count == 0)
        {
            return new DueCandidates(due, misfired);
        }

        foreach (var bucketId in dueBuckets)
        {
            if (!snapshot.Buckets.TryGetValue(bucketId, out var entries))
            {
                continue;
            }

            var dueEntries = entries.Where(e => e.NextFireTimeUtc <= noLaterThan).OrderBy(e => e.NextFireTimeUtc).ThenByDescending(e => e.Priority);

            foreach (var entry in dueEntries)
            {
                if (!seen.Add((entry.TriggerGroup, entry.TriggerName)))
                {
                    continue;
                }

                if (entry.NextFireTimeUtc < misfiredBefore)
                {
                    if (misfired.Count < maxMisfired)
                    {
                        misfired.Add((entry.TriggerGroup, entry.TriggerName, bucketId));
                    }
                }
                else if (due.Count < maxDue)
                {
                    due.Add((entry.TriggerGroup, entry.TriggerName, bucketId));
                }

                if (due.Count >= maxDue && misfired.Count >= maxMisfired)
                {
                    break;
                }
            }

            if (due.Count >= maxDue && misfired.Count >= maxMisfired)
            {
                break;
            }
        }

        return new DueCandidates(due, misfired);
    }

    private static readonly TimeSpan SpeculativeLookBack = TimeSpan.FromSeconds(8);

    private async Task<(IndexSnapshot Snapshot, List<long> DueBuckets)> LoadDueBucketsAsync(DateTimeOffset noLaterThan, long upTo, CancellationToken ct)
    {
        var from = BucketOf(noLaterThan - SpeculativeLookBack);
        var speculative = new List<long>();

        for (var bucketId = from; bucketId <= upTo; bucketId++)
        {
            speculative.Add(bucketId);
        }

        var snapshot = await LoadBucketsAsync(speculative, ct, keepMissing: false);
        var dueBuckets = snapshot.Active.Where(b => b <= upTo).OrderBy(b => b).ToList();
        var missing = dueBuckets.Where(b => !snapshot.Buckets.ContainsKey(b)).ToList();

        if (missing.Count > 0)
        {
            var rest = await LoadBucketsAsync(missing, ct, keepMissing: false);

            foreach (var (bucketId, entries) in rest.Buckets)
            {
                snapshot.Buckets[bucketId] = entries;
            }
        }

        return (snapshot, dueBuckets);
    }

    public async Task RebuildAsync(IEnumerable<NCacheTriggerData> triggers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        ct.ThrowIfCancellationRequested();

        var oldActive = await LoadActiveBucketsForRebuildAsync(ct);
        var byBucket = new Dictionary<long, List<NCacheNextFireTimeIndexEntry>>();

        foreach (var trigger in triggers)
        {
            ct.ThrowIfCancellationRequested();

            if (!BelongsInIndex(trigger))
            {
                trigger.IndexBucketId = null;
                continue;
            }

            var bucketId = BucketOf(trigger.NextFireTimeUtc!.Value);

            if (!byBucket.TryGetValue(bucketId, out var entries))
            {
                byBucket[bucketId] = entries = new List<NCacheNextFireTimeIndexEntry>();
            }

            entries.Add(new NCacheNextFireTimeIndexEntry { TriggerGroup = trigger.TriggerGroup, TriggerName = trigger.TriggerName, NextFireTimeUtc = trigger.NextFireTimeUtc.Value, Priority = trigger.Priority });
            trigger.IndexBucketId = bucketId;
        }

        foreach (var staleBucket in oldActive.Where(b => !byBucket.ContainsKey(b)))
        {
            byBucket[staleBucket] = new List<NCacheNextFireTimeIndexEntry>();
        }

        await SaveTouchedBucketsAsync(new IndexSnapshot(byBucket, oldActive), ct, rewriteActive: true);
    }

    private Task<List<long>> LoadActiveBucketsForRebuildAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            var bytes = _cache.Get<byte[]>(ActiveBucketsKey);

            if (bytes is not null)
            {
                try
                {
                    return NCacheSerializer.Deserialize<List<long>>(bytes);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
                {
                }
            }
            else
            {
                return new List<long>();
            }

            var bucketPrefix = BucketKey(0)[..^1];
            var found = new List<long>();

            foreach (System.Collections.DictionaryEntry entry in _cache)
            {
                ct.ThrowIfCancellationRequested();

                if (entry.Key is string key &&
                    key.StartsWith(bucketPrefix, StringComparison.Ordinal) &&
                    long.TryParse(key.AsSpan(bucketPrefix.Length), out var bucketId))
                {
                    found.Add(bucketId);
                }
            }

            return found;
        }, ct);

    private static List<NCacheTriggerData> DistinctByKey(IEnumerable<NCacheTriggerData> triggers)
    {
        var byKey = new Dictionary<(string, string), NCacheTriggerData>();

        foreach (var trigger in triggers)
        {
            byKey[(trigger.TriggerGroup, trigger.TriggerName)] = trigger;
        }

        return byKey.Values.ToList();
    }

    private static HashSet<long> CollectTouchedBuckets(List<NCacheTriggerData> batch)
    {
        var touched = new HashSet<long>();

        foreach (var trigger in batch)
        {
            if (trigger.IndexBucketId is { } oldBucket) touched.Add(oldBucket);
            if (BelongsInIndex(trigger)) touched.Add(BucketOf(trigger.NextFireTimeUtc!.Value));
        }

        return touched;
    }

    private static void RemoveOldEntries(List<NCacheTriggerData> batch, Dictionary<long, List<NCacheNextFireTimeIndexEntry>> loaded)
    {
        var byOldBucket = new Dictionary<long, HashSet<(string, string)>>();

        foreach (var trigger in batch)
        {
            if (trigger.IndexBucketId is { } oldBucket)
            {
                if (!byOldBucket.TryGetValue(oldBucket, out var keys))
                {
                    byOldBucket[oldBucket] = keys = new HashSet<(string, string)>();
                }

                keys.Add((trigger.TriggerGroup, trigger.TriggerName));
            }
        }

        foreach (var (bucketId, keys) in byOldBucket)
        {
            loaded[bucketId].RemoveAll(e => keys.Contains((e.TriggerGroup, e.TriggerName)));
        }
    }

    private static void ApplyToBuckets(NCacheTriggerData trigger, Dictionary<long, List<NCacheNextFireTimeIndexEntry>> loaded)
    {
        if (!BelongsInIndex(trigger))
        {
            trigger.IndexBucketId = null;
            return;
        }

        var newBucket = BucketOf(trigger.NextFireTimeUtc!.Value);
        loaded[newBucket].Add(new NCacheNextFireTimeIndexEntry { TriggerGroup = trigger.TriggerGroup, TriggerName = trigger.TriggerName, NextFireTimeUtc = trigger.NextFireTimeUtc!.Value, Priority = trigger.Priority });
        trigger.IndexBucketId = newBucket;
    }

    private static void Sort(List<NCacheNextFireTimeIndexEntry> entries) => entries.Sort((a, b) => a.NextFireTimeUtc.CompareTo(b.NextFireTimeUtc) is var byFireTime && byFireTime != 0 ? byFireTime : b.Priority.CompareTo(a.Priority));

    private PendingWrite BuildWrite(IndexSnapshot snapshot, bool rewriteActive)
    {
        var toInsert = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var toRemove = new List<string>();
        var activeSet = new HashSet<long>(snapshot.Active);
        var changed = rewriteActive;

        foreach (var (bucketId, entries) in snapshot.Buckets)
        {
            if (entries.Count == 0)
            {
                toRemove.Add(BucketKey(bucketId));
                changed |= activeSet.Remove(bucketId);
            }
            else
            {
                Sort(entries);
                toInsert[BucketKey(bucketId)] = NCacheSerializer.Serialize(entries);
                changed |= activeSet.Add(bucketId);
            }
        }

        if (changed)
        {
            toInsert[ActiveBucketsKey] = NCacheSerializer.Serialize(activeSet.ToList());
        }

        return new PendingWrite(toInsert, toRemove);
    }

    private Task SaveTouchedBucketsAsync(IndexSnapshot snapshot, CancellationToken ct, bool rewriteActive = false)
        => WriteAsync(BuildWrite(snapshot, rewriteActive), ct);

    private sealed record IndexSnapshot(Dictionary<long, List<NCacheNextFireTimeIndexEntry>> Buckets, List<long> Active);

    private Task<IndexSnapshot> LoadBucketsAsync(IEnumerable<long> bucketIds, CancellationToken ct, bool keepMissing = true) =>
        Task.Run(() =>
        {
            var ids = bucketIds.Distinct().ToList();
            var keys = ids.Select(BucketKey).Append(ActiveBucketsKey).ToList();
            var found = _cache.GetBulk<byte[]>(keys);
            var buckets = new Dictionary<long, List<NCacheNextFireTimeIndexEntry>>(ids.Count);

            foreach (var bucketId in ids)
            {
                buckets[bucketId] = found.TryGetValue(BucketKey(bucketId), out var bytes) && bytes is not null
                    ? NCacheSerializer.Deserialize<List<NCacheNextFireTimeIndexEntry>>(bytes)
                    : new List<NCacheNextFireTimeIndexEntry>();
            }

            if (!keepMissing)
            {
                foreach (var bucketId in ids)
                {
                    if (buckets[bucketId].Count == 0 && !found.ContainsKey(BucketKey(bucketId)))
                    {
                        buckets.Remove(bucketId);
                    }
                }
            }

            var active = found.TryGetValue(ActiveBucketsKey, out var activeBytes) && activeBytes is not null
                ? NCacheSerializer.Deserialize<List<long>>(activeBytes)
                : new List<long>();

            return new IndexSnapshot(buckets, active);
        }, ct);
    private Task<List<long>> LoadActiveBucketsAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            var bytes = _cache.Get<byte[]>(ActiveBucketsKey);
            return bytes is null ? new List<long>() : NCacheSerializer.Deserialize<List<long>>(bytes);
        }, ct);

    private static CacheItem CreateItem(byte[] bytes) => new(bytes) { Priority = Alachisoft.NCache.Runtime.CacheItemPriority.NotRemovable };

    private static void ThrowIfAnyFailed(IDictionary<string, Exception> failed)
    {
        if (failed.Count > 0)
        {
            var (key, error) = failed.First();
            throw new InvalidOperationException($"Bulk index write failed for {failed.Count} key(s), e.g. '{key}': {error.Message}", error);
        }
    }
}

