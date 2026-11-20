using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Alachisoft.NCache.Client;
using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Model;
using NCache.OSS.Quartz.Serialization;
using NCache.OSS.Quartz.Storage;
using Quartz;
using Xunit;

namespace NCache.OSS.Quartz.Tests
{
    public sealed class NCacheQuartzStorageUnitTest : IAsyncLifetime
    {
        private const string SchedulerName = "NCacheQuartzStorageUnitTest";
        private readonly string _prefix = $"ncache:quartz:storage:test:{Guid.NewGuid():N}:";
        private ICache? _cache;
        private NCacheStorage Storage => new(_cache!, _prefix, SchedulerName);

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

        private NCacheJobData CreateJob(string group, string name)
        {
            return new NCacheJobData
            {
                SchedulerName = SchedulerName,
                JobName = name,
                JobGroup = group,
                Description = $"Description-{name}",
                JobClassName = typeof(TestJob).AssemblyQualifiedName!,
                IsDurable = true,
                IsNonConcurrent = false,
                IsUpdateData = false,
                RequestsRecovery = false,
                JobDataMap = new Dictionary<string, object?>
                {
                    ["Key"] = "Value"
                }
            };
        }

        private NCacheTriggerData CreateTrigger(string group, string name, string jobGroup = "job-group", string jobName = "job", string? calendarName = null)
        {
            return new NCacheTriggerData
            {
                SchedulerName = SchedulerName,
                TriggerName = name,
                TriggerGroup = group,
                JobName = jobName,
                JobGroup = jobGroup,
                Description = $"Description-{name}",
                NextFireTimeUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                PreviousFireTimeUtc = null,
                Priority = 5,
                TriggerState = TriggerStates.Waiting,
                TriggerKind = NCacheTriggerKind.Simple,
                StartTimeUtc = DateTimeOffset.UtcNow,
                EndTimeUtc = null,
                CalendarName = calendarName,
                MisfireInstruction = 0,
                JobDataMap = new Dictionary<string, object?>
                {
                    ["TriggerKey"] = name
                },
                Simple = new NCacheSimpleTriggerData
                {
                    RepeatCount = 1,
                    RepeatInterval = TimeSpan.FromSeconds(30),
                    TimesTriggered = 0
                }
            };
        }

        private NCacheFiredTriggerData CreateFiredTrigger(string entryId, string instanceName, string triggerGroup = "trigger-group", string triggerName = "trigger", string? jobGroup = "job-group", string? jobName = "job", bool nonConcurrent = false)
        {
            return new NCacheFiredTriggerData
            {
                SchedulerName = SchedulerName,
                EntryId = entryId,
                TriggerName = triggerName,
                TriggerGroup = triggerGroup,
                InstanceName = instanceName,
                FiredTimeUtc = DateTimeOffset.UtcNow,
                ScheduledTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                Priority = 5,
                State = FiredTriggerStates.Acquired,
                JobName = jobName,
                JobGroup = jobGroup,
                IsNonConcurrent = nonConcurrent,
                RequestsRecovery = false
            };
        }

        private NCacheJobHeaderData CreateJobHeader(string group, string name)
        {
            return new NCacheJobHeaderData
            {
                SchedulerName = SchedulerName,
                JobName = name,
                JobGroup = group,
                Description = $"Header-{name}",
                JobClassName = typeof(TestJob).AssemblyQualifiedName!,
                IsDurable = true,
                IsNonConcurrent = false,
                IsUpdateData = false,
                RequestsRecovery = false
            };
        }

        private void Corrupt(string key)
        {
            _cache!.Insert(key, new byte[] { 1, 2, 3, 4 });
        }

        [Fact]
        public async Task GetJobAsync_returns_null_when_missing()
        {
            var result = await Storage.GetJobAsync("l01", "missing");
            Assert.Null(result);
        }

        [Fact]
        public async Task StoreJobAsync_stores_job()
        {
            var job = CreateJob("l02", "job");
            await Storage.StoreJobAsync(job, false);
            var result = await Storage.GetJobAsync(job.JobGroup, job.JobName);
            Assert.NotNull(result);
            Assert.Equal(job.JobName, result!.JobName);
            Assert.Equal(job.JobGroup, result.JobGroup);
        }

        [Fact]
        public async Task StoreJobAsync_rejects_duplicate()
        {
            var job = CreateJob("l03", "job");
            await Storage.StoreJobAsync(job, false);
            await Assert.ThrowsAsync<ObjectAlreadyExistsException>(() => Storage.StoreJobAsync(job, false));
        }

        [Fact]
        public async Task StoreJobAsync_replaces_existing_job()
        {
            var first = CreateJob("l04", "job");
            first.Description = "First";
            await Storage.StoreJobAsync(first, false);
            var second = CreateJob("l04", "job");
            second.Description = "Second";
            await Storage.StoreJobAsync(second, true);
            var result = await Storage.GetJobAsync("l04", "job");
            Assert.NotNull(result);
            Assert.Equal("Second", result!.Description);
        }

        [Fact]
        public async Task RemoveJobAsync_removes_existing_job()
        {
            var job = CreateJob("l05", "job");
            await Storage.StoreJobAsync(job, false);
            var removed = await Storage.RemoveJobAsync(job.JobGroup, job.JobName);
            Assert.True(removed);
            Assert.Null(await Storage.GetJobAsync(job.JobGroup, job.JobName));
        }

        [Fact]
        public async Task RemoveJobAsync_returns_false_when_missing()
        {
            var removed = await Storage.RemoveJobAsync("l06", "missing");
            Assert.False(removed);
        }

        [Fact]
        public async Task JobExistsAsync_returns_true_when_present()
        {
            var job = CreateJob("l07", "job");
            await Storage.StoreJobAsync(job, false);
            Assert.True(await Storage.JobExistsAsync(job.JobGroup, job.JobName));
        }

        [Fact]
        public async Task JobExistsAsync_returns_false_when_missing()
        {
            Assert.False(await Storage.JobExistsAsync("l08", "missing"));
        }

        [Fact]
        public async Task StoreJobAsync_creates_job_header()
        {
            var job = CreateJob("l09", "job");
            await Storage.StoreJobAsync(job, false);
            var header = await Storage.GetJobHeaderAsync(job.JobGroup, job.JobName);
            Assert.NotNull(header);
            Assert.Equal(job.JobName, header!.JobName);
            Assert.Equal(job.JobGroup, header.JobGroup);
        }

