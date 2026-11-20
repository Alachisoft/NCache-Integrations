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
public sealed class NCacheQuartzJobStoreCrudUnitTest : NCacheQuartzStoreTestBase
{
    [Fact]
    public async Task ScheduleJob_persists_job_trigger_and_index()
    {
        var store = CreateStore("instance-1");

        var job = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .WithDescription("Test job")
            .StoreDurably()
            .Build();

        var trigger = CreateQuartzSimpleTrigger(
            "trigger-1",
            "tests",
            job.Key,
            DateTimeOffset.UtcNow.AddMinutes(1));

        await store.ScheduleJob(job, trigger);

        var storedJob = await Storage.GetJobAsync(
            job.Key.Group,
            job.Key.Name);

        var storedTrigger = await Storage.GetTriggerAsync(
            trigger.Key.Group,
            trigger.Key.Name);

        var candidates = await Index.GetDueCandidatesAsync(
            DateTimeOffset.UtcNow.AddMinutes(2),
            10);

        Assert.NotNull(storedJob);
        Assert.NotNull(storedTrigger);
        Assert.Equal(job.Key.Name, storedJob.JobName);
        Assert.Equal(job.Key.Group, storedJob.JobGroup);
        Assert.Equal(trigger.Key.Name, storedTrigger.TriggerName);
        Assert.Equal(trigger.Key.Group, storedTrigger.TriggerGroup);
        Assert.Equal(TriggerStates.Waiting, storedTrigger.TriggerState);
        Assert.Contains(
            candidates,
            x => x.TriggerGroup == trigger.Key.Group &&
                 x.TriggerName == trigger.Key.Name);
    }

    [Fact]
    public async Task ScheduleJob_rejects_trigger_for_different_job()
    {
        var store = CreateStore("instance-1");

        var job = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        var trigger = CreateQuartzSimpleTrigger(
            "trigger-1",
            "tests",
            new JobKey("different-job", "tests"),
            DateTimeOffset.UtcNow.AddMinutes(1));

        await Assert.ThrowsAsync<JobPersistenceException>(
            async () => await store.ScheduleJob(job, trigger));

        Assert.False(await Storage.JobExistsAsync("tests", "job-1"));
        Assert.False(await Storage.TriggerExistsAsync("tests", "trigger-1"));
    }

    [Fact]
    public async Task ScheduleJob_rejects_existing_job()
    {
        var store = CreateStore("instance-1");

        var existingJob = CreateJob(
            "job-1",
            durable: true);

        await Storage.StoreJobAsync(
            existingJob,
            replaceExisting: false);

        var job = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        var trigger = CreateQuartzSimpleTrigger(
            "trigger-1",
            "tests",
            job.Key,
            DateTimeOffset.UtcNow.AddMinutes(1));

        await Assert.ThrowsAsync<ObjectAlreadyExistsException>(
            async () => await store.ScheduleJob(job, trigger));

        Assert.True(await Storage.JobExistsAsync("tests", "job-1"));
        Assert.False(await Storage.TriggerExistsAsync("tests", "trigger-1"));
    }

    [Fact]
    public async Task AddJob_rejects_duplicate_without_replace()
    {
        var store = CreateStore("instance-1");

        var firstJob = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .WithDescription("Original")
            .StoreDurably()
            .Build();

        await store.AddJob(firstJob);

        var duplicateJob = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .WithDescription("Replacement")
            .StoreDurably()
            .Build();

        await Assert.ThrowsAsync<ObjectAlreadyExistsException>(
            async () => await store.AddJob(duplicateJob));

        var stored = await Storage.GetJobAsync("tests", "job-1");

        Assert.NotNull(stored);
        Assert.Equal("Original", stored.Description);
    }

    [Fact]
    public async Task AddJob_replaces_existing_job()
    {
        var store = CreateStore("instance-1");

        var firstJob = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .WithDescription("Original")
            .StoreDurably()
            .Build();

        await store.AddJob(firstJob);

        var replacementJob = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .WithDescription("Replacement")
            .StoreDurably()
            .Build();

        await store.AddJob(
            replacementJob,
            new AddJobOptions
            {
                Replace = true
            });

        var stored = await Storage.GetJobAsync("tests", "job-1");

        Assert.NotNull(stored);
        Assert.Equal("Replacement", stored.Description);
    }

