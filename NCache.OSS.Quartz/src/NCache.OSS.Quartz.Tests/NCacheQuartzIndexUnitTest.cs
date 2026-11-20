using Alachisoft.NCache.Client;
using NCache.OSS.Quartz.Index;
using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Model;
using NCache.OSS.Quartz.Serialization;
using Xunit;

namespace NCache.OSS.Quartz.Tests
{
    public sealed class NCacheQuartzIndexUnitTest : IAsyncLifetime
    {
        private const string SchedulerName = "NCacheQuartzIndexUnitTest";
        private readonly string _prefix = $"ncache:quartz:index:test:{Guid.NewGuid():N}:";
        private ICache? _cache;
        private NextFireTimeIndex Index => new(_cache!, _prefix, SchedulerName);

        public Task InitializeAsync()
        {
            _cache = TestCache.Get();
            return Task.CompletedTask;
        }

        public Task DisposeAsync()
        {
            _cache = null;
            return Task.CompletedTask;
        }

        private static NCacheTriggerData CreateWaitingTrigger(string triggerGroup, string triggerName, DateTimeOffset nextFire, string jobGroup, string jobName, int priority, int repeatCount)
        {
            return new NCacheTriggerData
            {
                SchedulerName = SchedulerName,
                TriggerName = triggerName,
                TriggerGroup = triggerGroup,
                JobName = jobName,
                JobGroup = jobGroup,
                NextFireTimeUtc = nextFire,
                PreviousFireTimeUtc = null,
                Priority = priority,
                TriggerState = TriggerStates.Waiting,
                TriggerKind = NCacheTriggerKind.Simple,
                StartTimeUtc = nextFire.AddMinutes(-1),
                EndTimeUtc = null,
                CalendarName = null,
                MisfireInstruction = 0,
                Simple = new NCacheSimpleTriggerData
                {
                    RepeatCount = repeatCount,
                    RepeatInterval = TimeSpan.FromSeconds(30),
                    TimesTriggered = 0
                }
            };
        }

        private static (string Group, string Name)[] Keys(IEnumerable<(string TriggerGroup, string TriggerName, long BucketId)> candidates)
            => candidates.Select(c => (c.TriggerGroup, c.TriggerName)).ToArray();

        private static long BucketOf(DateTimeOffset t) => t.ToUnixTimeSeconds() / 2;

        private List<long> ReadActiveBuckets()
        {
            var bytes = _cache!.Get<byte[]>(NCacheKeys.NextFireTimeActiveBuckets(_prefix, SchedulerName));
            return bytes is null ? new List<long>() : NCacheSerializer.Deserialize<List<long>>(bytes);
        }

        private bool BucketKeyExists(long bucketId)
            => _cache!.Contains(NCacheKeys.NextFireTimeIndexBucket(_prefix, SchedulerName, bucketId));

        [Fact]
        public async Task UpsertAsync_adds_waiting_trigger_with_next_fire_time()
        {
            var trigger = CreateWaitingTrigger("k01", "trigger", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            await Index.UpsertAsync(trigger);
            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Single(result);
            Assert.Equal(("k01", "trigger"), Keys(result)[0]);
        }

        [Fact]
        public async Task UpsertAsync_assigns_the_bucket_of_the_next_fire_time_to_the_row()
        {
            var fireTime = DateTimeOffset.UtcNow.AddMinutes(-1);
            var trigger = CreateWaitingTrigger("k01b", "trigger", fireTime, "job-group", "job", 5, 1);

            Assert.Null(trigger.IndexBucketId);

            await Index.UpsertAsync(trigger);

            Assert.Equal(BucketOf(fireTime), trigger.IndexBucketId);

            var candidate = Assert.Single(await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100));
            Assert.Equal(trigger.IndexBucketId, candidate.BucketId);
            Assert.Contains(trigger.IndexBucketId!.Value, ReadActiveBuckets());
        }

        [Fact]
        public async Task UpsertAsync_replaces_existing_entry()
        {
            var first = CreateWaitingTrigger("k02", "trigger", DateTimeOffset.UtcNow.AddMinutes(-2), "job-group", "job", 1, 1);
            await Index.UpsertAsync(first);

            var second = CreateWaitingTrigger("k02", "trigger", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 9, 1);
            second.IndexBucketId = first.IndexBucketId;
            await Index.UpsertAsync(second);

            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Single(result);
            Assert.Equal(("k02", "trigger"), Keys(result)[0]);
            Assert.Equal(second.IndexBucketId, result[0].BucketId);
            Assert.False(BucketKeyExists(first.IndexBucketId!.Value));
        }