        [Fact]
        public async Task RemoveJobAsync_removes_job_header()
        {
            var job = CreateJob("l10", "job");
            await Storage.StoreJobAsync(job, false);
            await Storage.RemoveJobAsync(job.JobGroup, job.JobName);
            Assert.Null(await Storage.GetJobHeaderAsync(job.JobGroup, job.JobName));
        }

        [Fact]
        public async Task StoreJobAsync_adds_job_to_group_registry()
        {
            var job = CreateJob("l11", "job");
            await Storage.StoreJobAsync(job, false);
            var jobs = await Storage.GetJobsInGroupAsync(job.JobGroup);
            Assert.Contains(new JobKey(job.JobName, job.JobGroup), jobs);
        }

        [Fact]
        public async Task RemoveJobAsync_removes_job_from_group_registry()
        {
            var job = CreateJob("l12", "job");
            await Storage.StoreJobAsync(job, false);
            await Storage.RemoveJobAsync(job.JobGroup, job.JobName);
            var jobs = await Storage.GetJobsInGroupAsync(job.JobGroup);
            Assert.Empty(jobs);
        }

        [Fact]
        public async Task RemoveJobAsync_removes_empty_job_group_registry()
        {
            var job = CreateJob("l13", "job");
            await Storage.StoreJobAsync(job, false);
            await Storage.RemoveJobAsync(job.JobGroup, job.JobName);
            var groups = await Storage.GetAllJobGroupsAsync();
            Assert.DoesNotContain(job.JobGroup, groups);
        }

        [Fact]
        public async Task GetJobsInGroupAsync_excludes_missing_jobs()
        {
            var job = CreateJob("l14", "job");
            await Storage.StoreJobAsync(job, false);
            _cache!.Remove<byte[]>(NCacheKeys.Job(_prefix, SchedulerName, job.JobGroup, job.JobName), out _);
            var jobs = await Storage.GetJobsInGroupAsync(job.JobGroup);
            Assert.Empty(jobs);
        }

        [Fact]
        public async Task GetAllJobGroupsAsync_returns_registered_groups()
        {
            var first = CreateJob("l15-a", "job1");
            var second = CreateJob("l15-b", "job2");
            await Storage.StoreJobAsync(first, false);
            await Storage.StoreJobAsync(second, false);
            var groups = await Storage.GetAllJobGroupsAsync();
            Assert.Contains("l15-a", groups);
            Assert.Contains("l15-b", groups);
        }

        [Fact]
        public async Task GetTriggerAsync_returns_null_when_missing()
        {
            var result = await Storage.GetTriggerAsync("l16", "missing");
            Assert.Null(result);
        }

        [Fact]
        public async Task StoreTriggerAsync_stores_trigger()
        {
            var trigger = CreateTrigger("l17", "trigger");
            await Storage.StoreTriggerAsync(trigger, false);
            var result = await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
            Assert.NotNull(result);
            Assert.Equal(trigger.TriggerName, result!.TriggerName);
            Assert.Equal(trigger.TriggerGroup, result.TriggerGroup);
        }

        [Fact]
        public async Task StoreTriggerAsync_rejects_duplicate()
        {
            var trigger = CreateTrigger("l18", "trigger");
            await Storage.StoreTriggerAsync(trigger, false);
            await Assert.ThrowsAsync<ObjectAlreadyExistsException>(() => Storage.StoreTriggerAsync(trigger, false));
        }

        [Fact]
        public async Task StoreTriggerAsync_replaces_existing_trigger()
        {
            var first = CreateTrigger("l19", "trigger");
            first.Description = "First";
            await Storage.StoreTriggerAsync(first, false);
            var second = CreateTrigger("l19", "trigger");
            second.Description = "Second";
            await Storage.StoreTriggerAsync(second, true);
            var result = await Storage.GetTriggerAsync("l19", "trigger");
            Assert.NotNull(result);
            Assert.Equal("Second", result!.Description);
        }

        [Fact]
        public async Task RemoveTriggerAsync_removes_existing_trigger()
        {
            var trigger = CreateTrigger("l20", "trigger");
            await Storage.StoreTriggerAsync(trigger, false);
            var removed = await Storage.RemoveTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
            Assert.True(removed);
            Assert.Null(await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName));
        }

        [Fact]
        public async Task RemoveTriggerAsync_returns_false_when_missing()
        {
            var removed = await Storage.RemoveTriggerAsync("l21", "missing");
            Assert.False(removed);
        }

        [Fact]
        public async Task TriggerExistsAsync_returns_true_when_present()
        {
            var trigger = CreateTrigger("l22", "trigger");
            await Storage.StoreTriggerAsync(trigger, false);
            Assert.True(await Storage.TriggerExistsAsync(trigger.TriggerGroup, trigger.TriggerName));
        }

        [Fact]
        public async Task TriggerExistsAsync_returns_false_when_missing()
        {
            Assert.False(await Storage.TriggerExistsAsync("l23", "missing"));
        }

        [Fact]
        public async Task StoreTriggerAsync_registers_trigger_for_job()
        {
            var trigger = CreateTrigger("l24", "trigger");
            await Storage.StoreTriggerAsync(trigger, false);
            var triggers = await Storage.GetTriggersForJobAsync(trigger.JobGroup, trigger.JobName);
            Assert.Single(triggers);
            Assert.Equal(trigger.TriggerName, triggers[0].TriggerName);
        }

        [Fact]
        public async Task RemoveTriggerAsync_removes_trigger_from_job_registry()
        {
            var trigger = CreateTrigger("l25", "trigger");
            await Storage.StoreTriggerAsync(trigger, false);
            await Storage.RemoveTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
            var triggers = await Storage.GetTriggersForJobAsync(trigger.JobGroup, trigger.JobName);
            Assert.Empty(triggers);
        }

        [Fact]
        public async Task StoreTriggerAsync_registers_trigger_in_trigger_group()
        {
            var trigger = CreateTrigger("l26", "trigger");
            await Storage.StoreTriggerAsync(trigger, false);
            var triggers = await Storage.GetTriggersInGroupAsync(trigger.TriggerGroup);
            Assert.Single(triggers);
            Assert.Equal(trigger.TriggerName, triggers[0].Name);
        }