    [Fact]
    public async Task AddTrigger_requires_existing_job()
    {
        var store = CreateStore("instance-1");

        var jobKey = new JobKey("missing-job", "tests");

        var trigger = CreateQuartzSimpleTrigger(
            "trigger-1",
            "tests",
            jobKey,
            DateTimeOffset.UtcNow.AddMinutes(1));

        await Assert.ThrowsAsync<JobPersistenceException>(
            async () => await store.AddTrigger(trigger));

        Assert.False(await Storage.TriggerExistsAsync("tests", "trigger-1"));
    }

    [Fact]
    public async Task AddTrigger_rejects_duplicate_without_replace()
    {
        var store = CreateStore("instance-1");

        var job = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.AddJob(job);

        var firstTrigger = CreateQuartzSimpleTrigger(
            "trigger-1",
            "tests",
            job.Key,
            DateTimeOffset.UtcNow.AddMinutes(1));

        await store.AddTrigger(firstTrigger);

        var duplicateTrigger = CreateQuartzSimpleTrigger(
            "trigger-1",
            "tests",
            job.Key,
            DateTimeOffset.UtcNow.AddMinutes(2));

        await Assert.ThrowsAsync<ObjectAlreadyExistsException>(
            async () => await store.AddTrigger(duplicateTrigger));

        var stored = await Storage.GetTriggerAsync(
            "tests",
            "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(
            firstTrigger.StartTimeUtc,
            stored.StartTimeUtc);
    }

    [Fact]
    public async Task AddTrigger_replaces_existing_trigger_and_preserves_state()
    {
        var store = CreateStore("instance-1");

        var job = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.AddJob(job);

        var originalTrigger = CreateWaitingTrigger(
            "trigger-1",
            "job-1",
            DateTimeOffset.UtcNow.AddMinutes(1));

        originalTrigger.TriggerState = TriggerStates.Paused;

        await Storage.StoreTriggerAsync(
            originalTrigger,
            replaceExisting: false);

        await Index.UpsertAsync(originalTrigger);

        var replacementTrigger = CreateQuartzSimpleTrigger(
            "trigger-1",
            "tests",
            job.Key,
            DateTimeOffset.UtcNow.AddMinutes(5));

        await store.AddTrigger(
            replacementTrigger,
            new AddTriggerOptions
            {
                Replace = true
            });

        var stored = await Storage.GetTriggerAsync(
            "tests",
            "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(
            TriggerStates.Paused,
            stored.TriggerState);
        Assert.Equal(
            replacementTrigger.StartTimeUtc,
            stored.StartTimeUtc);
    }

    [Fact]
    public async Task GetJob_returns_null_for_missing_job()
    {
        var store = CreateStore("instance-1");

        var result = await store.GetJob(
            new JobKey("missing-job", "tests"));

        Assert.Null(result);
    }

