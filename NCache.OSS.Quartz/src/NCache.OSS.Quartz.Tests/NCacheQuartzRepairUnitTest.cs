using Alachisoft.NCache.Client;
using NCache.OSS.Quartz.Index;
using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Locking;
using NCache.OSS.Quartz.Model;
using NCache.OSS.Quartz.Serialization;
using NCache.OSS.Quartz.Storage;
using Quartz;
using Quartz.Extensibility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace NCache.OSS.Quartz.Tests
{
    public sealed class NCacheQuartzRepairUnitTest
    {
        private const string SchedulerName = "NCacheQuartzRepairUnitTest";
        private readonly string _prefix = $"ncache:quartz:repair:test:{Guid.NewGuid():N}:";
        private ICache Cache => TestCache.Get();
        private NCacheStorage Storage => new(Cache, _prefix, SchedulerName);
        private NextFireTimeIndex Index => new(Cache, _prefix, SchedulerName);

        [Fact]
        public async Task RebuildRegistries_rebuilds_job_groups()
        {
            var job = CreateJob("p01-job", "p01-group");

            await Storage.StoreJobAsync(job, false);
            Cache.Remove(NCacheKeys.JobGroups(_prefix, SchedulerName));

            await Storage.RebuildRegistriesAsync();

            var groups = await Storage.GetAllJobGroupsAsync();

            Assert.Contains("p01-group", groups);
        }

        [Fact]
        public async Task RebuildRegistries_rebuilds_jobs_in_groups()
        {
            var job = CreateJob("p02-job", "p02-group");

            await Storage.StoreJobAsync(job, false);
            Cache.Remove(NCacheKeys.JobsInGroup(_prefix, SchedulerName, "p02-group"));

            await Storage.RebuildRegistriesAsync();

            var jobs = await Storage.GetJobsInGroupAsync("p02-group");

            var jobKey = Assert.Single(jobs);
            Assert.Equal("p02-job", jobKey.Name);
            Assert.Equal("p02-group", jobKey.Group);
        }

        [Fact]
        public async Task RebuildRegistries_rebuilds_trigger_groups()
        {
            var job = CreateJob("p03-job", "p03-job-group");
            var trigger = CreateTriggerData("p03-trigger", "p03-trigger-group", "p03-job", "p03-job-group");

            await Storage.StoreJobAsync(job, false);
            await Storage.StoreTriggerAsync(trigger, false);
            Cache.Remove(NCacheKeys.TriggerGroups(_prefix, SchedulerName));

            await Storage.RebuildRegistriesAsync();

            var groups = await Storage.GetAllTriggerGroupsAsync();

            Assert.Contains("p03-trigger-group", groups);
        }

        [Fact]
        public async Task RebuildRegistries_rebuilds_triggers_in_groups()
        {
            var job = CreateJob("p04-job", "p04-job-group");
            var trigger = CreateTriggerData("p04-trigger", "p04-trigger-group", "p04-job", "p04-job-group");

            await Storage.StoreJobAsync(job, false);
            await Storage.StoreTriggerAsync(trigger, false);
            Cache.Remove(NCacheKeys.TriggersInGroup(_prefix, SchedulerName, "p04-trigger-group"));

            await Storage.RebuildRegistriesAsync();

            var triggers = await Storage.GetTriggersInGroupAsync("p04-trigger-group");

            var triggerKey = Assert.Single(triggers);
            Assert.Equal("p04-trigger", triggerKey.Name);
            Assert.Equal("p04-trigger-group", triggerKey.Group);
        }

        [Fact]
        public async Task RebuildRegistries_rebuilds_job_trigger_registry()
        {
            var job = CreateJob("p05-job", "p05-job-group");
            var trigger = CreateTriggerData("p05-trigger", "p05-trigger-group", "p05-job", "p05-job-group");

            await Storage.StoreJobAsync(job, false);
            await Storage.StoreTriggerAsync(trigger, false);
            Cache.Remove(NCacheKeys.JobTriggers(_prefix, SchedulerName, "p05-job-group", "p05-job"));

            await Storage.RebuildRegistriesAsync();

            var triggers = await Storage.GetTriggersForJobAsync("p05-job-group", "p05-job");

            var result = Assert.Single(triggers);
            Assert.Equal("p05-trigger", result.TriggerName);
        }

        [Fact]
        public async Task RebuildRegistries_rebuilds_calendar_registry()
        {
            var calendar = CreateCalendar("p06-calendar");

            await Storage.StoreCalendarAsync(calendar, false);
            Cache.Remove(NCacheKeys.CalendarRegistry(_prefix, SchedulerName));

            await Storage.RebuildRegistriesAsync();

            var calendars = await Storage.GetRegisteredCalendarNamesAsync();

            Assert.Contains("p06-calendar", calendars);
        }

        [Fact]
        public async Task RebuildRegistries_rebuilds_instance_fired_registry()
        {
            var fired = CreateFiredTrigger(
                "p07-entry",
                "p07-trigger",
                "p07-group",
                "p07-instance",
                "p07-job",
                "p07-job-group");

            await Storage.StoreFiredTriggerAsync(fired);
            Cache.Remove(NCacheKeys.InstanceFiredTriggers(_prefix, SchedulerName, "p07-instance"));

            await Storage.RebuildRegistriesAsync();

            var firings = await Storage.GetFiredTriggersForInstanceAsync("p07-instance");

            var result = Assert.Single(firings);
            Assert.Equal("p07-entry", result.EntryId);
        }

        [Fact]
        public async Task RebuildRegistries_rebuilds_job_fired_registry()
        {
            var fired = CreateFiredTrigger(
                "p08-entry",
                "p08-trigger",
                "p08-group",
                "p08-instance",
                "p08-job",
                "p08-job-group");
            fired.IsNonConcurrent = true;

            await Storage.StoreFiredTriggerAsync(fired);
            Cache.Remove(NCacheKeys.JobFiredTriggers(_prefix, SchedulerName, "p08-job-group", "p08-job"));

            await Storage.RebuildRegistriesAsync();

            var firings = await Storage.GetFiredTriggersForJobAsync("p08-job-group", "p08-job");

            var result = Assert.Single(firings);
            Assert.Equal("p08-entry", result.EntryId);
        }

        [Fact]
        public async Task RebuildRegistries_removes_a_job_fired_registry_left_for_a_concurrent_job()
        {
            var fired = CreateFiredTrigger(
                "p08b-entry",
                "p08b-trigger",
                "p08b-group",
                "p08b-instance",
                "p08b-job",
                "p08b-job-group");

            await Storage.StoreFiredTriggerAsync(fired);

            var registryKey = NCacheKeys.JobFiredTriggers(_prefix, SchedulerName, "p08b-job-group", "p08b-job");
            Cache.Insert(registryKey, NCacheSerializer.Serialize(new List<string> { "p08b-entry" }));

            await Storage.RebuildRegistriesAsync();

            Assert.False(Cache.Contains(registryKey));
            Assert.Single(await Storage.GetFiredTriggersForInstanceAsync("p08b-instance"));
        }

        [Fact]
        public async Task RebuildRegistries_rebuilds_scheduler_instance_registry()
        {
            var state = CreateSchedulerState("p09-instance");

            await Storage.CheckInAsync(state);
            Cache.Remove(NCacheKeys.SchedulerInstances(_prefix, SchedulerName));

            await Storage.RebuildRegistriesAsync();

            var instances = await Storage.GetRegisteredSchedulerInstanceNamesAsync();

            Assert.Contains("p09-instance", instances);
        }

        [Fact]
        public async Task RebuildRegistries_removes_job_group_registry_when_no_jobs_exist()
        {
            var job = CreateJob("p10-job", "p10-group");

            await Storage.StoreJobAsync(job, false);
            Cache.Remove(NCacheKeys.Job(_prefix, SchedulerName, "p10-group", "p10-job"));

            await Storage.RebuildRegistriesAsync();

            var groups = await Storage.GetAllJobGroupsAsync();

            Assert.DoesNotContain("p10-group", groups);
            Assert.Null(Cache.Get<byte[]>(NCacheKeys.JobsInGroup(_prefix, SchedulerName, "p10-group")));
        }

        [Fact]
        public async Task RebuildRegistries_removes_trigger_group_registry_when_no_triggers_exist()
        {
            var job = CreateJob("p11-job", "p11-job-group");
            var trigger = CreateTriggerData("p11-trigger", "p11-group", "p11-job", "p11-job-group");

            await Storage.StoreJobAsync(job, false);
            await Storage.StoreTriggerAsync(trigger, false);
            Cache.Remove(NCacheKeys.Trigger(_prefix, SchedulerName, "p11-group", "p11-trigger"));

            await Storage.RebuildRegistriesAsync();

            var groups = await Storage.GetAllTriggerGroupsAsync();

            Assert.DoesNotContain("p11-group", groups);
            Assert.Null(Cache.Get<byte[]>(NCacheKeys.TriggersInGroup(_prefix, SchedulerName, "p11-group")));
        }

        [Fact]
        public async Task RebuildRegistries_removes_calendar_registry_when_no_calendars_exist()
        {
            var calendar = CreateCalendar("p12-calendar");

            await Storage.StoreCalendarAsync(calendar, false);
            Cache.Remove(NCacheKeys.Calendar(_prefix, SchedulerName, "p12-calendar"));

            await Storage.RebuildRegistriesAsync();

            var calendars = await Storage.GetRegisteredCalendarNamesAsync();

            Assert.DoesNotContain("p12-calendar", calendars);
        }

        [Fact]
        public async Task RebuildRegistries_removes_scheduler_registry_when_no_scheduler_states_exist()
        {
            var state = CreateSchedulerState("p13-instance");

            await Storage.CheckInAsync(state);
            Cache.Remove(NCacheKeys.SchedulerState(_prefix, SchedulerName, "p13-instance"));

            await Storage.RebuildRegistriesAsync();

            var instances = await Storage.GetRegisteredSchedulerInstanceNamesAsync();

            Assert.DoesNotContain("p13-instance", instances);
        }

        [Fact]
        public async Task RebuildRegistries_ignores_wrong_scheduler_jobs()
        {
            var otherStorage = new NCacheStorage(Cache, _prefix, "OtherScheduler-P14");
            var job = CreateJob("p14-job", "p14-group");
            job.SchedulerName = "OtherScheduler-P14";

            await otherStorage.StoreJobAsync(job, false);
            await Storage.RebuildRegistriesAsync();

            var groups = await Storage.GetAllJobGroupsAsync();

            Assert.DoesNotContain("p14-group", groups);
        }

        [Fact]
        public async Task RebuildRegistries_ignores_wrong_scheduler_triggers()
        {
            var otherStorage = new NCacheStorage(Cache, _prefix, "OtherScheduler-P15");
            var job = CreateJob("p15-job", "p15-job-group");
            var trigger = CreateTriggerData("p15-trigger", "p15-trigger-group", "p15-job", "p15-job-group");
            trigger.SchedulerName = "OtherScheduler-P15";

            await otherStorage.StoreJobAsync(job, false);
            await otherStorage.StoreTriggerAsync(trigger, false);
            await Storage.RebuildRegistriesAsync();

            var groups = await Storage.GetAllTriggerGroupsAsync();

            Assert.DoesNotContain("p15-trigger-group", groups);
        }

        [Fact]
        public async Task RebuildRegistries_ignores_wrong_scheduler_calendars()
        {
            var otherStorage = new NCacheStorage(Cache, _prefix, "OtherScheduler-P16");
            var calendar = CreateCalendar("p16-calendar");
            calendar.SchedulerName = "OtherScheduler-P16";

            await otherStorage.StoreCalendarAsync(calendar, false);
            await Storage.RebuildRegistriesAsync();

            var calendars = await Storage.GetRegisteredCalendarNamesAsync();

            Assert.DoesNotContain("p16-calendar", calendars);
        }

        [Fact]
        public async Task RebuildRegistries_ignores_wrong_scheduler_fired_triggers()
        {
            var otherStorage = new NCacheStorage(Cache, _prefix, "OtherScheduler-P17");
            var fired = CreateFiredTrigger(
                "p17-entry",
                "p17-trigger",
                "p17-group",
                "p17-instance",
                "p17-job",
                "p17-job-group");
            fired.SchedulerName = "OtherScheduler-P17";

            await otherStorage.StoreFiredTriggerAsync(fired);
            await Storage.RebuildRegistriesAsync();

            var firings = await Storage.GetFiredTriggersForInstanceAsync("p17-instance");

            Assert.Empty(firings);
        }

        [Fact]
        public async Task RebuildRegistries_ignores_wrong_scheduler_states()
        {
            var otherStorage = new NCacheStorage(Cache, _prefix, "OtherScheduler-P18");
            var state = CreateSchedulerState("p18-instance");
            state.SchedulerName = "OtherScheduler-P18";

            await otherStorage.CheckInAsync(state);
            await Storage.RebuildRegistriesAsync();

            var instances = await Storage.GetRegisteredSchedulerInstanceNamesAsync();

            Assert.DoesNotContain("p18-instance", instances);
        }

        [Fact]
        public async Task RebuildRegistries_ignores_malformed_job_data()
        {
            var key = NCacheKeys.Job(_prefix, SchedulerName, "p19-group", "p19-job");
            Cache.Insert(key, Encoding.UTF8.GetBytes("not valid json"));

            await Storage.RebuildRegistriesAsync();

            var groups = await Storage.GetAllJobGroupsAsync();

            Assert.DoesNotContain("p19-group", groups);
        }

        [Fact]
        public async Task RebuildRegistries_ignores_malformed_trigger_data()
        {
            var key = NCacheKeys.Trigger(_prefix, SchedulerName, "p20-group", "p20-trigger");
            Cache.Insert(key, Encoding.UTF8.GetBytes("not valid json"));

            await Storage.RebuildRegistriesAsync();

            var groups = await Storage.GetAllTriggerGroupsAsync();

            Assert.DoesNotContain("p20-group", groups);
        }

        [Fact]
        public async Task RebuildRegistries_ignores_malformed_calendar_data()
        {
            var key = NCacheKeys.Calendar(_prefix, SchedulerName, "p21-calendar");
            Cache.Insert(key, Encoding.UTF8.GetBytes("not valid json"));

            await Storage.RebuildRegistriesAsync();

            var calendars = await Storage.GetRegisteredCalendarNamesAsync();

            Assert.DoesNotContain("p21-calendar", calendars);
        }

        [Fact]
        public async Task RebuildRegistries_ignores_malformed_fired_trigger_data()
        {
            var key = NCacheKeys.FiredTrigger(_prefix, SchedulerName, "p22-entry");
            Cache.Insert(key, Encoding.UTF8.GetBytes("not valid json"));

            await Storage.RebuildRegistriesAsync();

            var firings = await Storage.GetFiredTriggersForInstanceAsync("p22-instance");

            Assert.Empty(firings);
        }

        [Fact]
        public async Task RebuildRegistries_ignores_malformed_scheduler_state_data()
        {
            var key = NCacheKeys.SchedulerState(_prefix, SchedulerName, "p23-instance");
            Cache.Insert(key, Encoding.UTF8.GetBytes("not valid json"));

            await Storage.RebuildRegistriesAsync();

            var instances = await Storage.GetRegisteredSchedulerInstanceNamesAsync();

            Assert.DoesNotContain("p23-instance", instances);
        }

        [Fact]
        public async Task RebuildRegistries_ignores_job_headers()
        {
            var job = CreateJob("p24-job", "p24-group");

            await Storage.StoreJobAsync(job, false);
            Cache.Remove(NCacheKeys.Job(_prefix, SchedulerName, "p24-group", "p24-job"));

            await Storage.RebuildRegistriesAsync();

            var groups = await Storage.GetAllJobGroupsAsync();

            Assert.DoesNotContain("p24-group", groups);
        }

        [Fact]
        public async Task RebuildRegistries_ignores_job_trigger_registry_keys()
        {
            var job = CreateJob("p25-job", "p25-job-group");
            var trigger = CreateTriggerData("p25-trigger", "p25-trigger-group", "p25-job", "p25-job-group");

            await Storage.StoreJobAsync(job, false);
            await Storage.StoreTriggerAsync(trigger, false);
            Cache.Remove(NCacheKeys.Trigger(_prefix, SchedulerName, "p25-trigger-group", "p25-trigger"));

            await Storage.RebuildRegistriesAsync();

            var triggers = await Storage.GetTriggersForJobAsync("p25-job-group", "p25-job");

            Assert.Empty(triggers);
        }

        [Fact]
        public async Task RebuildRegistries_ignores_job_fired_registry_keys()
        {
            var fired = CreateFiredTrigger(
                "p26-entry",
                "p26-trigger",
                "p26-group",
                "p26-instance",
                "p26-job",
                "p26-job-group");

            await Storage.StoreFiredTriggerAsync(fired);
            Cache.Remove(NCacheKeys.FiredTrigger(_prefix, SchedulerName, "p26-entry"));

            await Storage.RebuildRegistriesAsync();

            var firings = await Storage.GetFiredTriggersForJobAsync("p26-job-group", "p26-job");

            Assert.Empty(firings);
        }

        [Fact]
        public async Task RebuildRegistries_ignores_trigger_registry_keys()
        {
            var job = CreateJob("p27-job", "p27-job-group");
            var trigger = CreateTriggerData("p27-trigger", "p27-trigger-group", "p27-job", "p27-job-group");

            await Storage.StoreJobAsync(job, false);
            await Storage.StoreTriggerAsync(trigger, false);
            Cache.Remove(NCacheKeys.Trigger(_prefix, SchedulerName, "p27-trigger-group", "p27-trigger"));

            await Storage.RebuildRegistriesAsync();

            var groups = await Storage.GetAllTriggerGroupsAsync();

            Assert.DoesNotContain("p27-trigger-group", groups);
        }

        [Fact]
        public async Task RebuildRegistries_deduplicates_job_names()
        {
            var job = CreateJob("p28-job", "p28-group");

            await Storage.StoreJobAsync(job, false);

            var key = NCacheKeys.JobsInGroup(_prefix, SchedulerName, "p28-group");
            Cache.Insert(key, JsonBytes(new List<string> { "p28-job", "p28-job" }));

            await Storage.RebuildRegistriesAsync();

            var jobs = await Storage.GetJobsInGroupAsync("p28-group");

            Assert.Single(jobs);
        }

        [Fact]
        public async Task RebuildRegistries_deduplicates_trigger_names()
        {
            var job = CreateJob("p29-job", "p29-job-group");
            var trigger = CreateTriggerData("p29-trigger", "p29-group", "p29-job", "p29-job-group");

            await Storage.StoreJobAsync(job, false);
            await Storage.StoreTriggerAsync(trigger, false);

            var key = NCacheKeys.TriggersInGroup(_prefix, SchedulerName, "p29-group");
            Cache.Insert(key, JsonBytes(new List<string> { "p29-trigger", "p29-trigger" }));

            await Storage.RebuildRegistriesAsync();

            var triggers = await Storage.GetTriggersInGroupAsync("p29-group");

            Assert.Single(triggers);
        }

        [Fact]
        public async Task RebuildRegistries_deduplicates_calendar_names()
        {
            var calendar = CreateCalendar("p30-calendar");

            await Storage.StoreCalendarAsync(calendar, false);

            var key = NCacheKeys.CalendarRegistry(_prefix, SchedulerName);
            Cache.Insert(key, JsonBytes(new List<string> { "p30-calendar", "p30-calendar" }));

            await Storage.RebuildRegistriesAsync();

            var calendars = await Storage.GetRegisteredCalendarNamesAsync();

            Assert.Single(calendars);
        }

        [Fact]
        public async Task RebuildRegistries_deduplicates_fired_entry_ids()
        {
            var fired = CreateFiredTrigger(
                "p31-entry",
                "p31-trigger",
                "p31-group",
                "p31-instance",
                "p31-job",
                "p31-job-group");

            await Storage.StoreFiredTriggerAsync(fired);

            var key = NCacheKeys.InstanceFiredTriggers(_prefix, SchedulerName, "p31-instance");
            Cache.Insert(key, JsonBytes(new List<string> { "p31-entry", "p31-entry" }));

            await Storage.RebuildRegistriesAsync();

            var firings = await Storage.GetFiredTriggersForInstanceAsync("p31-instance");

            Assert.Single(firings);
        }

        [Fact]
        public async Task RebuildRegistries_deduplicates_scheduler_instances()
        {
            var state = CreateSchedulerState("p32-instance");

            await Storage.CheckInAsync(state);

            var key = NCacheKeys.SchedulerInstances(_prefix, SchedulerName);
            Cache.Insert(key, JsonBytes(new List<string> { "p32-instance", "p32-instance" }));

            await Storage.RebuildRegistriesAsync();

            var instances = await Storage.GetRegisteredSchedulerInstanceNamesAsync();

            Assert.Single(instances);
        }

        [Fact]
        public async Task RebuildRegistries_rebuilds_only_records_found_in_cache()
        {
            var job = CreateJob("p33-job", "p33-group");

            await Storage.StoreJobAsync(job, false);
            Cache.Remove(NCacheKeys.Job(_prefix, SchedulerName, "p33-group", "p33-job"));
            Cache.Remove(NCacheKeys.JobGroups(_prefix, SchedulerName));
            Cache.Remove(NCacheKeys.JobsInGroup(_prefix, SchedulerName, "p33-group"));

            await Storage.RebuildRegistriesAsync();

            Assert.Empty(await Storage.GetAllJobGroupsAsync());
        }

        [Fact]
        public async Task RebuildRegistries_honors_cancellation()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Storage.RebuildRegistriesAsync(cts.Token));
        }

        [Fact]
        public async Task Repair_after_partial_failure_rebuilds_registries()
        {
            var store = CreateJobStore("p35-instance");
            var job = CreateQuartzJob("p35-job", "p35-group");

            Cache.Insert(
                NCacheKeys.JobsInGroup(_prefix, SchedulerName, "p35-group"),
                Encoding.UTF8.GetBytes("corrupt registry"));

            var exception = await Assert.ThrowsAsync<JsonException>(async () => await store.AddJob(job));

            var groups = await Storage.GetAllJobGroupsAsync();

            Assert.DoesNotContain("p35-group", groups);
            Assert.Null(await Storage.GetJobAsync("p35-group", "p35-job"));
        }
        [Fact]
        public async Task Repair_after_partial_failure_rebuilds_next_fire_time_index()
        {
            var store = CreateJobStore("p36-instance");
            var job = CreateQuartzJob("p36-job", "p36-job-group");

            var existing = CreateQuartzTrigger(
                "p36-existing",
                "p36-trigger-group",
                "p36-job",
                "p36-job-group");

            await store.AddJob(job);
            await store.AddTrigger(existing);

            var storedTrigger = await Storage.GetTriggerAsync("p36-trigger-group", "p36-existing");

            Assert.NotNull(storedTrigger);
            Assert.Equal(TriggerStates.Waiting, storedTrigger!.TriggerState);
            Assert.NotNull(storedTrigger.NextFireTimeUtc);

            var triggerGroupsBeforeCorruption = await Storage.GetAllTriggerGroupsAsync();

            Assert.Contains("p36-trigger-group", triggerGroupsBeforeCorruption);

            var triggerKeysBeforeCorruption = await Storage.GetTriggersInGroupAsync("p36-trigger-group");

            Assert.Contains(
                triggerKeysBeforeCorruption,
                x => x.Group == "p36-trigger-group" &&
                     x.Name == "p36-existing");

            var beforeCorruption = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddHours(1), 20);

            Assert.Contains(
                beforeCorruption,
                x => x.TriggerGroup == "p36-trigger-group" &&
                     x.TriggerName == "p36-existing");

            Cache.Insert(
                NCacheKeys.NextFireTimeActiveBuckets(_prefix, SchedulerName),
                Encoding.UTF8.GetBytes("corrupt index"));

            await Assert.ThrowsAsync<ObjectAlreadyExistsException>(async () => await store.AddJob(job));
            await Assert.ThrowsAsync<JsonException>(async () => await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddHours(1), 20));

            var doomed = CreateQuartzTrigger("p36-doomed", "p36-trigger-group", "p36-job", "p36-job-group");

            await Assert.ThrowsAsync<JsonException>(async () => await store.AddTrigger(doomed));

            Assert.Null(await Storage.GetTriggerAsync("p36-trigger-group", "p36-doomed"));

            var triggerGroupsAfterRepair = await Storage.GetAllTriggerGroupsAsync();

            Assert.Contains("p36-trigger-group", triggerGroupsAfterRepair);

            var triggerKeysAfterRepair = await Storage.GetTriggersInGroupAsync("p36-trigger-group");

            Assert.Contains(
                triggerKeysAfterRepair,
                x => x.Group == "p36-trigger-group" &&
                     x.Name == "p36-existing");

            var repairedTrigger = await Storage.GetTriggerAsync("p36-trigger-group", "p36-existing");

            Assert.NotNull(repairedTrigger);
            Assert.Equal(TriggerStates.Waiting, repairedTrigger!.TriggerState);
            Assert.NotNull(repairedTrigger.NextFireTimeUtc);

            var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddHours(1), 20);

            Assert.Contains(
                candidates,
                x => x.TriggerGroup == "p36-trigger-group" &&
                     x.TriggerName == "p36-existing");
        }
        [Fact]
        public async Task Repair_after_partial_failure_rebuilds_a_corrupt_index_bucket()
        {
            var store = CreateJobStore("p36b-instance");
            var job = CreateQuartzJob("p36b-job", "p36b-job-group");
            var existing = CreateQuartzTrigger("p36b-existing", "p36b-trigger-group", "p36b-job", "p36b-job-group");

            await store.AddJob(job);
            await store.AddTrigger(existing);

            var storedTrigger = await Storage.GetTriggerAsync("p36b-trigger-group", "p36b-existing");
            Assert.NotNull(storedTrigger);
            Assert.NotNull(storedTrigger!.IndexBucketId);

            Cache.Insert(
                NCacheKeys.NextFireTimeIndexBucket(_prefix, SchedulerName, storedTrigger.IndexBucketId!.Value),
                Encoding.UTF8.GetBytes("corrupt bucket"));

            await Assert.ThrowsAsync<JsonException>(async () => await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddHours(1), 20));

            var doomed = (IOperableTrigger)TriggerBuilder.Create()
                .WithIdentity("p36b-doomed", "p36b-trigger-group")
                .ForJob(new JobKey("p36b-job", "p36b-job-group"))
                .StartAt(storedTrigger.NextFireTimeUtc!.Value)
                .WithSimpleSchedule(schedule => schedule.WithInterval(TimeSpan.FromMinutes(1)).RepeatForever())
                .Build();
            doomed.ComputeFirstFireTimeUtc(null);

            await Assert.ThrowsAsync<JsonException>(async () => await store.AddTrigger(doomed));

            Assert.Null(await Storage.GetTriggerAsync("p36b-trigger-group", "p36b-doomed"));

            var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddHours(1), 20);

            Assert.Contains(candidates, x => x.TriggerGroup == "p36b-trigger-group" && x.TriggerName == "p36b-existing");
            Assert.DoesNotContain(candidates, x => x.TriggerName == "p36b-doomed");

            var repaired = await Storage.GetTriggerAsync("p36b-trigger-group", "p36b-existing");
            Assert.NotNull(repaired);
            Assert.Equal(repaired!.IndexBucketId, Assert.Single(candidates, x => x.TriggerName == "p36b-existing").BucketId);
        }

        [Fact]
        public async Task Repair_after_partial_failure_does_not_mask_original_exception()
        {
            var store = CreateJobStore("p37-instance");
            var job = CreateQuartzJob("p37-job", "p37-group");

            await store.AddJob(job);

            var exception = await Assert.ThrowsAsync<ObjectAlreadyExistsException>(
                async () => await store.AddJob(job));

            Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Repair_after_registry_rebuild_failure_continues_to_index_rebuild()
        {
            var store = CreateJobStore("p38-instance");
            var job = CreateQuartzJob("p38-job", "p38-job-group");
            var existing = CreateQuartzTrigger("p38-existing", "p38-trigger-group", "p38-job", "p38-job-group");

            await store.AddJob(job);
            await store.AddTrigger(existing);

            Cache.Insert(NCacheKeys.JobGroups(_prefix, SchedulerName), Encoding.UTF8.GetBytes("corrupt job groups"));
            Cache.Insert(NCacheKeys.NextFireTimeActiveBuckets(_prefix, SchedulerName), Encoding.UTF8.GetBytes("corrupt index"));

            var doomed = CreateQuartzTrigger("p38-doomed", "p38-trigger-group", "p38-job", "p38-job-group");
            await Assert.ThrowsAsync<JsonException>(async () => await store.AddTrigger(doomed));

            await Assert.ThrowsAsync<JsonException>(async () => await Storage.GetAllJobGroupsAsync());

            var candidates = await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddHours(1), 20);
            Assert.Contains(candidates, x => x.TriggerGroup == "p38-trigger-group" && x.TriggerName == "p38-existing");
            Assert.DoesNotContain(candidates, x => x.TriggerName == "p38-doomed");
        }

        [Fact]
        public async Task Repair_after_index_rebuild_failure_does_not_mask_original_exception()
        {
            var store = CreateJobStore("p39-instance");
            var job = CreateQuartzJob("p39-job", "p39-group");

            Cache.Insert(NCacheKeys.JobsInGroup(_prefix, SchedulerName, "p39-group"), new byte[] { 1, 2, 3, 4 });

            Cache.Insert(NCacheKeys.TriggerGroups(_prefix, SchedulerName), Encoding.UTF8.GetBytes("corrupt trigger groups"));

            var exception = await Assert.ThrowsAsync<JsonException>(async () => await store.AddJob(job));

            Assert.DoesNotContain("'c'", exception.Message);
            Assert.Null(await Storage.GetJobAsync("p39-group", "p39-job"));
        }

        [Fact]
        public async Task Clear_rebuilds_next_fire_time_index()
        {
            var store = CreateJobStore("p40-instance");
            var job = CreateQuartzJob("p40-job", "p40-job-group");
            var trigger = CreateQuartzTrigger(
                "p40-trigger",
                "p40-trigger-group",
                "p40-job",
                "p40-job-group");

            await store.AddJob(job);
            await store.AddTrigger(trigger);

            var before = await Index.GetDueCandidatesAsync(
                DateTimeOffset.UtcNow.AddHours(1),
                20);

            Assert.Contains(
                before,
                candidate => candidate.TriggerGroup == "p40-trigger-group" &&
                             candidate.TriggerName == "p40-trigger");

            await store.Clear();

            var after = await Index.GetDueCandidatesAsync(
                DateTimeOffset.UtcNow.AddHours(1),
                20);

            Assert.Empty(after);
            Assert.Null(await Storage.GetJobAsync("p40-job-group", "p40-job"));
            Assert.Null(await Storage.GetTriggerAsync("p40-trigger-group", "p40-trigger"));
        }

        [Fact]
        public async Task SchedulerStarted_repairs_registries_before_normal_operation()
        {
            var job = CreateJob("p41-job", "p41-group");

            await Storage.StoreJobAsync(job, false);

            Cache.Remove(NCacheKeys.JobGroups(_prefix, SchedulerName));
            Cache.Remove(NCacheKeys.JobsInGroup(_prefix, SchedulerName, "p41-group"));

            var store = CreateJobStore("p41-instance");

            try
            {
                await store.Initialize(new SchedulerIdentity
                {
                    SchedulerName = SchedulerName,
                    InstanceId = "p41-instance"
                });

                await store.SchedulerStarted();

                var groups = await Storage.GetAllJobGroupsAsync();
                var jobs = await Storage.GetJobsInGroupAsync("p41-group");

                Assert.Contains("p41-group", groups);

                var jobKey = Assert.Single(jobs);
                Assert.Equal("p41-job", jobKey.Name);
            }
            finally
            {
                await store.Shutdown();
            }
        }

        [Fact]
        public async Task SchedulerStarted_repairs_stale_index_entries()
        {
            var job = CreateJob("p42-job", "p42-job-group");
            var trigger = CreateTriggerData(
                "p42-trigger",
                "p42-trigger-group",
                "p42-job",
                "p42-job-group");

            trigger.NextFireTimeUtc = DateTimeOffset.UtcNow.AddMinutes(1);
            trigger.TriggerState = TriggerStates.Waiting;

            await Storage.StoreJobAsync(job, false);
            await Storage.StoreTriggerAsync(trigger, false);
            await Index.UpsertAsync(trigger);

            Cache.Remove(
                NCacheKeys.Trigger(
                    _prefix,
                    SchedulerName,
                    "p42-trigger-group",
                    "p42-trigger"));

            var store = CreateJobStore("p42-instance");

            try
            {
                await store.Initialize(new SchedulerIdentity
                {
                    SchedulerName = SchedulerName,
                    InstanceId = "p42-instance"
                });

                await store.SchedulerStarted();

                var candidates = await Index.GetDueCandidatesAsync(
                    DateTimeOffset.UtcNow.AddHours(1),
                    20);

                Assert.DoesNotContain(
                    candidates,
                    candidate => candidate.TriggerGroup == "p42-trigger-group" &&
                                 candidate.TriggerName == "p42-trigger");
            }
            finally
            {
                await store.Shutdown();
            }
        }

        [Fact]
        public async Task Recovery_repairs_registry_state_after_stale_scheduler_cleanup()
        {
            var store = CreateJobStore("p43-current");
            var staleInstance = CreateSchedulerState("p43-stale");
            staleInstance.LastCheckinTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
            staleInstance.CheckinInterval = TimeSpan.FromSeconds(10);

            var fired = CreateFiredTrigger(
                "p43-entry",
                "p43-trigger",
                "p43-trigger-group",
                "p43-stale",
                "p43-job",
                "p43-job-group");

            await Storage.CheckInAsync(staleInstance);
            await Storage.StoreFiredTriggerAsync(fired);

            var recovered = await store.RecoverStaleSchedulerInstancesAsync();

            Assert.Equal(0, recovered);
            Assert.Null(await Storage.GetSchedulerStateAsync("p43-stale"));
            Assert.Empty(await Storage.GetFiredTriggersForInstanceAsync("p43-stale"));
            Assert.DoesNotContain(
                "p43-stale",
                await Storage.GetRegisteredSchedulerInstanceNamesAsync());
        }
        private NCacheJobStore CreateJobStore(string instanceId)
        {
            var store = new NCacheJobStore(Storage, Index, new NCacheDistributedLocks(Cache, _prefix, SchedulerName));

            store.Initialize(new SchedulerIdentity { SchedulerName = SchedulerName, InstanceId = instanceId })
                .GetAwaiter()
                .GetResult();

            return store;
        }

        [Fact]
        public async Task JobStore_refuses_to_operate_before_Initialize()
        {
            var store = new NCacheJobStore(Storage, Index, new NCacheDistributedLocks(Cache, _prefix, SchedulerName));

            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.AddJob(CreateQuartzJob("p44-job", "p44-group")));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SchedulerStarted());
        }

        private NCacheJobData CreateJob(string name, string group)
        {
            return new NCacheJobData
            {
                SchedulerName = SchedulerName,
                JobName = name,
                JobGroup = group,
                JobClassName = typeof(TestJob).AssemblyQualifiedName!
            };
        }

        private static NCacheTriggerData CreateTriggerData(string triggerName, string triggerGroup, string jobName, string jobGroup)
        {
            return new NCacheTriggerData
            {
                SchedulerName = SchedulerName,
                TriggerName = triggerName,
                TriggerGroup = triggerGroup,
                JobName = jobName,
                JobGroup = jobGroup,
                TriggerState = TriggerStates.Waiting,
                TriggerKind = NCacheTriggerKind.Simple,
                StartTimeUtc = DateTimeOffset.UtcNow,
                NextFireTimeUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                Priority = 5,
                Simple = new NCacheSimpleTriggerData
                {
                    RepeatCount = -1,
                    RepeatInterval = TimeSpan.FromMinutes(1),
                    TimesTriggered = 0
                }
            };
        }

        private static NCacheCalendarData CreateCalendar(string calendarName)
        {
            return new NCacheCalendarData
            {
                SchedulerName = SchedulerName,
                CalendarName = calendarName,
                CalendarJson = "{}"
            };
        }

        private static NCacheFiredTriggerData CreateFiredTrigger(string entryId, string triggerName, string triggerGroup, string instanceName, string jobName, string jobGroup)
        {
            return new NCacheFiredTriggerData
            {
                SchedulerName = SchedulerName,
                EntryId = entryId,
                TriggerName = triggerName,
                TriggerGroup = triggerGroup,
                InstanceName = instanceName,
                FiredTimeUtc = DateTimeOffset.UtcNow,
                ScheduledTimeUtc = DateTimeOffset.UtcNow,
                Priority = 5,
                State = FiredTriggerStates.Executing,
                JobName = jobName,
                JobGroup = jobGroup,
                IsNonConcurrent = false,
                RequestsRecovery = false
            };
        }

        private static NCacheSchedulerState CreateSchedulerState(string instanceName)
        {
            return new NCacheSchedulerState
            {
                SchedulerName = SchedulerName,
                InstanceName = instanceName,
                LastCheckinTimeUtc = DateTimeOffset.UtcNow,
                CheckinInterval = TimeSpan.FromSeconds(10)
            };
        }

        private static IJobDetail CreateQuartzJob(string name, string group)
        {
            return JobBuilder.Create<TestJob>()
                .WithIdentity(name, group)
                .Build();
        }

        private static IOperableTrigger CreateQuartzTrigger(string name,string group,string jobName,string jobGroup)
        {
            var trigger = TriggerBuilder.Create()
                .WithIdentity(name, group)
                .ForJob(new JobKey(jobName, jobGroup))
                .StartNow()
                .WithSimpleSchedule(schedule =>
                    schedule.WithInterval(TimeSpan.FromMinutes(1))
                             .RepeatForever())
                .Build() as IOperableTrigger
                ?? throw new InvalidOperationException("Could not create operable trigger.");

            trigger.ComputeFirstFireTimeUtc(null);

            return trigger;
        }

        private static byte[] JsonBytes<T>(T value)
        {
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        }

        private sealed class TestJob : IJob
        {
            public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}