        [Fact]
        public async Task RemoveTriggerAsync_removes_trigger_from_trigger_group()
        {
            var trigger = CreateTrigger("l27", "trigger");
            await Storage.StoreTriggerAsync(trigger, false);
            await Storage.RemoveTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
            var triggers = await Storage.GetTriggersInGroupAsync(trigger.TriggerGroup);
            Assert.Empty(triggers);
        }

        [Fact]
        public async Task RemoveTriggerAsync_removes_empty_trigger_group_registry()
        {
            var trigger = CreateTrigger("l28", "trigger");
            await Storage.StoreTriggerAsync(trigger, false);
            await Storage.RemoveTriggerAsync(trigger.TriggerGroup, trigger.TriggerName);
            var groups = await Storage.GetAllTriggerGroupsAsync();
            Assert.DoesNotContain(trigger.TriggerGroup, groups);
        }

        [Fact]
        public async Task GetTriggersForJobAsync_returns_registered_triggers()
        {
            var first = CreateTrigger("l29", "first");
            var second = CreateTrigger("l29", "second");
            await Storage.StoreTriggerAsync(first, false);
            await Storage.StoreTriggerAsync(second, false);
            var triggers = await Storage.GetTriggersForJobAsync(first.JobGroup, first.JobName);
            Assert.Equal(2, triggers.Count);
        }

        [Fact]
        public async Task GetTriggersForJobAsync_skips_missing_triggers()
        {
            var trigger = CreateTrigger("l30", "trigger");
            await Storage.StoreTriggerAsync(trigger, false);
            _cache!.Remove<byte[]>(NCacheKeys.Trigger(_prefix, SchedulerName, trigger.TriggerGroup, trigger.TriggerName), out _);
            var triggers = await Storage.GetTriggersForJobAsync(trigger.JobGroup, trigger.JobName);
            Assert.Empty(triggers);
        }

        [Fact]
        public async Task GetTriggersInGroupAsync_excludes_missing_triggers()
        {
            var trigger = CreateTrigger("l31", "trigger");
            await Storage.StoreTriggerAsync(trigger, false);
            _cache!.Remove<byte[]>(NCacheKeys.Trigger(_prefix, SchedulerName, trigger.TriggerGroup, trigger.TriggerName), out _);
            var triggers = await Storage.GetTriggersInGroupAsync(trigger.TriggerGroup);
            Assert.Empty(triggers);
        }

        [Fact]
        public async Task GetAllTriggerGroupsAsync_returns_registered_groups()
        {
            var first = CreateTrigger("l32-a", "trigger1");
            var second = CreateTrigger("l32-b", "trigger2");
            await Storage.StoreTriggerAsync(first, false);
            await Storage.StoreTriggerAsync(second, false);
            var groups = await Storage.GetAllTriggerGroupsAsync();
            Assert.Contains("l32-a", groups);
            Assert.Contains("l32-b", groups);
        }

        [Fact]
        public async Task GetTriggersForCalendarAsync_returns_matching_triggers()
        {
            var first = CreateTrigger("l33", "first", calendarName: "calendar-a");
            var second = CreateTrigger("l33", "second", calendarName: "calendar-a");
            await Storage.StoreTriggerAsync(first, false);
            await Storage.StoreTriggerAsync(second, false);
            var triggers = await Storage.GetTriggersForCalendarAsync("calendar-a");
            Assert.Equal(2, triggers.Count);
            Assert.All(triggers, x => Assert.Equal("calendar-a", x.CalendarName));
        }

        [Fact]
        public async Task GetTriggersForCalendarAsync_ignores_other_calendars()
        {
            var first = CreateTrigger("l34", "first", calendarName: "calendar-a");
            var second = CreateTrigger("l34", "second", calendarName: "calendar-b");
            await Storage.StoreTriggerAsync(first, false);
            await Storage.StoreTriggerAsync(second, false);
            var triggers = await Storage.GetTriggersForCalendarAsync("calendar-a");
            Assert.Single(triggers);
            Assert.Equal("first", triggers[0].TriggerName);
        }

        [Fact]
        public async Task GetCalendarAsync_returns_null_when_missing()
        {
            var result = await Storage.GetCalendarAsync("l35");
            Assert.Null(result);
        }

        [Fact]
        public async Task StoreCalendarAsync_stores_calendar()
        {
            var calendar = new NCacheCalendarData
            {
                SchedulerName = SchedulerName,
                CalendarName = "l36",
                CalendarJson = "{}"
            };
            await Storage.StoreCalendarAsync(calendar, false);
            var result = await Storage.GetCalendarAsync("l36");
            Assert.NotNull(result);
            Assert.Equal("l36", result!.CalendarName);
        }

        [Fact]
        public async Task StoreCalendarAsync_rejects_duplicate()
        {
            var calendar = new NCacheCalendarData
            {
                SchedulerName = SchedulerName,
                CalendarName = "l37",
                CalendarJson = "{}"
            };
            await Storage.StoreCalendarAsync(calendar, false);
            await Assert.ThrowsAsync<ObjectAlreadyExistsException>(() => Storage.StoreCalendarAsync(calendar, false));
        }

        [Fact]
        public async Task StoreCalendarAsync_replaces_existing_calendar()
        {
            var first = new NCacheCalendarData
            {
                SchedulerName = SchedulerName,
                CalendarName = "l38",
                CalendarJson = "first"
            };
            await Storage.StoreCalendarAsync(first, false);
            var second = new NCacheCalendarData
            {
                SchedulerName = SchedulerName,
                CalendarName = "l38",
                CalendarJson = "second"
            };
            await Storage.StoreCalendarAsync(second, true);
            var result = await Storage.GetCalendarAsync("l38");
            Assert.NotNull(result);
            Assert.Equal("second", result!.CalendarJson);
        }

        [Fact]
        public async Task RemoveCalendarAsync_removes_existing_calendar()
        {
            var calendar = new NCacheCalendarData
            {
                SchedulerName = SchedulerName,
                CalendarName = "l39",
                CalendarJson = "{}"
            };
            await Storage.StoreCalendarAsync(calendar, false);
            var removed = await Storage.RemoveCalendarAsync("l39");
            Assert.True(removed);
            Assert.Null(await Storage.GetCalendarAsync("l39"));
        }