    [Fact]
    public async Task GetTrigger_returns_null_for_missing_trigger()
    {
        var store = CreateStore("instance-1");

        var result = await store.GetTrigger(
            new TriggerKey("missing-trigger", "tests"));

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteTrigger_removes_non_durable_orphan_job()
    {
        var store = CreateStore("instance-1");

        var job = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .Build();

        await store.AddJob(job);

        var trigger = CreateQuartzSimpleTrigger(
            "trigger-1",
            "tests",
            job.Key,
            DateTimeOffset.UtcNow.AddMinutes(1));

        await store.AddTrigger(trigger);

        var deleted = await store.DeleteTrigger(
            trigger.Key);

        Assert.True(deleted);
        Assert.False(await Storage.TriggerExistsAsync("tests", "trigger-1"));
        Assert.False(await Storage.JobExistsAsync("tests", "job-1"));

        var candidates = await Index.GetDueCandidatesAsync(
            DateTimeOffset.UtcNow.AddMinutes(2),
            10);

        Assert.DoesNotContain(
            candidates,
            x => x.TriggerGroup == "tests" &&
                 x.TriggerName == "trigger-1");
    }

    [Fact]
    public async Task DeleteTrigger_preserves_durable_job()
    {
        var store = CreateStore("instance-1");

        var job = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.AddJob(job);

        var trigger = CreateQuartzSimpleTrigger(
            "trigger-1",
            "tests",
            job.Key,
            DateTimeOffset.UtcNow.AddMinutes(1));

        await store.AddTrigger(trigger);

        var deleted = await store.DeleteTrigger(
            trigger.Key);

        Assert.True(deleted);
        Assert.False(await Storage.TriggerExistsAsync("tests", "trigger-1"));
        Assert.True(await Storage.JobExistsAsync("tests", "job-1"));
    }

    [Fact]
    public async Task DeleteJob_removes_job_triggers_and_index_entries()
    {
        var store = CreateStore("instance-1");

        var job = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.AddJob(job);

        var trigger1 = CreateQuartzSimpleTrigger(
            "trigger-1",
            "tests",
            job.Key,
            DateTimeOffset.UtcNow.AddMinutes(1));

        var trigger2 = CreateQuartzSimpleTrigger(
            "trigger-2",
            "tests",
            job.Key,
            DateTimeOffset.UtcNow.AddMinutes(2));

        await store.AddTrigger(trigger1);
        await store.AddTrigger(trigger2);

        var deleted = await store.DeleteJob(
            job.Key);

        Assert.True(deleted);
        Assert.False(await Storage.JobExistsAsync("tests", "job-1"));
        Assert.False(await Storage.TriggerExistsAsync("tests", "trigger-1"));
        Assert.False(await Storage.TriggerExistsAsync("tests", "trigger-2"));

        var candidates = await Index.GetDueCandidatesAsync(
            DateTimeOffset.UtcNow.AddMinutes(3),
            10);

        Assert.DoesNotContain(
            candidates,
            x => x.TriggerGroup == "tests" &&
                 (x.TriggerName == "trigger-1" ||
                  x.TriggerName == "trigger-2"));
    }

    [Fact]
    public async Task DeleteJob_returns_false_when_job_does_not_exist()
    {
        var store = CreateStore("instance-1");

        var deleted = await store.DeleteJob(
            new JobKey("missing-job", "tests"));

        Assert.False(deleted);
    }
    [Fact]
    public async Task ReplaceTrigger_returns_false_when_trigger_does_not_exist()
    {
        var store = CreateStore("instance-1");

        var job = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.AddJob(job);

        var replacement = CreateQuartzSimpleTrigger(
            "replacement",
            "tests",
            job.Key,
            DateTimeOffset.UtcNow.AddMinutes(1));

        var replaced = await store.ReplaceTrigger(
            new TriggerKey("missing", "tests"),
            replacement);

        Assert.False(replaced);
        Assert.False(await Storage.TriggerExistsAsync("tests", "replacement"));
    }

    [Fact]
    public async Task ReplaceTrigger_rejects_replacement_for_different_job()
    {
        var store = CreateStore("instance-1");

        var job1 = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        var job2 = JobBuilder.Create<TestJob>()
            .WithIdentity("job-2", "tests")
            .StoreDurably()
            .Build();

        await store.AddJob(job1);
        await store.AddJob(job2);

        var existing = CreateQuartzSimpleTrigger(
            "trigger-1",
            "tests",
            job1.Key,
            DateTimeOffset.UtcNow.AddMinutes(1));

        await store.AddTrigger(existing);

        var replacement = CreateQuartzSimpleTrigger(
            "trigger-1",
            "tests",
            job2.Key,
            DateTimeOffset.UtcNow.AddMinutes(2));

        await Assert.ThrowsAsync<JobPersistenceException>(
            async () => await store.ReplaceTrigger(
                existing.Key,
                replacement));

        var stored = await Storage.GetTriggerAsync(
            "tests",
            "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal("job-1", stored.JobName);
        Assert.Equal("tests", stored.JobGroup);
    }

    [Fact]
    public async Task ReplaceTrigger_rejects_replacement_key_collision()
    {
        var store = CreateStore("instance-1");

        var job = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.AddJob(job);

        var trigger1 = CreateQuartzSimpleTrigger(
            "trigger-1",
            "tests",
            job.Key,
            DateTimeOffset.UtcNow.AddMinutes(1));

        var trigger2 = CreateQuartzSimpleTrigger(
            "trigger-2",
            "tests",
            job.Key,
            DateTimeOffset.UtcNow.AddMinutes(2));

        await store.AddTrigger(trigger1);
        await store.AddTrigger(trigger2);

        var replacement = CreateQuartzSimpleTrigger(
            "trigger-2",
            "tests",
            job.Key,
            DateTimeOffset.UtcNow.AddMinutes(3));

        await Assert.ThrowsAsync<ObjectAlreadyExistsException>(
            async () => await store.ReplaceTrigger(
                trigger1.Key,
                replacement));

        Assert.True(await Storage.TriggerExistsAsync("tests", "trigger-1"));
        Assert.True(await Storage.TriggerExistsAsync("tests", "trigger-2"));
    }

    [Fact]
    public async Task ReplaceTrigger_replaces_trigger_and_preserves_existing_state()
    {
        var store = CreateStore("instance-1");

        var job = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.AddJob(job);

        var existing = CreateWaitingTrigger(
            "trigger-1",
            "job-1",
            DateTimeOffset.UtcNow.AddMinutes(1));

        existing.TriggerState = TriggerStates.Paused;
        existing.PreviousFireTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-5);

        await Storage.StoreTriggerAsync(
            existing,
            replaceExisting: false);

        var replacement = CreateQuartzSimpleTrigger(
            "trigger-1",
            "tests",
            job.Key,
            DateTimeOffset.UtcNow.AddMinutes(5));

        await store.ReplaceTrigger(new TriggerKey(existing.TriggerName, existing.TriggerGroup), replacement);

        var stored = await Storage.GetTriggerAsync(
            "tests",
            "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Paused, stored.TriggerState);
        Assert.Equal(
            existing.PreviousFireTimeUtc,
            stored.PreviousFireTimeUtc);
        Assert.Equal(
            replacement.StartTimeUtc,
            stored.StartTimeUtc);
    }

    [Fact]
    public async Task ReplaceTrigger_preserves_existing_automatic_preferred_node_claim()
    {
        var store = CreateStore("instance-1");

        var job = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.AddJob(job);

        var existing = CreateWaitingTrigger(
            "trigger-1",
            "job-1",
            DateTimeOffset.UtcNow.AddMinutes(1));

        existing.PreferredNodeIsAutomatic = true;
        existing.PreferredNode = "instance-1";

        await Storage.StoreTriggerAsync(existing, replaceExisting: false);
        await Index.UpsertAsync(existing);

        var roundTripped = (await store.GetTrigger(new TriggerKey("trigger-1", "tests")))!;
        Assert.Equal(PreferredNode.Auto, roundTripped.PreferredNode);
        roundTripped.StartTimeUtc = DateTimeOffset.UtcNow.AddMinutes(5);
        roundTripped.ComputeFirstFireTimeUtc(null);

        await store.ReplaceTrigger(new TriggerKey(existing.TriggerName, existing.TriggerGroup), roundTripped);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.True(stored.PreferredNodeIsAutomatic);
        Assert.Equal("instance-1", stored.PreferredNode);
    }

    [Fact]
    public async Task ReplaceTrigger_honours_an_explicit_change_away_from_an_automatic_claim()
    {
        var store = CreateStore("instance-1");

        var job = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.AddJob(job);

        var existing = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(1));
        existing.PreferredNodeIsAutomatic = true;
        existing.PreferredNode = "instance-1";

        await Storage.StoreTriggerAsync(existing, replaceExisting: false);
        await Index.UpsertAsync(existing);

        var none = CreateQuartzSimpleTrigger("trigger-1", "tests", job.Key, DateTimeOffset.UtcNow.AddMinutes(5));
        await store.ReplaceTrigger(new TriggerKey("trigger-1", "tests"), none);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");
        Assert.NotNull(stored);
        Assert.False(stored.PreferredNodeIsAutomatic);
        Assert.Null(stored.PreferredNode);

        var pinned = CreateQuartzSimpleTrigger("trigger-1", "tests", job.Key, DateTimeOffset.UtcNow.AddMinutes(5));
        pinned.PreferredNode = PreferredNode.For("instance-9");
        await store.ReplaceTrigger(new TriggerKey("trigger-1", "tests"), pinned);

        stored = await Storage.GetTriggerAsync("tests", "trigger-1");
        Assert.NotNull(stored);
        Assert.False(stored.PreferredNodeIsAutomatic);
        Assert.Equal("instance-9", stored.PreferredNode);
    }

    [Fact]
    public async Task ReplaceTrigger_revives_a_completed_trigger()
    {
        var store = CreateStore("instance-1");

        var job = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.AddJob(job);

        var existing = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddMinutes(-1));
        existing.TriggerState = TriggerStates.Complete;
        existing.NextFireTimeUtc = null;

        await Storage.StoreTriggerAsync(existing, replaceExisting: false);

        var replacement = CreateQuartzSimpleTrigger("trigger-1", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), repeatCount: 5);