        [Fact]
        public async Task UpsertAsync_moves_existing_entry_when_fire_time_changes()
        {
            var first = CreateWaitingTrigger("k03", "trigger", DateTimeOffset.UtcNow.AddMinutes(1), "job-group", "job", 5, 1);
            await Index.UpsertAsync(first);
            var oldBucket = first.IndexBucketId!.Value;

            var second = CreateWaitingTrigger("k03", "trigger", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            second.IndexBucketId = first.IndexBucketId;
            await Index.UpsertAsync(second);

            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Single(result);
            Assert.Equal(("k03", "trigger"), Keys(result)[0]);
            Assert.NotEqual(oldBucket, second.IndexBucketId);
            Assert.DoesNotContain(oldBucket, ReadActiveBuckets());
        }

        [Fact]
        public async Task UpsertAsync_without_the_old_bucket_id_leaves_a_stale_entry_that_reads_dedupe()
        {
            var first = CreateWaitingTrigger("k03b", "trigger", DateTimeOffset.UtcNow.AddMinutes(-2), "job-group", "job", 5, 1);
            await Index.UpsertAsync(first);

            var second = CreateWaitingTrigger("k03b", "trigger", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            await Index.UpsertAsync(second);

            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Single(result);
            Assert.Equal(("k03b", "trigger"), Keys(result)[0]);
            Assert.True(BucketKeyExists(first.IndexBucketId!.Value));
            Assert.True(BucketKeyExists(second.IndexBucketId!.Value));
        }

        [Fact]
        public async Task UpsertAsync_removes_non_waiting_trigger()
        {
            var trigger = CreateWaitingTrigger("k04", "trigger", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            await Index.UpsertAsync(trigger);
            trigger.TriggerState = TriggerStates.Paused;
            await Index.UpsertAsync(trigger);
            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Empty(result);
            Assert.Null(trigger.IndexBucketId);
        }

        [Fact]
        public async Task UpsertAsync_removes_trigger_without_next_fire_time()
        {
            var trigger = CreateWaitingTrigger("k05", "trigger", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            await Index.UpsertAsync(trigger);
            trigger.NextFireTimeUtc = null;
            await Index.UpsertAsync(trigger);
            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Empty(result);
            Assert.Null(trigger.IndexBucketId);
        }

        [Fact]
        public async Task UpsertAsync_does_not_duplicate_trigger()
        {
            var trigger = CreateWaitingTrigger("k06", "trigger", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            await Index.UpsertAsync(trigger);
            await Index.UpsertAsync(trigger);
            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Single(result);
            Assert.Equal(("k06", "trigger"), Keys(result)[0]);
        }

        [Fact]
        public async Task UpsertManyAsync_keeps_the_last_row_for_a_duplicated_key()
        {
            var fireTime = DateTimeOffset.UtcNow.AddMinutes(-1);
            var earlier = CreateWaitingTrigger("k06b", "trigger", fireTime, "job-group", "job", 1, 1);
            var later = CreateWaitingTrigger("k06b", "trigger", fireTime, "job-group", "job", 9, 1);

            await Index.UpsertManyAsync(new[] { earlier, later });

            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Single(result);
            Assert.Null(earlier.IndexBucketId);
            Assert.Equal(BucketOf(fireTime), later.IndexBucketId);
        }

        [Fact]
        public async Task UpsertManyAsync_with_an_empty_batch_writes_nothing()
        {
            await Index.UpsertManyAsync(Array.Empty<NCacheTriggerData>());

            Assert.Empty(ReadActiveBuckets());
            Assert.Empty(await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddHours(1), 100));
        }

        [Fact]
        public async Task RemoveAsync_removes_existing_entry()
        {
            var trigger = CreateWaitingTrigger("k07", "trigger", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            await Index.UpsertAsync(trigger);
            var bucket = trigger.IndexBucketId!.Value;

            await Index.RemoveAsync(trigger);

            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Empty(result);
            Assert.Null(trigger.IndexBucketId);
            Assert.False(BucketKeyExists(bucket));
            Assert.DoesNotContain(bucket, ReadActiveBuckets());
        }

        [Fact]
        public async Task RemoveAsync_is_noop_for_missing_entry()
        {
            var never = CreateWaitingTrigger("k08", "missing", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);

            await Index.RemoveAsync(never);

            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Empty(result);
            Assert.Empty(ReadActiveBuckets());
        }

        [Fact]
        public async Task RemoveAsync_does_not_modify_other_entries()
        {
            var first = CreateWaitingTrigger("k09", "first", DateTimeOffset.UtcNow.AddMinutes(-2), "job-group", "job", 5, 1);
            var second = CreateWaitingTrigger("k09", "second", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            await Index.UpsertAsync(first);
            await Index.UpsertAsync(second);
            await Index.RemoveAsync(first);
            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Single(result);
            Assert.Equal(("k09", "second"), Keys(result)[0]);
        }

        [Fact]
        public async Task RemoveAsync_leaves_a_shared_bucket_in_place_for_the_remaining_entry()
        {
            var fireTime = DateTimeOffset.UtcNow.AddMinutes(-1);
            var first = CreateWaitingTrigger("k09b", "first", fireTime, "job-group", "job", 5, 1);
            var second = CreateWaitingTrigger("k09b", "second", fireTime, "job-group", "job", 5, 1);
            await Index.UpsertManyAsync(new[] { first, second });
            Assert.Equal(first.IndexBucketId, second.IndexBucketId);

            await Index.RemoveAsync(first);

            Assert.True(BucketKeyExists(second.IndexBucketId!.Value));
            Assert.Contains(second.IndexBucketId.Value, ReadActiveBuckets());
            Assert.Equal(new[] { ("k09b", "second") }, Keys(await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100)));
        }

        [Fact]
        public async Task RemoveManyAsync_removes_every_entry_that_carries_a_bucket_id()
        {
            var first = CreateWaitingTrigger("k09c", "first", DateTimeOffset.UtcNow.AddMinutes(-3), "job-group", "job", 5, 1);
            var second = CreateWaitingTrigger("k09c", "second", DateTimeOffset.UtcNow.AddMinutes(-2), "job-group", "job", 5, 1);
            var third = CreateWaitingTrigger("k09c", "third", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            await Index.UpsertManyAsync(new[] { first, second, third });

            var neverIndexed = CreateWaitingTrigger("k09c", "never", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);

            await Index.RemoveManyAsync(new[] { first, third, neverIndexed });

            Assert.Equal(new[] { ("k09c", "second") }, Keys(await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100)));
            Assert.Null(first.IndexBucketId);
            Assert.Null(third.IndexBucketId);
            Assert.NotNull(second.IndexBucketId);
        }

        [Fact]
        public async Task ApplyAsync_drops_stale_candidates_and_applies_upserts_in_one_pass()
        {
            var stale = CreateWaitingTrigger("k09d", "stale", DateTimeOffset.UtcNow.AddMinutes(-2), "job-group", "job", 5, 1);
            var acquired = CreateWaitingTrigger("k09d", "acquired", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            var untouched = CreateWaitingTrigger("k09d", "untouched", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            await Index.UpsertManyAsync(new[] { stale, acquired, untouched });

            acquired.TriggerState = TriggerStates.Acquired;

            await Index.ApplyAsync(
                new[] { (stale.TriggerGroup, stale.TriggerName, stale.IndexBucketId!.Value) },
                new[] { acquired });

            Assert.Equal(new[] { ("k09d", "untouched") }, Keys(await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100)));
            Assert.Null(acquired.IndexBucketId);
        }

        [Fact]
        public async Task ApplyAsync_with_nothing_to_do_writes_nothing()
        {
            var trigger = CreateWaitingTrigger("k09e", "trigger", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            await Index.UpsertAsync(trigger);
            var activeBefore = ReadActiveBuckets();

            await Index.ApplyAsync(Array.Empty<(string, string, long)>(), Array.Empty<NCacheTriggerData>());

            Assert.Equal(activeBefore, ReadActiveBuckets());
            Assert.Single(await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100));
        }

        [Fact]
        public async Task GetDueCandidatesAsync_returns_due_triggers()
        {
            var trigger = CreateWaitingTrigger("k10", "trigger", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            await Index.UpsertAsync(trigger);
            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Single(result);
            Assert.Equal(("k10", "trigger"), Keys(result)[0]);
        }

        [Fact]
        public async Task GetDueCandidatesAsync_excludes_future_triggers()
        {
            var trigger = CreateWaitingTrigger("k11", "trigger", DateTimeOffset.UtcNow.AddMinutes(10), "job-group", "job", 5, 1);
            await Index.UpsertAsync(trigger);
            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Empty(result);
        }

        [Fact]
        public async Task GetDueCandidatesAsync_excludes_a_later_entry_in_the_same_bucket_as_a_due_one()
        {
            var window = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 2 * 2);
            var due = CreateWaitingTrigger("k11b", "due", window.AddMilliseconds(200), "job-group", "job", 5, 1);
            var notYet = CreateWaitingTrigger("k11b", "not-yet", window.AddMilliseconds(1800), "job-group", "job", 5, 1);
            await Index.UpsertManyAsync(new[] { due, notYet });
            Assert.Equal(due.IndexBucketId, notYet.IndexBucketId);

            var result = await Index.GetDueCandidatesAsync(window.AddSeconds(1), 100);

            Assert.Equal(new[] { ("k11b", "due") }, Keys(result));
        }

        [Fact]
        public async Task GetDueCandidatesAsync_respects_max_count()
        {
            for (var i = 0; i < 5; i++)
            {
                var trigger = CreateWaitingTrigger("k12", $"trigger-{i}", DateTimeOffset.UtcNow.AddMinutes(-5 + i), "job-group", "job", 5, 1);
                await Index.UpsertAsync(trigger);
            }

            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 2);
            Assert.Equal(2, result.Count);
            Assert.Equal(new[] { ("k12", "trigger-0"), ("k12", "trigger-1") }, Keys(result));
        }

        [Fact]
        public async Task GetDueCandidatesAsync_returns_fire_time_order()
        {
            var first = CreateWaitingTrigger("k13", "first", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            var second = CreateWaitingTrigger("k13", "second", DateTimeOffset.UtcNow.AddMinutes(-3), "job-group", "job", 5, 1);
            var third = CreateWaitingTrigger("k13", "third", DateTimeOffset.UtcNow.AddMinutes(-2), "job-group", "job", 5, 1);
            await Index.UpsertAsync(first);
            await Index.UpsertAsync(second);
            await Index.UpsertAsync(third);
            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Equal(new[] { ("k13", "second"), ("k13", "third"), ("k13", "first") }, Keys(result));
        }

        [Fact]
        public async Task GetDueCandidatesAsync_orders_equal_fire_times_by_priority_descending()
        {
            var fireTime = DateTimeOffset.UtcNow.AddMinutes(-1);
            var low = CreateWaitingTrigger("k14", "low", fireTime, "job-group", "job", 1, 1);
            var high = CreateWaitingTrigger("k14", "high", fireTime, "job-group", "job", 10, 1);
            var medium = CreateWaitingTrigger("k14", "medium", fireTime, "job-group", "job", 5, 1);
            await Index.UpsertAsync(low);
            await Index.UpsertAsync(high);
            await Index.UpsertAsync(medium);
            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Equal(new[] { ("k14", "high"), ("k14", "medium"), ("k14", "low") }, Keys(result));
        }

        [Fact]
        public async Task GetDueCandidatesAsync_orders_within_a_bucket_by_fire_time_before_priority()
        {
            var window = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 2 * 2).AddMinutes(-1);
            var earlyLow = CreateWaitingTrigger("k14b", "early-low", window.AddMilliseconds(100), "job-group", "job", 1, 1);
            var lateHigh = CreateWaitingTrigger("k14b", "late-high", window.AddMilliseconds(1500), "job-group", "job", 10, 1);
            await Index.UpsertManyAsync(new[] { lateHigh, earlyLow });
            Assert.Equal(earlyLow.IndexBucketId, lateHigh.IndexBucketId);

            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);

            Assert.Equal(new[] { ("k14b", "early-low"), ("k14b", "late-high") }, Keys(result));
        }

        [Fact]
        public async Task GetDueCandidatesAsync_returns_empty_when_no_entries_are_due()
        {
            var first = CreateWaitingTrigger("k15", "first", DateTimeOffset.UtcNow.AddMinutes(5), "job-group", "job", 5, 1);
            var second = CreateWaitingTrigger("k15", "second", DateTimeOffset.UtcNow.AddMinutes(10), "job-group", "job", 5, 1);
            await Index.UpsertAsync(first);
            await Index.UpsertAsync(second);
            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Empty(result);
        }

        [Fact]
        public async Task GetDueCandidatesAsync_returns_empty_when_the_index_has_never_been_written()
        {
            Assert.Empty(await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddHours(1), 100));
        }

        [Fact]
        public async Task RebuildAsync_indexes_only_waiting_triggers()
        {
            var waiting = CreateWaitingTrigger("k16", "waiting", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            var paused = CreateWaitingTrigger("k16", "paused", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            paused.TriggerState = TriggerStates.Paused;
            var blocked = CreateWaitingTrigger("k16", "blocked", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            blocked.TriggerState = TriggerStates.Blocked;
            await Index.RebuildAsync(new[] { waiting, paused, blocked });
            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Single(result);
            Assert.Equal(("k16", "waiting"), Keys(result)[0]);
            Assert.NotNull(waiting.IndexBucketId);
            Assert.Null(paused.IndexBucketId);
            Assert.Null(blocked.IndexBucketId);
        }

        [Fact]
        public async Task RebuildAsync_excludes_triggers_without_next_fire_time()
        {
            var waitingWithTime = CreateWaitingTrigger("k17", "with-time", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            var waitingWithoutTime = CreateWaitingTrigger("k17", "without-time", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            waitingWithoutTime.NextFireTimeUtc = null;
            await Index.RebuildAsync(new[] { waitingWithTime, waitingWithoutTime });
            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Single(result);
            Assert.Equal(("k17", "with-time"), Keys(result)[0]);
        }

        [Fact]
        public async Task RebuildAsync_orders_entries()
        {
            var first = CreateWaitingTrigger("k18", "first", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            var second = CreateWaitingTrigger("k18", "second", DateTimeOffset.UtcNow.AddMinutes(-3), "job-group", "job", 5, 1);
            var third = CreateWaitingTrigger("k18", "third", DateTimeOffset.UtcNow.AddMinutes(-2), "job-group", "job", 5, 1);
            await Index.RebuildAsync(new[] { first, second, third });
            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Equal(new[] { ("k18", "second"), ("k18", "third"), ("k18", "first") }, Keys(result));
        }

        [Fact]
        public async Task RebuildAsync_replaces_previous_index()
        {
            var oldTrigger = CreateWaitingTrigger("k19", "old", DateTimeOffset.UtcNow.AddMinutes(-5), "job-group", "job", 5, 1);
            await Index.UpsertAsync(oldTrigger);
            var oldBucket = oldTrigger.IndexBucketId!.Value;

            var newTrigger = CreateWaitingTrigger("k19", "new", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            await Index.RebuildAsync(new[] { newTrigger });

            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Single(result);
            Assert.Equal(("k19", "new"), Keys(result)[0]);
            Assert.False(BucketKeyExists(oldBucket));
            Assert.Equal(new[] { newTrigger.IndexBucketId!.Value }, ReadActiveBuckets());
        }

        [Fact]
        public async Task RebuildAsync_handles_empty_input()
        {
            var trigger = CreateWaitingTrigger("k20", "trigger", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            await Index.UpsertAsync(trigger);
            await Index.RebuildAsync(Array.Empty<NCacheTriggerData>());
            var result = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100);
            Assert.Empty(result);
            Assert.Empty(ReadActiveBuckets());
        }

        [Fact]
        public async Task RebuildAsync_resets_a_stale_bucket_id_carried_by_the_row()
        {
            var trigger = CreateWaitingTrigger("k20b", "trigger", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            trigger.IndexBucketId = 42;

            await Index.RebuildAsync(new[] { trigger });

            Assert.Equal(BucketOf(trigger.NextFireTimeUtc!.Value), trigger.IndexBucketId);
            Assert.False(BucketKeyExists(42));
        }

        [Fact]
        public async Task UpsertAsync_honors_cancellation()
        {
            var trigger = CreateWaitingTrigger("k21", "trigger", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Index.UpsertAsync(trigger, cts.Token));
        }

        [Fact]
        public async Task RemoveAsync_honors_cancellation()
        {
            var trigger = CreateWaitingTrigger("k22", "trigger", DateTimeOffset.UtcNow.AddMinutes(-1), "job-group", "job", 5, 1);
            await Index.UpsertAsync(trigger);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Index.RemoveAsync(trigger, cts.Token));
        }

        [Fact]
        public async Task GetDueCandidatesAsync_honors_cancellation()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow, 100, cts.Token));
        }

        [Fact]
        public async Task RebuildAsync_honors_cancellation()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Index.RebuildAsync(Array.Empty<NCacheTriggerData>(), cts.Token));
        }
    }
}