        [Fact]
        public async Task RemoveCalendarAsync_returns_false_when_missing()
        {
            var removed = await Storage.RemoveCalendarAsync("l40");
            Assert.False(removed);
        }

        [Fact]
        public async Task StoreCalendarAsync_registers_calendar()
        {
            var calendar = new NCacheCalendarData
            {
                SchedulerName = SchedulerName,
                CalendarName = "l41",
                CalendarJson = "{}"
            };
            await Storage.StoreCalendarAsync(calendar, false);
            var calendars = await Storage.GetRegisteredCalendarNamesAsync();
            Assert.Contains("l41", calendars);
        }

        [Fact]
        public async Task RemoveCalendarAsync_removes_calendar_from_registry()
        {
            var calendar = new NCacheCalendarData
            {
                SchedulerName = SchedulerName,
                CalendarName = "l42",
                CalendarJson = "{}"
            };
            await Storage.StoreCalendarAsync(calendar, false);
            await Storage.RemoveCalendarAsync("l42");
            var calendars = await Storage.GetRegisteredCalendarNamesAsync();
            Assert.DoesNotContain("l42", calendars);
        }

        [Fact]
        public async Task GetRegisteredCalendarNamesAsync_returns_registered_calendars()
        {
            var first = new NCacheCalendarData
            {
                SchedulerName = SchedulerName,
                CalendarName = "l43-a",
                CalendarJson = "{}"
            };
            var second = new NCacheCalendarData
            {
                SchedulerName = SchedulerName,
                CalendarName = "l43-b",
                CalendarJson = "{}"
            };
            await Storage.StoreCalendarAsync(first, false);
            await Storage.StoreCalendarAsync(second, false);
            var calendars = await Storage.GetRegisteredCalendarNamesAsync();
            Assert.Contains("l43-a", calendars);
            Assert.Contains("l43-b", calendars);
        }

        [Fact]
        public async Task GetFiredTriggerAsync_returns_null_when_missing()
        {
            var result = await Storage.GetFiredTriggerAsync("l44");
            Assert.Null(result);
        }

        [Fact]
        public async Task StoreFiredTriggerAsync_stores_fired_trigger()
        {
            var fired = CreateFiredTrigger("l45", "instance");
            await Storage.StoreFiredTriggerAsync(fired);
            var result = await Storage.GetFiredTriggerAsync("l45");
            Assert.NotNull(result);
            Assert.Equal("l45", result!.EntryId);
        }

        [Fact]
        public async Task StoreFiredTriggerAsync_registers_by_instance()
        {
            var fired = CreateFiredTrigger("l46", "instance-a");
            await Storage.StoreFiredTriggerAsync(fired);
            var results = await Storage.GetFiredTriggersForInstanceAsync("instance-a");
            Assert.Single(results);
            Assert.Equal("l46", results[0].EntryId);
        }

        [Fact]
        public async Task StoreFiredTriggerAsync_registers_by_job()
        {
            var fired = CreateFiredTrigger("l47", "instance-a", jobGroup: "job-group", jobName: "job", nonConcurrent: true);
            await Storage.StoreFiredTriggerAsync(fired);
            var results = await Storage.GetFiredTriggersForJobAsync("job-group", "job");
            Assert.Single(results);
            Assert.Equal("l47", results[0].EntryId);
        }

        [Fact]
        public async Task StoreFiredTriggerAsync_does_not_keep_a_job_registry_for_a_concurrent_job()
        {
            var fired = CreateFiredTrigger("l47b", "instance-a", jobGroup: "job-group", jobName: "job", nonConcurrent: false);
            await Storage.StoreFiredTriggerAsync(fired);

            Assert.Empty(await Storage.GetFiredTriggersForJobAsync("job-group", "job"));
            Assert.False(_cache!.Contains(NCacheKeys.JobFiredTriggers(_prefix, SchedulerName, "job-group", "job")));

            Assert.NotNull(await Storage.GetFiredTriggerAsync("l47b"));
            Assert.Single(await Storage.GetFiredTriggersForInstanceAsync("instance-a"));
        }

        [Fact]
        public async Task StoreFiredTriggerAsync_does_not_register_missing_job_identity()
        {
            var fired = CreateFiredTrigger("l48", "instance-a", jobGroup: null, jobName: null);
            await Storage.StoreFiredTriggerAsync(fired);
            var results = await Storage.GetFiredTriggersForJobAsync("job-group", "job");
            Assert.Empty(results);
            var instanceResults = await Storage.GetFiredTriggersForInstanceAsync("instance-a");
            Assert.Single(instanceResults);
        }

        [Fact]
        public async Task RemoveFiredTriggerAsync_removes_existing_firing()
        {
            var fired = CreateFiredTrigger("l49", "instance-a");
            await Storage.StoreFiredTriggerAsync(fired);
            var removed = await Storage.RemoveFiredTriggerAsync("l49");
            Assert.True(removed);
            Assert.Null(await Storage.GetFiredTriggerAsync("l49"));
        }

        [Fact]
        public async Task RemoveFiredTriggerAsync_returns_false_when_missing()
        {
            var removed = await Storage.RemoveFiredTriggerAsync("l50");
            Assert.False(removed);
        }

        [Fact]
        public async Task RemoveFiredTriggerAsync_removes_instance_registry_entry()
        {
            var fired = CreateFiredTrigger("l51", "instance-a");
            await Storage.StoreFiredTriggerAsync(fired);
            await Storage.RemoveFiredTriggerAsync("l51");
            var results = await Storage.GetFiredTriggersForInstanceAsync("instance-a");
            Assert.Empty(results);
        }

        [Fact]
        public async Task RemoveFiredTriggerAsync_removes_job_registry_entry()
        {
            var fired = CreateFiredTrigger("l52", "instance-a", jobGroup: "job-group", jobName: "job", nonConcurrent: true);
            await Storage.StoreFiredTriggerAsync(fired);
            await Storage.RemoveFiredTriggerAsync("l52");
            var results = await Storage.GetFiredTriggersForJobAsync("job-group", "job");
            Assert.Empty(results);
        }