        Assert.True(await store.ReplaceTrigger(new TriggerKey("trigger-1", "tests"), replacement));

        var stored = await Storage.GetTriggerAsync("tests", "trigger-1");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Waiting, stored.TriggerState);
        Assert.Single(await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 5)));
    }

    [Fact]
    public async Task AddTrigger_replace_blocks_new_trigger_while_non_concurrent_job_is_executing()
    {
        var store = CreateStore("instance-1");

        var job = CreateJob("job-1", nonConcurrent: true);
        var running = CreateWaitingTrigger("trigger-1", "job-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        await StoreJobAndTriggerAsync(job, running);

        var acquired = Assert.Single(await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 1)));
        Assert.NotNull(Assert.Single(await store.TriggersFired(new[] { acquired })).TriggerFiredBundle);

        var second = CreateQuartzSimpleTrigger("trigger-2", "tests", new JobKey("job-1", "tests"), DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.AddTrigger(second);

        var stored = await Storage.GetTriggerAsync("tests", "trigger-2");

        Assert.NotNull(stored);
        Assert.Equal(TriggerStates.Blocked, stored.TriggerState);
        Assert.Empty(await store.AcquireNextTriggers(CreateRequest(DateTimeOffset.UtcNow, 5)));
    }

    [Fact]
    public async Task ReplaceTrigger_can_change_trigger_key()
    {
        var store = CreateStore("instance-1");

        var job = JobBuilder.Create<TestJob>()
            .WithIdentity("job-1", "tests")
            .StoreDurably()
            .Build();

        await store.AddJob(job);

        var existing = CreateQuartzSimpleTrigger(
            "trigger-1",
            "tests",
            job.Key,
            DateTimeOffset.UtcNow.AddMinutes(1));

        await store.AddTrigger(existing);

        var replacement = CreateQuartzSimpleTrigger(
            "trigger-2",
            "tests",
            job.Key,
            DateTimeOffset.UtcNow.AddMinutes(2));

        var replaced = await store.ReplaceTrigger(
            existing.Key,
            replacement);

        Assert.True(replaced);
        Assert.False(await Storage.TriggerExistsAsync("tests", "trigger-1"));
        Assert.True(await Storage.TriggerExistsAsync("tests", "trigger-2"));

        var candidates = await Index.GetDueCandidatesAsync(
            DateTimeOffset.UtcNow.AddMinutes(3),
            10);

        Assert.DoesNotContain(
            candidates,
            x => x.TriggerGroup == "tests" &&
                 x.TriggerName == "trigger-1");

        Assert.Contains(
            candidates,
            x => x.TriggerGroup == "tests" &&
                 x.TriggerName == "trigger-2");
    }
}