        [Fact]
        public async Task GetFiredTriggersForJobAsync_returns_registered_firings()
        {
            var first = CreateFiredTrigger("l53-a", "instance-a", jobGroup: "job-group", jobName: "job", nonConcurrent: true);
            var second = CreateFiredTrigger("l53-b", "instance-b", jobGroup: "job-group", jobName: "job", nonConcurrent: true);
            await Storage.StoreFiredTriggerAsync(first);
            await Storage.StoreFiredTriggerAsync(second);
            var results = await Storage.GetFiredTriggersForJobAsync("job-group", "job");
            Assert.Equal(2, results.Count);
        }

        [Fact]
        public async Task GetFiredTriggersForInstanceAsync_returns_registered_firings()
        {
            var first = CreateFiredTrigger("l54-a", "instance-a");
            var second = CreateFiredTrigger("l54-b", "instance-a");
            await Storage.StoreFiredTriggerAsync(first);
            await Storage.StoreFiredTriggerAsync(second);
            var results = await Storage.GetFiredTriggersForInstanceAsync("instance-a");
            Assert.Equal(2, results.Count);
        }

        [Fact]
        public async Task CheckInAsync_stores_scheduler_state()
        {
            var state = new NCacheSchedulerState
            {
                SchedulerName = SchedulerName,
                InstanceName = "l55-instance",
                LastCheckinTimeUtc = DateTimeOffset.UtcNow,
                CheckinInterval = TimeSpan.FromSeconds(10)
            };
            await Storage.CheckInAsync(state);
            var result = await Storage.GetSchedulerStateAsync(state.InstanceName);
            Assert.NotNull(result);
            Assert.Equal(state.InstanceName, result!.InstanceName);
        }

        [Fact]
        public async Task CheckInAsync_registers_instance()
        {
            var state = new NCacheSchedulerState
            {
                SchedulerName = SchedulerName,
                InstanceName = "l56-instance",
                LastCheckinTimeUtc = DateTimeOffset.UtcNow,
                CheckinInterval = TimeSpan.FromSeconds(10)
            };
            await Storage.CheckInAsync(state);
            var instances = await Storage.GetRegisteredSchedulerInstanceNamesAsync();
            Assert.Contains("l56-instance", instances);
        }

        [Fact]
        public async Task GetSchedulerStateAsync_returns_state()
        {
            var state = new NCacheSchedulerState
            {
                SchedulerName = SchedulerName,
                InstanceName = "l57-instance",
                LastCheckinTimeUtc = DateTimeOffset.UtcNow,
                CheckinInterval = TimeSpan.FromSeconds(10)
            };
            await Storage.CheckInAsync(state);
            var result = await Storage.GetSchedulerStateAsync("l57-instance");
            Assert.NotNull(result);
            Assert.Equal(SchedulerName, result!.SchedulerName);
        }

        [Fact]
        public async Task GetSchedulerStateAsync_returns_null_when_missing()
        {
            var result = await Storage.GetSchedulerStateAsync("l58-instance");
            Assert.Null(result);
        }

        [Fact]
        public async Task RemoveSchedulerStateAsync_removes_state()
        {
            var state = new NCacheSchedulerState
            {
                SchedulerName = SchedulerName,
                InstanceName = "l59-instance",
                LastCheckinTimeUtc = DateTimeOffset.UtcNow,
                CheckinInterval = TimeSpan.FromSeconds(10)
            };
            await Storage.CheckInAsync(state);
            var removed = await Storage.RemoveSchedulerStateAsync("l59-instance");
            Assert.True(removed);
            Assert.Null(await Storage.GetSchedulerStateAsync("l59-instance"));
        }

        [Fact]
        public async Task RemoveSchedulerStateAsync_returns_false_when_missing()
        {
            var removed = await Storage.RemoveSchedulerStateAsync("l60-instance");
            Assert.False(removed);
        }

        [Fact]
        public async Task RemoveSchedulerStateAsync_removes_instance_registry_entry()
        {
            var state = new NCacheSchedulerState
            {
                SchedulerName = SchedulerName,
                InstanceName = "l61-instance",
                LastCheckinTimeUtc = DateTimeOffset.UtcNow,
                CheckinInterval = TimeSpan.FromSeconds(10)
            };
            await Storage.CheckInAsync(state);
            await Storage.RemoveSchedulerStateAsync("l61-instance");
            var instances = await Storage.GetRegisteredSchedulerInstanceNamesAsync();
            Assert.DoesNotContain("l61-instance", instances);
        }

        [Fact]
        public async Task GetRegisteredSchedulerInstanceNamesAsync_returns_instances()
        {
            var first = new NCacheSchedulerState
            {
                SchedulerName = SchedulerName,
                InstanceName = "l62-a",
                LastCheckinTimeUtc = DateTimeOffset.UtcNow,
                CheckinInterval = TimeSpan.FromSeconds(10)
            };
            var second = new NCacheSchedulerState
            {
                SchedulerName = SchedulerName,
                InstanceName = "l62-b",
                LastCheckinTimeUtc = DateTimeOffset.UtcNow,
                CheckinInterval = TimeSpan.FromSeconds(10)
            };
            await Storage.CheckInAsync(first);
            await Storage.CheckInAsync(second);
            var instances = await Storage.GetRegisteredSchedulerInstanceNamesAsync();
            Assert.Contains("l62-a", instances);
            Assert.Contains("l62-b", instances);
        }

        [Fact]
        public async Task GetAllSchedulerStatesAsync_returns_existing_states()
        {
            var first = new NCacheSchedulerState
            {
                SchedulerName = SchedulerName,
                InstanceName = "l63-a",
                LastCheckinTimeUtc = DateTimeOffset.UtcNow,
                CheckinInterval = TimeSpan.FromSeconds(10)
            };
            var second = new NCacheSchedulerState
            {
                SchedulerName = SchedulerName,
                InstanceName = "l63-b",
                LastCheckinTimeUtc = DateTimeOffset.UtcNow,
                CheckinInterval = TimeSpan.FromSeconds(10)
            };
            await Storage.CheckInAsync(first);
            await Storage.CheckInAsync(second);
            var results = await Storage.GetAllSchedulerStatesAsync(new[] { "l63-a", "l63-b" });
            Assert.Equal(2, results.Count);
        }
        [Fact]
        public async Task GetAllSchedulerStatesAsync_skips_missing_states()
        {
            var state = new NCacheSchedulerState { SchedulerName = SchedulerName, InstanceName = "l64-existing", LastCheckinTimeUtc = DateTimeOffset.UtcNow, CheckinInterval = TimeSpan.FromSeconds(10) };
            await Storage.CheckInAsync(state);
            var results = await Storage.GetAllSchedulerStatesAsync(new[] { "l64-existing", "l64-missing" });
            var result = Assert.Single(results);
            Assert.Equal("l64-existing", result.InstanceName);
        }

        [Fact]
        public async Task AddPausedTriggerGroupAsync_registers_group()
        {
            await Storage.AddPausedTriggerGroupAsync("l65");
            var groups = await Storage.GetPausedTriggerGroupsAsync();
            Assert.Contains("l65", groups);
        }

        [Fact]
        public async Task AddPausedTriggerGroupAsync_is_idempotent()
        {
            await Storage.AddPausedTriggerGroupAsync("l66");
            await Storage.AddPausedTriggerGroupAsync("l66");
            var groups = await Storage.GetPausedTriggerGroupsAsync();
            Assert.Equal(1, groups.Count(x => x == "l66"));
        }

        [Fact]
        public async Task RemovePausedTriggerGroupAsync_removes_group()
        {
            await Storage.AddPausedTriggerGroupAsync("l67");
            await Storage.RemovePausedTriggerGroupAsync("l67");
            var groups = await Storage.GetPausedTriggerGroupsAsync();
            Assert.DoesNotContain("l67", groups);
        }

        [Fact]
        public async Task RemovePausedTriggerGroupAsync_is_noop_when_missing()
        {
            await Storage.RemovePausedTriggerGroupAsync("l68");
            var groups = await Storage.GetPausedTriggerGroupsAsync();
            Assert.DoesNotContain("l68", groups);
        }

        [Fact]
        public async Task AddPausedJobGroupAsync_registers_group()
        {
            await Storage.AddPausedJobGroupAsync("l69");
            var groups = await Storage.GetPausedJobGroupsAsync();
            Assert.Contains("l69", groups);
        }

        [Fact]
        public async Task AddPausedJobGroupAsync_is_idempotent()
        {
            await Storage.AddPausedJobGroupAsync("l70");
            await Storage.AddPausedJobGroupAsync("l70");
            var groups = await Storage.GetPausedJobGroupsAsync();
            Assert.Equal(1, groups.Count(x => x == "l70"));
        }

        [Fact]
        public async Task RemovePausedJobGroupAsync_removes_group()
        {
            await Storage.AddPausedJobGroupAsync("l71");
            await Storage.RemovePausedJobGroupAsync("l71");
            var groups = await Storage.GetPausedJobGroupsAsync();
            Assert.DoesNotContain("l71", groups);
        }

        [Fact]
        public async Task RemovePausedJobGroupAsync_is_noop_when_missing()
        {
            await Storage.RemovePausedJobGroupAsync("l72");
            var groups = await Storage.GetPausedJobGroupsAsync();
            Assert.DoesNotContain("l72", groups);
        }

        [Fact]
        public async Task GetPausedTriggerGroupsAsync_returns_registered_groups()
        {
            await Storage.AddPausedTriggerGroupAsync("l73-a");
            await Storage.AddPausedTriggerGroupAsync("l73-b");
            var groups = await Storage.GetPausedTriggerGroupsAsync();
            Assert.Contains("l73-a", groups);
            Assert.Contains("l73-b", groups);
        }

        [Fact]
        public async Task GetPausedJobGroupsAsync_returns_registered_groups()
        {
            await Storage.AddPausedJobGroupAsync("l74-a");
            await Storage.AddPausedJobGroupAsync("l74-b");
            var groups = await Storage.GetPausedJobGroupsAsync();
            Assert.Contains("l74-a", groups);
            Assert.Contains("l74-b", groups);
        }

        [Fact]
        public async Task GetJobHeaderAsync_returns_header()
        {
            var header = CreateJobHeader("l75", "job");
            await Storage.StoreJobHeaderAsync(header, false);
            var result = await Storage.GetJobHeaderAsync("l75", "job");
            Assert.NotNull(result);
            Assert.Equal("job", result!.JobName);
            Assert.Equal("l75", result.JobGroup);
        }

        [Fact]
        public async Task GetJobHeaderAsync_returns_null_when_missing()
        {
            var result = await Storage.GetJobHeaderAsync("l76", "missing");
            Assert.Null(result);
        }

        [Fact]
        public async Task StoreJobHeaderAsync_stores_header()
        {
            var header = CreateJobHeader("l77", "job");
            await Storage.StoreJobHeaderAsync(header, false);
            Assert.NotNull(await Storage.GetJobHeaderAsync("l77", "job"));
        }

        [Fact]
        public async Task StoreJobHeaderAsync_replaces_existing_header()
        {
            var first = CreateJobHeader("l78", "job");
            first.Description = "First";
            await Storage.StoreJobHeaderAsync(first, false);
            var second = CreateJobHeader("l78", "job");
            second.Description = "Second";
            await Storage.StoreJobHeaderAsync(second, true);
            var result = await Storage.GetJobHeaderAsync("l78", "job");
            Assert.NotNull(result);
            Assert.Equal("Second", result!.Description);
        }

        [Fact]
        public async Task RemoveJobHeaderAsync_removes_header()
        {
            var header = CreateJobHeader("l79", "job");
            await Storage.StoreJobHeaderAsync(header, false);
            var removed = await Storage.RemoveJobHeaderAsync("l79", "job");
            Assert.True(removed);
            Assert.Null(await Storage.GetJobHeaderAsync("l79", "job"));
        }

        [Fact]
        public async Task RemoveJobHeaderAsync_returns_false_when_missing()
        {
            var removed = await Storage.RemoveJobHeaderAsync("l80", "missing");
            Assert.False(removed);
        }

        [Fact]
        public async Task AddJobToGroupRegistry_does_not_duplicate_names()
        {
            var job = CreateJob("l81", "job");
            await Storage.StoreJobAsync(job, false);
            await Storage.StoreJobAsync(CreateJob("l81", "job2"), false);
            var jobs = await Storage.GetJobsInGroupAsync("l81");
            Assert.Equal(2, jobs.Count);
        }

        [Fact]
        public async Task AddTriggerToJobRegistry_does_not_duplicate_references()
        {
            var trigger = CreateTrigger("l82", "trigger");
            await Storage.StoreTriggerAsync(trigger, false);
            await Assert.ThrowsAsync<ObjectAlreadyExistsException>(() => Storage.StoreTriggerAsync(trigger, false));
            var triggers = await Storage.GetTriggersForJobAsync(trigger.JobGroup, trigger.JobName);
            Assert.Single(triggers);
        }

        [Fact]
        public async Task AddTriggerToGroupRegistry_does_not_duplicate_names()
        {
            var first = CreateTrigger("l83", "first");
            await Storage.StoreTriggerAsync(first, false);
            var second = CreateTrigger("l83", "second");
            await Storage.StoreTriggerAsync(second, false);
            var triggers = await Storage.GetTriggersInGroupAsync("l83");
            Assert.Equal(2, triggers.Count);
        }

        [Fact]
        public async Task AddFiredTriggerToInstanceRegistry_does_not_duplicate_entries()
        {
            var fired = CreateFiredTrigger("l84", "instance-a");
            await Storage.StoreFiredTriggerAsync(fired);
            await Storage.StoreFiredTriggerAsync(fired);
            var results = await Storage.GetFiredTriggersForInstanceAsync("instance-a");
            Assert.Single(results);
        }

        [Fact]
        public async Task AddFiredTriggerToJobRegistry_does_not_duplicate_entries()
        {
            var fired = CreateFiredTrigger("l85", "instance-a", jobGroup: "job-group", jobName: "job", nonConcurrent: true);
            await Storage.StoreFiredTriggerAsync(fired);
            await Storage.StoreFiredTriggerAsync(fired);
            var results = await Storage.GetFiredTriggersForJobAsync("job-group", "job");
            Assert.Single(results);
        }

        [Fact]
        public async Task AddSchedulerInstance_does_not_duplicate_instances()
        {
            var state = new NCacheSchedulerState
            {
                SchedulerName = SchedulerName,
                InstanceName = "l86-instance",
                LastCheckinTimeUtc = DateTimeOffset.UtcNow,
                CheckinInterval = TimeSpan.FromSeconds(10)
            };
            await Storage.CheckInAsync(state);
            await Storage.CheckInAsync(state);
            var instances = await Storage.GetRegisteredSchedulerInstanceNamesAsync();
            Assert.Equal(1, instances.Count(x => x == "l86-instance"));
        }

        [Fact]
        public async Task StoreJobAsync_rolls_back_when_header_storage_fails()
        {
            var job = CreateJob("l87", "job");
            var headerKey = NCacheKeys.JobHeader(_prefix, SchedulerName, job.JobGroup, job.JobName);
            var (cache, control) = FaultInjectingCache.Wrap(_cache!);
            control.FailWhen = FaultInjectingCache.Call("Insert", k => k == headerKey);

            await Assert.ThrowsAnyAsync<Exception>(() => new NCacheStorage(cache, _prefix, SchedulerName).StoreJobAsync(job, false));

            Assert.Null(await Storage.GetJobAsync(job.JobGroup, job.JobName));
            Assert.Null(await Storage.GetJobHeaderAsync(job.JobGroup, job.JobName));
            Assert.Empty(await Storage.GetJobsInGroupAsync(job.JobGroup));
        }

        [Fact]
        public async Task StoreJobAsync_overwrites_a_leftover_header_that_has_no_job_row()
        {
            var job = CreateJob("l87b", "job");
            var leftover = CreateJobHeader(job.JobGroup, job.JobName);
            await Storage.StoreJobHeaderAsync(leftover, false);

            await Storage.StoreJobAsync(job, false);

            Assert.NotNull(await Storage.GetJobAsync(job.JobGroup, job.JobName));
            var header = await Storage.GetJobHeaderAsync(job.JobGroup, job.JobName);
            Assert.NotNull(header);
            Assert.Equal(job.Description, header!.Description);
        }

        [Fact]
        public async Task StoreJobAsync_rolls_back_when_job_group_registry_update_fails()
        {
            var job = CreateJob("l88", "job");
            Corrupt(NCacheKeys.JobsInGroup(_prefix, SchedulerName, job.JobGroup));
            await Assert.ThrowsAnyAsync<Exception>(() => Storage.StoreJobAsync(job, false));
            Assert.Null(await Storage.GetJobAsync(job.JobGroup, job.JobName));
            Assert.Null(await Storage.GetJobHeaderAsync(job.JobGroup, job.JobName));
        }

        [Fact]
        public async Task StoreTriggerAsync_rolls_back_new_trigger_when_registry_update_fails()
        {
            var trigger = CreateTrigger("l89", "trigger");
            Corrupt(NCacheKeys.JobTriggers(_prefix, SchedulerName, trigger.JobGroup, trigger.JobName));
            await Assert.ThrowsAnyAsync<Exception>(() => Storage.StoreTriggerAsync(trigger, false));
            Assert.Null(await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName));
            Assert.Empty(await Storage.GetTriggersInGroupAsync(trigger.TriggerGroup));
            Assert.DoesNotContain(trigger.TriggerGroup, await Storage.GetAllTriggerGroupsAsync());
        }

        [Fact]
        public async Task StoreTriggerAsync_restores_previous_trigger_when_registry_update_fails_on_replace()
        {
            var original = CreateTrigger("l89b", "trigger");
            original.Description = "Original";
            await Storage.StoreTriggerAsync(original, false);

            var replacement = CreateTrigger("l89b", "trigger");
            replacement.Description = "Replacement";
            Corrupt(NCacheKeys.TriggersInGroup(_prefix, SchedulerName, replacement.TriggerGroup));

            await Assert.ThrowsAnyAsync<Exception>(() => Storage.StoreTriggerAsync(replacement, true));

            var stored = await Storage.GetTriggerAsync("l89b", "trigger");
            Assert.NotNull(stored);
            Assert.Equal("Original", stored!.Description);
        }

        [Fact]
        public async Task UpdateTriggerAsync_overwrites_existing_trigger_without_touching_registries()
        {
            var trigger = CreateTrigger("l92", "trigger");
            await Storage.StoreTriggerAsync(trigger, false);

            trigger.TriggerState = TriggerStates.Blocked;
            trigger.Description = "Updated";
            await Storage.UpdateTriggerAsync(trigger);

            var stored = await Storage.GetTriggerAsync("l92", "trigger");
            Assert.NotNull(stored);
            Assert.Equal(TriggerStates.Blocked, stored!.TriggerState);
            Assert.Equal("Updated", stored.Description);
            Assert.Single(await Storage.GetTriggersInGroupAsync("l92"));
            Assert.Single(await Storage.GetTriggersForJobAsync(trigger.JobGroup, trigger.JobName));
        }

        [Fact]
        public async Task UpdateTriggerAsync_refuses_to_resurrect_deleted_trigger()
        {
            var trigger = CreateTrigger("l93", "trigger");

            await Assert.ThrowsAsync<JobPersistenceException>(() => Storage.UpdateTriggerAsync(trigger));
            Assert.Null(await Storage.GetTriggerAsync("l93", "trigger"));
        }

        [Fact]
        public async Task StoreTriggerAsync_replace_moves_registration_when_trigger_is_repointed_to_another_job()
        {
            var trigger = CreateTrigger("l94", "trigger", jobGroup: "job-group", jobName: "job-a");
            await Storage.StoreTriggerAsync(trigger, false);

            var repointed = CreateTrigger("l94", "trigger", jobGroup: "job-group", jobName: "job-b");
            await Storage.StoreTriggerAsync(repointed, true);

            Assert.Empty(await Storage.GetTriggersForJobAsync("job-group", "job-a"));
            Assert.Single(await Storage.GetTriggersForJobAsync("job-group", "job-b"));
        }

        [Fact]
        public async Task GetTriggersForJobAsync_ignores_registry_entries_that_point_at_another_job()
        {
            var trigger = CreateTrigger("l95", "trigger", jobGroup: "job-group", jobName: "job-a");
            await Storage.StoreTriggerAsync(trigger, false);

            trigger.JobName = "job-b";
            await Storage.UpdateTriggerAsync(trigger);

            Assert.Empty(await Storage.GetTriggersForJobAsync("job-group", "job-a"));
        }

        [Fact]
        public async Task WriteSchedulerStateAsync_does_not_register_instance()
        {
            await Storage.WriteSchedulerStateAsync(new NCacheSchedulerState
            {
                SchedulerName = SchedulerName,
                InstanceName = "l96-instance",
                LastCheckinTimeUtc = DateTimeOffset.UtcNow,
                CheckinInterval = TimeSpan.FromSeconds(10)
            });

            Assert.NotNull(await Storage.GetSchedulerStateAsync("l96-instance"));
            Assert.DoesNotContain("l96-instance", await Storage.GetRegisteredSchedulerInstanceNamesAsync());
        }

        [Fact]
        public async Task RemoveSchedulerStateRecordAsync_keeps_instance_registered()
        {
            await Storage.CheckInAsync(new NCacheSchedulerState
            {
                SchedulerName = SchedulerName,
                InstanceName = "l97-instance",
                LastCheckinTimeUtc = DateTimeOffset.UtcNow,
                CheckinInterval = TimeSpan.FromSeconds(10)
            });

            Assert.True(await Storage.RemoveSchedulerStateRecordAsync("l97-instance"));
            Assert.Null(await Storage.GetSchedulerStateAsync("l97-instance"));
            Assert.Contains("l97-instance", await Storage.GetRegisteredSchedulerInstanceNamesAsync());
        }

        [Fact]
        public async Task GetAllFiredTriggersAsync_spans_every_registered_instance()
        {
            await Storage.CheckInAsync(new NCacheSchedulerState { SchedulerName = SchedulerName, InstanceName = "l98-a", LastCheckinTimeUtc = DateTimeOffset.UtcNow, CheckinInterval = TimeSpan.FromSeconds(10) });
            await Storage.CheckInAsync(new NCacheSchedulerState { SchedulerName = SchedulerName, InstanceName = "l98-b", LastCheckinTimeUtc = DateTimeOffset.UtcNow, CheckinInterval = TimeSpan.FromSeconds(10) });
            await Storage.StoreFiredTriggerAsync(CreateFiredTrigger("l98-1", "l98-a"));
            await Storage.StoreFiredTriggerAsync(CreateFiredTrigger("l98-2", "l98-b"));
            await Storage.StoreFiredTriggerAsync(CreateFiredTrigger("l98-3", "l98-unregistered"));

            var all = await Storage.GetAllFiredTriggersAsync();

            Assert.Equal(new[] { "l98-1", "l98-2" }, all.Select(x => x.EntryId).OrderBy(x => x));
        }

        [Fact]
        public async Task ClearPausedGroupsAsync_removes_both_registries()
        {
            await Storage.AddPausedTriggerGroupAsync("l99-t");
            await Storage.AddPausedJobGroupAsync("l99-j");

            await Storage.ClearPausedGroupsAsync();

            Assert.Empty(await Storage.GetPausedTriggerGroupsAsync());
            Assert.Empty(await Storage.GetPausedJobGroupsAsync());
        }

        [Fact]
        public async Task RemoveJobAsync_leaves_consistent_state_after_registry_failure()
        {
            var job = CreateJob("l90", "job");
            await Storage.StoreJobAsync(job, false);
            Corrupt(NCacheKeys.JobsInGroup(_prefix, SchedulerName, job.JobGroup));
            await Assert.ThrowsAnyAsync<Exception>(() => Storage.RemoveJobAsync(job.JobGroup, job.JobName));
            Assert.Null(await Storage.GetJobAsync(job.JobGroup, job.JobName));
        }

        [Fact]
        public async Task RemoveTriggerAsync_leaves_consistent_state_after_registry_failure()
        {
            var trigger = CreateTrigger("l91", "trigger");
            await Storage.StoreTriggerAsync(trigger, false);
            Corrupt(NCacheKeys.JobTriggers(_prefix, SchedulerName, trigger.JobGroup, trigger.JobName));
            await Assert.ThrowsAnyAsync<Exception>(() => Storage.RemoveTriggerAsync(trigger.TriggerGroup, trigger.TriggerName));
            Assert.Null(await Storage.GetTriggerAsync(trigger.TriggerGroup, trigger.TriggerName));
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

