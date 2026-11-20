using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using NCache.OSS.Quartz.Mapping;
using NCache.OSS.Quartz.Model;
using NCache.OSS.Quartz.Serialization;
using Quartz;
using Quartz.Impl.Triggers;
using Xunit;

namespace NCache.OSS.Quartz.Tests
{
    public sealed class NCacheQuartzMappingSerializationUnitTest
    {
        [Fact]
        public void JobMapper_ToModel_maps_identity()
        {
            var job = CreateJobDetail("job-m01", "group-m01");

            var model = JobMapper.ToModel(job, "scheduler-m");

            Assert.Equal("job-m01", model.JobName);
            Assert.Equal("group-m01", model.JobGroup);
            Assert.Equal("scheduler-m", model.SchedulerName);
        }

        [Fact]
        public void JobMapper_ToModel_maps_description()
        {
            var job = CreateJobDetail("job-m02", "group-m02", "test description");

            var model = JobMapper.ToModel(job, "scheduler-m");

            Assert.Equal("test description", model.Description);
        }

        [Fact]
        public void JobMapper_ToModel_maps_job_type()
        {
            var job = CreateJobDetail("job-m03", "group-m03");

            var model = JobMapper.ToModel(job, "scheduler-m");

            Assert.Equal(new JobType(typeof(TestJob)).FullName, model.JobClassName);
        }
        [Fact]
        public void JobMapper_ToModel_maps_durability()
        {
            var job = JobBuilder.Create<TestJob>()
                .WithIdentity("job-m04", "group-m04")
                .StoreDurably()
                .Build();

            var model = JobMapper.ToModel(job, "scheduler-m");

            Assert.True(model.IsDurable);
        }

        [Fact]
        public void JobMapper_ToModel_maps_non_concurrent_flag()
        {
            var job = JobBuilder.Create<TestNonConcurrentJob>()
                .WithIdentity("job-m05", "group-m05")
                .Build();

            var model = JobMapper.ToModel(job, "scheduler-m");

            Assert.True(model.IsNonConcurrent);
        }

        [Fact]
        public void JobMapper_ToModel_maps_persist_job_data_flag()
        {
            var job = JobBuilder.Create<TestJob>()
                .WithIdentity("job-m06", "group-m06")
                .PersistJobDataAfterExecution()
                .Build();

            var model = JobMapper.ToModel(job, "scheduler-m");

            Assert.True(model.IsUpdateData);
        }

        [Fact]
        public void JobMapper_ToModel_maps_recovery_flag()
        {
            var job = JobBuilder.Create<TestJob>()
                .WithIdentity("job-m07", "group-m07")
                .RequestRecovery()
                .Build();

            var model = JobMapper.ToModel(job, "scheduler-m");

            Assert.True(model.RequestsRecovery);
        }

        [Fact]
        public void JobMapper_ToModel_maps_job_data_map()
        {
            var job = JobBuilder.Create<TestJob>()
                .WithIdentity("job-m08", "group-m08")
                .UsingJobData("name", "value")
                .UsingJobData("count", 42)
                .Build();

            var model = JobMapper.ToModel(job, "scheduler-m");

            Assert.Equal("value", model.JobDataMap["name"]);
            Assert.Equal(42, model.JobDataMap["count"]);
        }

        [Fact]
        public void JobMapper_ToQuartzJob_reconstructs_job_type()
        {
            var data = CreateJobData("job-m09", "group-m09");
            data.JobClassName = typeof(TestJob).AssemblyQualifiedName!;

            var job = JobMapper.ToQuartzJob(data);

            Assert.Equal(typeof(TestJob), job.JobType);
        }

        [Fact]
        public void JobMapper_ToQuartzJob_reconstructs_identity()
        {
            var data = CreateJobData("job-m10", "group-m10");
            data.JobClassName = typeof(TestJob).AssemblyQualifiedName!;

            var job = JobMapper.ToQuartzJob(data);

            Assert.Equal("job-m10", job.Key.Name);
            Assert.Equal("group-m10", job.Key.Group);
        }

        [Fact]
        public void JobMapper_ToQuartzJob_reconstructs_description()
        {
            var data = CreateJobData("job-m11", "group-m11");
            data.JobClassName = typeof(TestJob).AssemblyQualifiedName!;
            data.Description = "description";

            var job = JobMapper.ToQuartzJob(data);

            Assert.Equal("description", job.Description);
        }

        [Fact]
        public void JobMapper_ToQuartzJob_reconstructs_durability()
        {
            var data = CreateJobData("job-m12", "group-m12");
            data.JobClassName = typeof(TestJob).AssemblyQualifiedName!;
            data.IsDurable = true;

            var job = JobMapper.ToQuartzJob(data);

            Assert.True(job.Durable);
        }

        [Fact]
        public void JobMapper_ToQuartzJob_reconstructs_recovery_flag()
        {
            var data = CreateJobData("job-m13", "group-m13");
            data.JobClassName = typeof(TestJob).AssemblyQualifiedName!;
            data.RequestsRecovery = true;

            var job = JobMapper.ToQuartzJob(data);

            Assert.True(job.RequestsRecovery);
        }

        [Fact]
        public void JobMapper_ToQuartzJob_reconstructs_job_data_map()
        {
            var data = CreateJobData("job-m14", "group-m14");
            data.JobClassName = typeof(TestJob).AssemblyQualifiedName!;
            data.JobDataMap = new Dictionary<string, object?>
            {
                ["name"] = "value",
                ["count"] = 42
            };

            var job = JobMapper.ToQuartzJob(data);

            Assert.Equal("value", job.JobDataMap["name"]);
            Assert.Equal(42, job.JobDataMap["count"]);
        }

        [Fact]
        public void JobMapper_ToQuartzJob_throws_for_invalid_job_type()
        {
            var data = CreateJobData("job-m15", "group-m15");
            data.JobClassName = "No.Such.Type.That.Exists";

            Assert.Throws<JobPersistenceException>(() => JobMapper.ToQuartzJob(data));
        }

        [Fact]
        public void TriggerMapper_ToModel_maps_identity()
        {
            var trigger = CreateSimpleTrigger("trigger-m16", "group-m16");

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal("trigger-m16", model.TriggerName);
            Assert.Equal("group-m16", model.TriggerGroup);
            Assert.Equal("scheduler-m", model.SchedulerName);
        }

        [Fact]
        public void TriggerMapper_ToModel_maps_job_identity()
        {
            var trigger = CreateSimpleTrigger("trigger-m17", "group-m17", "job-m17", "job-group-m17");

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal("job-m17", model.JobName);
            Assert.Equal("job-group-m17", model.JobGroup);
        }

        [Fact]
        public void TriggerMapper_ToModel_maps_description()
        {
            var trigger = CreateSimpleTrigger("trigger-m18", "group-m18");
            trigger.Description = "trigger description";

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal("trigger description", model.Description);
        }

        [Fact]
        public void TriggerMapper_ToModel_maps_fire_times()
        {
            var start = DateTimeOffset.UtcNow.AddMinutes(-5);
            var next = DateTimeOffset.UtcNow.AddMinutes(5);
            var previous = DateTimeOffset.UtcNow.AddMinutes(-1);
            var trigger = CreateSimpleTrigger("trigger-m19", "group-m19");
            trigger.StartTimeUtc = start;
            trigger.NextFireTimeUtc = next;
            trigger.PreviousFireTimeUtc = previous;

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal(start, model.StartTimeUtc);
            Assert.Equal(next, model.NextFireTimeUtc);
            Assert.Equal(previous, model.PreviousFireTimeUtc);
        }

        [Fact]
        public void TriggerMapper_ToModel_maps_priority()
        {
            var trigger = CreateSimpleTrigger("trigger-m20", "group-m20");
            trigger.Priority = 17;

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal(17, model.Priority);
        }

        [Fact]
        public void TriggerMapper_ToModel_maps_state()
        {
            var trigger = CreateSimpleTrigger("trigger-m21", "group-m21");

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Paused);

            Assert.Equal(TriggerStates.Paused, model.TriggerState);
        }

        [Fact]
        public void TriggerMapper_ToModel_maps_start_and_end()
        {
            var start = DateTimeOffset.UtcNow.AddMinutes(-10);
            var end = DateTimeOffset.UtcNow.AddMinutes(10);
            var trigger = CreateSimpleTrigger("trigger-m22", "group-m22");
            trigger.StartTimeUtc = start;
            trigger.EndTimeUtc = end;

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal(start, model.StartTimeUtc);
            Assert.Equal(end, model.EndTimeUtc);
        }

        [Fact]
        public void TriggerMapper_ToModel_maps_calendar()
        {
            var trigger = CreateSimpleTrigger("trigger-m23", "group-m23");
            trigger.CalendarName = "calendar-m23";

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal("calendar-m23", model.CalendarName);
        }

        [Fact]
        public void TriggerMapper_ToModel_maps_misfire_instruction()
        {
            var trigger = CreateSimpleTrigger("trigger-m24", "group-m24");
            trigger.MisfireInstructionCode = 5;

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal(5, model.MisfireInstruction);
        }
        [Fact]
        public void TriggerMapper_ToModel_maps_execution_group()
        {
            var trigger = CreateSimpleTrigger("trigger-m25", "group-m25");
            trigger.ExecutionGroup = "execution-m25";

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal("execution-m25", model.ExecutionGroup);
        }

        [Fact]
        public void TriggerMapper_ToModel_maps_retry_policy()
        {
            var trigger = CreateSimpleTrigger("trigger-m26", "group-m26");
            trigger.RetryPolicy = RetryPolicy.Fixed(3, TimeSpan.FromSeconds(2));

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal(RetryPolicy.Fixed(3, TimeSpan.FromSeconds(2)).ToStoredString(), model.RetryPolicy);
        }

        [Fact]
        public void TriggerMapper_ToModel_maps_retry_attempt()
        {
            var trigger = CreateSimpleTrigger("trigger-m27", "group-m27");
            trigger.RetryAttempt = 4;

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal(4, model.RetryAttempt);
        }

        [Fact]
        public void TriggerMapper_ToModel_maps_preferred_node()
        {
            var trigger = CreateSimpleTrigger("trigger-m28", "group-m28");
            trigger.PreferredNode = PreferredNode.For("node-m28");

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal("node-m28", model.PreferredNode);
        }

        [Fact]
        public void TriggerMapper_ToModel_maps_automatic_preferred_node()
        {
            var trigger = CreateSimpleTrigger("trigger-m29", "group-m29");
            trigger.PreferredNode = PreferredNode.Auto;

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.True(model.PreferredNodeIsAutomatic);
        }

        [Fact]
        public void TriggerMapper_ToModel_maps_job_data_map()
        {
            var trigger = CreateSimpleTrigger("trigger-m30", "group-m30");
            trigger.JobDataMap["name"] = "value";
            trigger.JobDataMap["number"] = 42;

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal("value", model.JobDataMap["name"]);
            Assert.Equal(42, model.JobDataMap["number"]);
        }

        [Fact]
        public void TriggerMapper_maps_simple_trigger()
        {
            var trigger = CreateSimpleTrigger("trigger-m31", "group-m31");
            trigger.RepeatCount = 7;
            trigger.RepeatInterval = TimeSpan.FromSeconds(20);
            trigger.TimesTriggered = 3;

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal(NCacheTriggerKind.Simple, model.TriggerKind);
            Assert.NotNull(model.Simple);
            Assert.Equal(7, model.Simple!.RepeatCount);
            Assert.Equal(TimeSpan.FromSeconds(20), model.Simple.RepeatInterval);
            Assert.Equal(3, model.Simple.TimesTriggered);
        }

        [Fact]
        public void TriggerMapper_maps_cron_trigger()
        {
            var trigger = CreateCronTrigger("trigger-m32", "group-m32");

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal(NCacheTriggerKind.Cron, model.TriggerKind);
            Assert.NotNull(model.Cron);
            Assert.Equal("0/5 * * * * ?", model.Cron!.CronExpression);
            Assert.Equal(trigger.TimeZone.Id, model.Cron.TimeZoneId);
        }

        [Fact]
        public void TriggerMapper_maps_calendar_interval_trigger()
        {
            var trigger = CreateCalendarIntervalTrigger("trigger-m33", "group-m33");
            trigger.RepeatInterval = 4;
            trigger.RepeatIntervalUnit = IntervalUnit.Hour;
            trigger.TimesTriggered = 8;
            trigger.PreserveHourOfDayAcrossDaylightSavings = true;
            trigger.SkipDayIfHourDoesNotExist = true;

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal(NCacheTriggerKind.CalendarInterval, model.TriggerKind);
            Assert.NotNull(model.CalendarInterval);
            Assert.Equal(4, model.CalendarInterval!.RepeatInterval);
            Assert.Equal(IntervalUnit.Hour.ToString(), model.CalendarInterval.RepeatIntervalUnit);
            Assert.Equal(8, model.CalendarInterval.TimesTriggered);
            Assert.True(model.CalendarInterval.PreserveHourOfDayAcrossDaylightSavings);
            Assert.True(model.CalendarInterval.SkipDayIfHourDoesNotExist);
        }

        [Fact]
        public void TriggerMapper_maps_daily_time_interval_trigger()
        {
            var trigger = CreateDailyTimeIntervalTrigger("trigger-m34", "group-m34");
            trigger.RepeatInterval = 10;
            trigger.RepeatIntervalUnit = IntervalUnit.Minute;
            trigger.TimesTriggered = 6;
            trigger.StartTimeOfDay = new TimeOnly(8, 30, 0);
            trigger.EndTimeOfDay = new TimeOnly(17, 45, 0);
            trigger.DaysOfWeek = new HashSet<DayOfWeek>
            {
                DayOfWeek.Monday,
                DayOfWeek.Friday
            };
            trigger.RepeatCount = 9;

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal(NCacheTriggerKind.DailyTimeInterval, model.TriggerKind);
            Assert.NotNull(model.DailyTimeInterval);
            Assert.Equal(10, model.DailyTimeInterval!.RepeatInterval);
            Assert.Equal(IntervalUnit.Minute.ToString(), model.DailyTimeInterval.RepeatIntervalUnit);
            Assert.Equal(6, model.DailyTimeInterval.TimesTriggered);
            Assert.Equal("08:30:00", model.DailyTimeInterval.StartTimeOfDay);
            Assert.Equal("17:45:00", model.DailyTimeInterval.EndTimeOfDay);
            Assert.Equal(new HashSet<int> { 2, 6 }, model.DailyTimeInterval.DaysOfWeek);
            Assert.Equal(9, model.DailyTimeInterval.RepeatCount);
        }

        [Fact]
        public void TriggerMapper_round_trips_simple_trigger()
        {
            var original = CreateSimpleTrigger("trigger-m35", "group-m35");
            original.RepeatCount = 5;
            original.RepeatInterval = TimeSpan.FromMinutes(3);
            original.TimesTriggered = 2;

            var model = TriggerMapper.ToModel(original, "scheduler-m", TriggerStates.Waiting);
            var rebuilt = TriggerMapper.ToQuartzTrigger(model);

            var result = Assert.IsType<SimpleTriggerImpl>(rebuilt);
            Assert.Equal(5, result.RepeatCount);
            Assert.Equal(TimeSpan.FromMinutes(3), result.RepeatInterval);
            Assert.Equal(2, result.TimesTriggered);
        }

        [Fact]
        public void TriggerMapper_round_trips_cron_trigger()
        {
            var original = CreateCronTrigger("trigger-m36", "group-m36");

            var model = TriggerMapper.ToModel(original, "scheduler-m", TriggerStates.Waiting);
            var rebuilt = TriggerMapper.ToQuartzTrigger(model);

            var result = Assert.IsType<CronTriggerImpl>(rebuilt);
            Assert.Equal(original.CronExpressionString, result.CronExpressionString);
            Assert.Equal(original.TimeZone.Id, result.TimeZone.Id);
        }

        [Fact]
        public void TriggerMapper_round_trips_calendar_interval_trigger()
        {
            var original = CreateCalendarIntervalTrigger("trigger-m37", "group-m37");
            original.RepeatInterval = 3;
            original.RepeatIntervalUnit = IntervalUnit.Day;
            original.TimesTriggered = 4;
            original.PreserveHourOfDayAcrossDaylightSavings = true;
            original.SkipDayIfHourDoesNotExist = true;

            var model = TriggerMapper.ToModel(original, "scheduler-m", TriggerStates.Waiting);
            var rebuilt = TriggerMapper.ToQuartzTrigger(model);

            var result = Assert.IsType<CalendarIntervalTriggerImpl>(rebuilt);
            Assert.Equal(3, result.RepeatInterval);
            Assert.Equal(IntervalUnit.Day, result.RepeatIntervalUnit);
            Assert.Equal(4, result.TimesTriggered);
            Assert.True(result.PreserveHourOfDayAcrossDaylightSavings);
            Assert.True(result.SkipDayIfHourDoesNotExist);
        }

        [Fact]
        public void TriggerMapper_round_trips_daily_time_interval_trigger()
        {
            var original = CreateDailyTimeIntervalTrigger("trigger-m38", "group-m38");
            original.RepeatInterval = 15;
            original.RepeatIntervalUnit = IntervalUnit.Minute;
            original.TimesTriggered = 7;
            original.StartTimeOfDay = new TimeOnly(9, 15, 0);
            original.EndTimeOfDay = new TimeOnly(18, 30, 0);
            original.DaysOfWeek = new HashSet<DayOfWeek>
            {
                DayOfWeek.Tuesday,
                DayOfWeek.Thursday
            };
            original.RepeatCount = 12;

            var model = TriggerMapper.ToModel(original, "scheduler-m", TriggerStates.Waiting);
            var rebuilt = TriggerMapper.ToQuartzTrigger(model);

            var result = Assert.IsType<DailyTimeIntervalTriggerImpl>(rebuilt);
            Assert.Equal(15, result.RepeatInterval);
            Assert.Equal(IntervalUnit.Minute, result.RepeatIntervalUnit);
            Assert.Equal(7, result.TimesTriggered);
            Assert.Equal(new TimeOnly(9, 15, 0), result.StartTimeOfDay);
            Assert.Equal(new TimeOnly(18, 30, 0), result.EndTimeOfDay);
            Assert.Equal(new[] { DayOfWeek.Tuesday, DayOfWeek.Thursday }, result.DaysOfWeek.OrderBy(d => d));
            Assert.Equal(12, result.RepeatCount);
        }

        [Fact]
        public void TriggerMapper_ToQuartzTrigger_reconstructs_identity()
        {
            var data = CreateTriggerData("trigger-m39", "group-m39");

            var trigger = TriggerMapper.ToQuartzTrigger(data);

            Assert.Equal("trigger-m39", trigger.Key.Name);
            Assert.Equal("group-m39", trigger.Key.Group);
        }

        [Fact]
        public void TriggerMapper_ToQuartzTrigger_reconstructs_job_identity()
        {
            var data = CreateTriggerData("trigger-m40", "group-m40");
            data.JobName = "job-m40";
            data.JobGroup = "job-group-m40";

            var trigger = TriggerMapper.ToQuartzTrigger(data);

            Assert.Equal("job-m40", trigger.JobKey.Name);
            Assert.Equal("job-group-m40", trigger.JobKey.Group);
        }

        [Fact]
        public void TriggerMapper_ToQuartzTrigger_reconstructs_description()
        {
            var data = CreateTriggerData("trigger-m41", "group-m41");
            data.Description = "description-m41";

            var trigger = TriggerMapper.ToQuartzTrigger(data);

            Assert.Equal("description-m41", trigger.Description);
        }

        [Fact]
        public void TriggerMapper_ToQuartzTrigger_reconstructs_priority()
        {
            var data = CreateTriggerData("trigger-m42", "group-m42");
            data.Priority = 19;

            var trigger = TriggerMapper.ToQuartzTrigger(data);

            Assert.Equal(19, trigger.Priority);
        }

        [Fact]
        public void TriggerMapper_ToQuartzTrigger_reconstructs_state_fields()
        {
            var data = CreateTriggerData("trigger-m43", "group-m43");
            data.ExecutionGroup = "exec-m43";
            data.RetryAttempt = 3;
            data.PreferredNode = "node-m43";
            data.PreferredNodeIsAutomatic = false;

            var trigger = TriggerMapper.ToQuartzTrigger(data);

            Assert.Equal("exec-m43", trigger.ExecutionGroup);
            Assert.Equal(3, trigger.RetryAttempt);
            Assert.Equal("node-m43", trigger.PreferredNode.Node);
            Assert.False(trigger.PreferredNode.IsAutomatic);
        }

        [Fact]
        public void TriggerMapper_ToQuartzTrigger_reconstructs_fire_times()
        {
            var next = DateTimeOffset.UtcNow.AddMinutes(20);
            var previous = DateTimeOffset.UtcNow.AddMinutes(-20);
            var data = CreateTriggerData("trigger-m44", "group-m44");
            data.NextFireTimeUtc = next;
            data.PreviousFireTimeUtc = previous;

            var trigger = TriggerMapper.ToQuartzTrigger(data);

            Assert.Equal(next, trigger.NextFireTimeUtc);
            Assert.Equal(previous, trigger.PreviousFireTimeUtc);
        }

        [Fact]
        public void TriggerMapper_ToQuartzTrigger_reconstructs_calendar()
        {
            var data = CreateTriggerData("trigger-m45", "group-m45");
            data.CalendarName = "calendar-m45";

            var trigger = TriggerMapper.ToQuartzTrigger(data);

            Assert.Equal("calendar-m45", trigger.CalendarName);
        }

        [Fact]
        public void TriggerMapper_ToQuartzTrigger_reconstructs_misfire_instruction()
        {
            var data = CreateTriggerData("trigger-m46", "group-m46");
            data.MisfireInstruction = 5;

            var trigger = TriggerMapper.ToQuartzTrigger(data);

            Assert.Equal(5, trigger.MisfireInstructionCode);
        }

        [Fact]
        public void TriggerMapper_ToQuartzTrigger_reconstructs_execution_group()
        {
            var data = CreateTriggerData("trigger-m47", "group-m47");
            data.ExecutionGroup = "execution-m47";

            var trigger = TriggerMapper.ToQuartzTrigger(data);

            Assert.Equal("execution-m47", trigger.ExecutionGroup);
        }

        [Fact]
        public void TriggerMapper_ToQuartzTrigger_reconstructs_retry_policy()
        {
            var data = CreateTriggerData("trigger-m48", "group-m48");
            data.RetryPolicy = RetryPolicy.Exponential(
                4,
                TimeSpan.FromSeconds(2),
                2.0,
                TimeSpan.FromSeconds(20)).ToStoredString();

            var trigger = TriggerMapper.ToQuartzTrigger(data);

            Assert.NotNull(trigger.RetryPolicy);
            Assert.Equal(RetryPolicy.Parse(data.RetryPolicy), trigger.RetryPolicy);
        }

        [Fact]
        public void TriggerMapper_ToQuartzTrigger_reconstructs_retry_attempt()
        {
            var data = CreateTriggerData("trigger-m49", "group-m49");
            data.RetryAttempt = 8;

            var trigger = TriggerMapper.ToQuartzTrigger(data);

            Assert.Equal(8, trigger.RetryAttempt);
        }

        [Fact]
        public void TriggerMapper_ToQuartzTrigger_reconstructs_preferred_node()
        {
            var data = CreateTriggerData("trigger-m50", "group-m50");
            data.PreferredNode = "node-m50";
            data.PreferredNodeIsAutomatic = false;

            var trigger = TriggerMapper.ToQuartzTrigger(data);

            Assert.Equal("node-m50", trigger.PreferredNode.Node);
            Assert.False(trigger.PreferredNode.IsAutomatic);
        }

        [Fact]
        public void TriggerMapper_ToQuartzTrigger_reconstructs_automatic_preferred_node()
        {
            var data = CreateTriggerData("trigger-m51", "group-m51");
            data.PreferredNode = "*";
            data.PreferredNodeIsAutomatic = true;

            var trigger = TriggerMapper.ToQuartzTrigger(data);

            Assert.True(trigger.PreferredNode.IsAutomatic);
            Assert.Equal(PreferredNode.Auto, trigger.PreferredNode);
        }

        [Fact]
        public void TriggerMapper_ToQuartzTrigger_reconstructs_job_data_map()
        {
            var data = CreateTriggerData("trigger-m52", "group-m52");
            data.JobDataMap = new Dictionary<string, object?>
            {
                ["name"] = "value",
                ["count"] = 42
            };

            var trigger = TriggerMapper.ToQuartzTrigger(data);

            Assert.Equal("value", trigger.JobDataMap["name"]);
            Assert.Equal(42, trigger.JobDataMap["count"]);
        }

        [Fact]
        public void TriggerMapper_throws_when_simple_data_is_missing()
        {
            var data = CreateTriggerData("trigger-m53", "group-m53");
            data.TriggerKind = NCacheTriggerKind.Simple;
            data.Simple = null;

            Assert.Throws<InvalidOperationException>(() => TriggerMapper.ToQuartzTrigger(data));
        }

        [Fact]
        public void TriggerMapper_throws_when_cron_data_is_missing()
        {
            var data = CreateTriggerData("trigger-m54", "group-m54");
            data.TriggerKind = NCacheTriggerKind.Cron;
            data.Cron = null;

            Assert.Throws<InvalidOperationException>(() => TriggerMapper.ToQuartzTrigger(data));
        }

        [Fact]
        public void TriggerMapper_throws_when_calendar_interval_data_is_missing()
        {
            var data = CreateTriggerData("trigger-m55", "group-m55");
            data.TriggerKind = NCacheTriggerKind.CalendarInterval;
            data.CalendarInterval = null;

            Assert.Throws<InvalidOperationException>(() => TriggerMapper.ToQuartzTrigger(data));
        }

        [Fact]
        public void TriggerMapper_throws_when_daily_time_interval_data_is_missing()
        {
            var data = CreateTriggerData("trigger-m56", "group-m56");
            data.TriggerKind = NCacheTriggerKind.DailyTimeInterval;
            data.DailyTimeInterval = null;

            Assert.Throws<InvalidOperationException>(() => TriggerMapper.ToQuartzTrigger(data));
        }

        [Fact]
        public void TriggerMapper_throws_for_unknown_trigger_kind()
        {
            var data = CreateTriggerData("trigger-m57", "group-m57");
            data.TriggerKind = NCacheTriggerKind.Custom;

            Assert.Throws<NotSupportedException>(() => TriggerMapper.ToQuartzTrigger(data));
        }

        [Fact]
        public void TriggerMapper_throws_for_invalid_timezone()
        {
            var data = CreateTriggerData("trigger-m58", "group-m58");
            data.TriggerKind = NCacheTriggerKind.Cron;
            data.Cron = new NCacheCronTriggerData
            {
                CronExpression = "0/5 * * * * ?",
                TimeZoneId = "No.Such.Time.Zone"
            };

            Assert.Throws<TimeZoneNotFoundException>(() => TriggerMapper.ToQuartzTrigger(data));
        }

        [Fact]
        public void TriggerMapper_throws_for_invalid_interval_unit()
        {
            var data = CreateTriggerData("trigger-m59", "group-m59");
            data.TriggerKind = NCacheTriggerKind.CalendarInterval;
            data.CalendarInterval = new NCacheCalendarIntervalTriggerData
            {
                RepeatInterval = 1,
                RepeatIntervalUnit = "NotARealIntervalUnit",
                TimesTriggered = 0,
                TimeZoneId = TimeZoneInfo.Utc.Id
            };

            Assert.Throws<ArgumentException>(() => TriggerMapper.ToQuartzTrigger(data));
        }

        [Fact]
        public void TriggerMapper_throws_for_invalid_time_of_day()
        {
            var data = CreateTriggerData("trigger-m60", "group-m60");
            data.TriggerKind = NCacheTriggerKind.DailyTimeInterval;
            data.DailyTimeInterval = new NCacheDailyTimeIntervalTriggerData
            {
                RepeatInterval = 1,
                RepeatIntervalUnit = IntervalUnit.Minute.ToString(),
                TimesTriggered = 0,
                TimeZoneId = TimeZoneInfo.Utc.Id,
                StartTimeOfDay = "not-a-time",
                EndTimeOfDay = "10:00:00"
            };

            Assert.Throws<FormatException>(() => TriggerMapper.ToQuartzTrigger(data));
        }
        [Fact]
        public void TriggerMapper_automatic_preferred_node_claim_round_trip_behavior()
        {
            var data = CreateTriggerData("trigger-m61", "group-m61");
            data.PreferredNode = "node-m61";
            data.PreferredNodeIsAutomatic = true;

            var rebuilt = TriggerMapper.ToQuartzTrigger(data);

            Assert.True(rebuilt.PreferredNode.IsAutomatic);
            Assert.Null(rebuilt.PreferredNode.Node);
        }

        [Fact]
        public void TriggerMapper_explicit_preferred_node_round_trip_behavior()
        {
            var original = CreateSimpleTrigger("trigger-m62", "group-m62");
            original.PreferredNode = PreferredNode.For("node-m62");

            var model = TriggerMapper.ToModel(original, "scheduler-m", TriggerStates.Waiting);
            var rebuilt = TriggerMapper.ToQuartzTrigger(model);

            Assert.Equal("node-m62", model.PreferredNode);
            Assert.False(model.PreferredNodeIsAutomatic);
            Assert.Equal("node-m62", rebuilt.PreferredNode.Node);
            Assert.False(rebuilt.PreferredNode.IsAutomatic);
        }

        [Fact]
        public void JobMapper_round_trips_builder_set_concurrency_and_persistence_flags()
        {
            var job = JobBuilder.Create<TestJob>()
                .WithIdentity("job-m63", "group-m63")
                .DisallowConcurrentExecution()
                .PersistJobDataAfterExecution()
                .Build();

            var model = JobMapper.ToModel(job, "scheduler-m");
            Assert.True(model.IsNonConcurrent);
            Assert.True(model.IsUpdateData);

            var rebuilt = JobMapper.ToQuartzJob(model);
            Assert.True(rebuilt.ConcurrentExecutionDisallowed);
            Assert.True(rebuilt.PersistJobDataAfterExecution);

            var plain = JobMapper.ToQuartzJob(JobMapper.ToModel(JobBuilder.Create<TestJob>().WithIdentity("job-m64", "group-m64").Build(), "scheduler-m"));
            Assert.False(plain.ConcurrentExecutionDisallowed);
            Assert.False(plain.PersistJobDataAfterExecution);
        }

        [Fact]
        public void TriggerMapper_ToModel_rejects_trigger_types_it_cannot_reconstruct()
        {
            var custom = new CustomTrigger
            {
                Key = new TriggerKey("trigger-m65", "group-m65"),
                JobKey = new JobKey("job", "job-group"),
                StartTimeUtc = DateTimeOffset.UtcNow
            };

            Assert.Throws<NotSupportedException>(() => TriggerMapper.ToModel(custom, "scheduler-m", TriggerStates.Waiting));
        }

        [Fact]
        public void TriggerMapper_ApplyScheduleState_copies_counters_and_start_time_after_Triggered()
        {
            var trigger = CreateSimpleTrigger("trigger-m66", "group-m66");
            trigger.RepeatCount = 3;
            trigger.RepeatInterval = TimeSpan.FromMinutes(5);
            trigger.ComputeFirstFireTimeUtc(null);

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Acquired);
            Assert.Equal(0, model.Simple!.TimesTriggered);

            trigger.Triggered(null);
            TriggerMapper.ApplyScheduleState(model, trigger);

            Assert.Equal(1, model.Simple!.TimesTriggered);
            Assert.Equal(trigger.NextFireTimeUtc, model.NextFireTimeUtc);
            Assert.Equal(trigger.PreviousFireTimeUtc, model.PreviousFireTimeUtc);
            Assert.Equal(TriggerStates.Acquired, model.TriggerState);
        }

        [Fact]
        public void TriggerMapper_ApplyScheduleState_captures_what_a_misfire_rewrites()
        {
            var trigger = CreateSimpleTrigger("trigger-m67", "group-m67");
            trigger.StartTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-30);
            trigger.RepeatCount = 10;
            trigger.RepeatInterval = TimeSpan.FromMinutes(5);
            trigger.MisfireInstructionCode = (int)SimpleTriggerMisfireInstruction.NowWithRemainingCount;
            trigger.ComputeFirstFireTimeUtc(null);
            trigger.Triggered(null);
            trigger.Triggered(null);

            var model = TriggerMapper.ToModel(trigger, "scheduler-m", TriggerStates.Waiting);
            var originalStart = model.StartTimeUtc;

            trigger.UpdateAfterMisfire(null);
            TriggerMapper.ApplyScheduleState(model, trigger);

            Assert.NotEqual(originalStart, model.StartTimeUtc);
            Assert.Equal(trigger.StartTimeUtc, model.StartTimeUtc);
            Assert.Equal(((ISimpleTrigger)trigger).RepeatCount, model.Simple!.RepeatCount);
            Assert.Equal(((ISimpleTrigger)trigger).TimesTriggered, model.Simple.TimesTriggered);
            Assert.Equal(trigger.NextFireTimeUtc, model.NextFireTimeUtc);
        }

        [Fact]
        public void TriggerMapper_round_trips_recurrence_trigger()
        {
            var original = (global::Quartz.Extensibility.IOperableTrigger)TriggerBuilder.Create()
                .WithIdentity("trigger-m68", "group-m68")
                .ForJob("job", "job-group")
                .StartAt(DateTimeOffset.UtcNow)
                .WithRecurrenceSchedule("FREQ=DAILY;INTERVAL=2")
                .Build();

            var model = TriggerMapper.ToModel(original, "scheduler-m", TriggerStates.Waiting);

            Assert.Equal(NCacheTriggerKind.Recurrence, model.TriggerKind);
            Assert.NotNull(model.Recurrence);
            Assert.Equal("FREQ=DAILY;INTERVAL=2", model.Recurrence!.RecurrenceRule);

            var rebuilt = TriggerMapper.ToQuartzTrigger(NCacheSerializer.Deserialize<NCacheTriggerData>(NCacheSerializer.Serialize(model)));
            var result = Assert.IsType<RecurrenceTriggerImpl>(rebuilt);
            Assert.Equal("FREQ=DAILY;INTERVAL=2", result.RecurrenceRule);
            Assert.Equal(((IRecurrenceTrigger)original).TimeZone.Id, result.TimeZone.Id);
        }

        [Fact]
        public void JobDataMapValueConverter_round_trips_decimal_and_timespan()
        {
            var source = new Dictionary<string, object?>
            {
                ["amount"] = 12.34m,
                ["window"] = TimeSpan.FromMinutes(90)
            };

            var result = DeserializeMap(SerializeMap(source));

            Assert.IsType<decimal>(result["amount"]);
            Assert.Equal(12.34m, result["amount"]);
            Assert.IsType<TimeSpan>(result["window"]);
            Assert.Equal(TimeSpan.FromMinutes(90), result["window"]);
        }

        [Fact]
        public void JobDataMapMapper_null_map_returns_empty_dictionary()
        {
            var result = JobDataMapMapper.ToStorageMap(null);

            Assert.Empty(result);
        }

        [Fact]
        public void JobDataMapMapper_empty_map_round_trips_empty()
        {
            var source = new JobDataMap();

            var storage = JobDataMapMapper.ToStorageMap(source);
            var result = JobDataMapMapper.ToJobDataMap(storage);

            Assert.Empty(result);
        }

        [Fact]
        public void JobDataMapMapper_maps_string()
        {
            var source = new JobDataMap { ["value"] = "hello" };

            var storage = JobDataMapMapper.ToStorageMap(source);
            var result = JobDataMapMapper.ToJobDataMap(storage);

            Assert.Equal("hello", result["value"]);
        }

        [Fact]
        public void JobDataMapMapper_maps_int()
        {
            var source = new JobDataMap { ["value"] = 42 };

            var result = JobDataMapMapper.ToJobDataMap(JobDataMapMapper.ToStorageMap(source));

            Assert.Equal(42, result["value"]);
        }

        [Fact]
        public void JobDataMapMapper_maps_long()
        {
            var source = new JobDataMap { ["value"] = 42L };

            var result = JobDataMapMapper.ToJobDataMap(JobDataMapMapper.ToStorageMap(source));

            Assert.Equal(42L, result["value"]);
        }

        [Fact]
        public void JobDataMapMapper_maps_float()
        {
            var source = new JobDataMap { ["value"] = 1.5f };

            var result = JobDataMapMapper.ToJobDataMap(JobDataMapMapper.ToStorageMap(source));

            Assert.Equal(1.5f, result["value"]);
        }

        [Fact]
        public void JobDataMapMapper_maps_double()
        {
            var source = new JobDataMap { ["value"] = 1.5d };

            var result = JobDataMapMapper.ToJobDataMap(JobDataMapMapper.ToStorageMap(source));

            Assert.Equal(1.5d, result["value"]);
        }

        [Fact]
        public void JobDataMapMapper_maps_boolean()
        {
            var source = new JobDataMap { ["value"] = true };

            var result = JobDataMapMapper.ToJobDataMap(JobDataMapMapper.ToStorageMap(source));

            Assert.True((bool)result["value"]!);
        }

        [Fact]
        public void JobDataMapMapper_maps_guid()
        {
            var value = Guid.NewGuid();
            var source = new JobDataMap { ["value"] = value };

            var result = JobDataMapMapper.ToJobDataMap(JobDataMapMapper.ToStorageMap(source));

            Assert.Equal(value, result["value"]);
        }

        [Fact]
        public void JobDataMapMapper_maps_datetime()
        {
            var value = new DateTime(2026, 9, 9, 12, 30, 0, DateTimeKind.Utc);
            var source = new JobDataMap { ["value"] = value };

            var result = JobDataMapMapper.ToJobDataMap(JobDataMapMapper.ToStorageMap(source));

            Assert.Equal(value, result["value"]);
        }

        [Fact]
        public void JobDataMapMapper_maps_datetime_offset()
        {
            var value = new DateTimeOffset(2026, 9, 9, 12, 30, 0, TimeSpan.FromHours(5));
            var source = new JobDataMap { ["value"] = value };

            var result = JobDataMapMapper.ToJobDataMap(JobDataMapMapper.ToStorageMap(source));

            Assert.Equal(value, result["value"]);
        }

        [Fact]
        public void JobDataMapMapper_maps_null_value()
        {
            var source = new JobDataMap { ["value"] = null };

            var storage = JobDataMapMapper.ToStorageMap(source);

            Assert.True(storage.ContainsKey("value"));
            Assert.Null(storage["value"]);
        }

        [Fact]
        public void JobDataMapMapper_maps_multiple_values()
        {
            var guid = Guid.NewGuid();
            var source = new JobDataMap
            {
                ["string"] = "value",
                ["int"] = 42,
                ["long"] = 42L,
                ["float"] = 1.5f,
                ["double"] = 2.5d,
                ["bool"] = true,
                ["guid"] = guid
            };

            var result = JobDataMapMapper.ToJobDataMap(JobDataMapMapper.ToStorageMap(source));

            Assert.Equal("value", result["string"]);
            Assert.Equal(42, result["int"]);
            Assert.Equal(42L, result["long"]);
            Assert.Equal(1.5f, result["float"]);
            Assert.Equal(2.5d, result["double"]);
            Assert.True((bool)result["bool"]!);
            Assert.Equal(guid, result["guid"]);
        }

        [Fact]
        public void JobDataMapMapper_ToJobDataMap_keeps_null_values()
        {
            var storage = new Dictionary<string, object?>
            {
                ["present"] = "value",
                ["nullValue"] = null
            };

            var result = JobDataMapMapper.ToJobDataMap(storage);

            Assert.True(result.ContainsKey("present"));
            Assert.True(result.ContainsKey("nullValue"));
            Assert.Null(result["nullValue"]);
        }

        [Fact]
        public void JobDataMapMapper_round_trips_a_null_value()
        {
            var source = new JobDataMap { ["nullValue"] = null!, ["other"] = 1 };

            var result = JobDataMapMapper.ToJobDataMap(JobDataMapMapper.ToStorageMap(source));

            Assert.Equal(2, result.Count);
            Assert.True(result.ContainsKey("nullValue"));
            Assert.Null(result["nullValue"]);
        }

        [Fact]
        public void JobDataMapMapper_creates_independent_dictionary()
        {
            var source = new JobDataMap { ["value"] = "original" };

            var storage = JobDataMapMapper.ToStorageMap(source);
            source["value"] = "changed";
            source["new"] = "new";

            Assert.Equal("original", storage["value"]);
            Assert.False(storage.ContainsKey("new"));
        }

        [Fact]
        public void JobDataMapValueConverter_serializes_string()
        {
            var result = SerializeMap(new Dictionary<string, object?> { ["value"] = "hello" });

            Assert.Contains("\"$t\":\"String\"", result);
            Assert.Contains("\"$v\":\"hello\"", result);
        }

        [Fact]
        public void JobDataMapValueConverter_serializes_int()
        {
            var result = SerializeMap(new Dictionary<string, object?> { ["value"] = 42 });

            Assert.Contains("\"$t\":\"Int32\"", result);
            Assert.Contains("\"$v\":42", result);
        }

        [Fact]
        public void JobDataMapValueConverter_serializes_long()
        {
            var result = SerializeMap(new Dictionary<string, object?> { ["value"] = 42L });

            Assert.Contains("\"$t\":\"Int64\"", result);
            Assert.Contains("\"$v\":42", result);
        }

        [Fact]
        public void JobDataMapValueConverter_serializes_float()
        {
            var result = SerializeMap(new Dictionary<string, object?> { ["value"] = 1.5f });

            Assert.Contains("\"$t\":\"Single\"", result);
        }

        [Fact]
        public void JobDataMapValueConverter_serializes_double()
        {
            var result = SerializeMap(new Dictionary<string, object?> { ["value"] = 1.5d });

            Assert.Contains("\"$t\":\"Double\"", result);
        }

        [Fact]
        public void JobDataMapValueConverter_serializes_boolean()
        {
            var result = SerializeMap(new Dictionary<string, object?> { ["value"] = true });

            Assert.Contains("\"$t\":\"Boolean\"", result);
            Assert.Contains("\"$v\":true", result);
        }

        [Fact]
        public void JobDataMapValueConverter_serializes_guid()
        {
            var guid = Guid.NewGuid();

            var result = SerializeMap(new Dictionary<string, object?> { ["value"] = guid });

            Assert.Contains("\"$t\":\"Guid\"", result);
            Assert.Contains(guid.ToString(), result);
        }

        [Fact]
        public void JobDataMapValueConverter_serializes_datetime()
        {
            var value = new DateTime(2026, 9, 9, 12, 30, 0, DateTimeKind.Utc);

            var result = SerializeMap(new Dictionary<string, object?> { ["value"] = value });

            Assert.Contains("\"$t\":\"DateTime\"", result);
        }

        [Fact]
        public void JobDataMapValueConverter_serializes_datetime_offset()
        {
            var value = new DateTimeOffset(2026, 9, 9, 12, 30, 0, TimeSpan.FromHours(5));

            var result = SerializeMap(new Dictionary<string, object?> { ["value"] = value });

            Assert.Contains("\"$t\":\"DateTimeOffset\"", result);
        }

        [Fact]
        public void JobDataMapValueConverter_serializes_null()
        {
            var result = SerializeMap(new Dictionary<string, object?> { ["value"] = null });

            Assert.Contains("\"value\":null", result);
        }

        [Fact]
        public void JobDataMapValueConverter_round_trips_all_supported_types()
        {
            var guid = Guid.NewGuid();
            var dateTime = new DateTime(2026, 9, 9, 12, 30, 0, DateTimeKind.Utc);
            var dateTimeOffset = new DateTimeOffset(2026, 9, 9, 12, 30, 0, TimeSpan.FromHours(5));

            var source = new Dictionary<string, object?>
            {
                ["string"] = "value",
                ["int"] = 42,
                ["long"] = 42L,
                ["float"] = 1.5f,
                ["double"] = 2.5d,
                ["bool"] = true,
                ["guid"] = guid,
                ["dateTime"] = dateTime,
                ["dateTimeOffset"] = dateTimeOffset,
                ["null"] = null
            };

            var json = SerializeMap(source);
            var result = DeserializeMap(json);

            Assert.Equal("value", result["string"]);
            Assert.Equal(42, result["int"]);
            Assert.Equal(42L, result["long"]);
            Assert.Equal(1.5f, result["float"]);
            Assert.Equal(2.5d, result["double"]);
            Assert.Equal(true, result["bool"]);
            Assert.Equal(guid, result["guid"]);
            Assert.Equal(dateTime, result["dateTime"]);
            Assert.Equal(dateTimeOffset, result["dateTimeOffset"]);
            Assert.Null(result["null"]);
        }

        [Fact]
        public void JobDataMapValueConverter_throws_for_unsupported_type()
        {
            var source = new Dictionary<string, object?>
            {
                ["unsupported"] = double.NaN
            };

            Assert.Throws<JsonException>(() => SerializeMap(source));
        }

        [Fact]
        public void JobDataMapValueConverter_throws_for_invalid_json_token()
        {
            var json = """{"value":"not-an-entry-object"}""";

            Assert.Throws<InvalidOperationException>(() => DeserializeMap(json));
        }

        [Fact]
        public void JobDataMapValueConverter_throws_when_type_field_missing()
        {
            var json = """{"value":{"$v":42}}""";

            Assert.Throws<KeyNotFoundException>(() => DeserializeMap(json));
        }

        [Fact]
        public void JobDataMapValueConverter_throws_when_value_field_missing()
        {
            var json = """{"value":{"$t":"Int32"}}""";

            Assert.Throws<KeyNotFoundException>(() => DeserializeMap(json));
        }

        [Fact]
        public void JobDataMapValueConverter_throws_for_unsupported_stored_type()
        {
            var json = """{"value":{"$t":"UInt32","$v":7}}""";

            Assert.Throws<JsonException>(() => DeserializeMap(json));
        }

        [Fact]
        public void JobDataMapValueConverter_preserves_clr_types()
        {
            var source = new Dictionary<string, object?>
            {
                ["int"] = 42,
                ["long"] = 42L,
                ["float"] = 1.5f,
                ["double"] = 1.5d,
                ["bool"] = true
            };

            var result = DeserializeMap(SerializeMap(source));

            Assert.IsType<int>(result["int"]);
            Assert.IsType<long>(result["long"]);
            Assert.IsType<float>(result["float"]);
            Assert.IsType<double>(result["double"]);
            Assert.IsType<bool>(result["bool"]);
        }

        [Fact]
        public void NCacheSerializer_round_trips_job()
        {
            var source = CreateJobData("job-n33", "group-n33");
            source.Description = "job description";
            source.IsDurable = true;
            source.IsNonConcurrent = true;
            source.IsUpdateData = true;
            source.RequestsRecovery = true;
            source.JobDataMap = new Dictionary<string, object?>
            {
                ["value"] = "hello",
                ["count"] = 42
            };

            var bytes = NCacheSerializer.Serialize(source);
            var result = NCacheSerializer.Deserialize<NCacheJobData>(bytes);

            Assert.Equal(source.SchedulerName, result.SchedulerName);
            Assert.Equal(source.JobName, result.JobName);
            Assert.Equal(source.JobGroup, result.JobGroup);
            Assert.Equal(source.Description, result.Description);
            Assert.Equal(source.JobClassName, result.JobClassName);
            Assert.Equal(source.IsDurable, result.IsDurable);
            Assert.Equal(source.IsNonConcurrent, result.IsNonConcurrent);
            Assert.Equal(source.IsUpdateData, result.IsUpdateData);
            Assert.Equal(source.RequestsRecovery, result.RequestsRecovery);
            Assert.Equal("hello", result.JobDataMap["value"]);
            Assert.Equal(42, result.JobDataMap["count"]);
        }

        [Fact]
        public void NCacheSerializer_round_trips_trigger()
        {
            var source = CreateTriggerData("trigger-n34", "group-n34");
            source.TriggerKind = NCacheTriggerKind.Simple;
            source.Simple = new NCacheSimpleTriggerData
            {
                RepeatCount = 5,
                RepeatInterval = TimeSpan.FromSeconds(10),
                TimesTriggered = 2
            };
            source.JobDataMap["name"] = "value";

            var bytes = NCacheSerializer.Serialize(source);
            var result = NCacheSerializer.Deserialize<NCacheTriggerData>(bytes);

            Assert.Equal(source.TriggerName, result.TriggerName);
            Assert.Equal(source.TriggerGroup, result.TriggerGroup);
            Assert.Equal(NCacheTriggerKind.Simple, result.TriggerKind);
            Assert.NotNull(result.Simple);
            Assert.Equal(5, result.Simple!.RepeatCount);
            Assert.Equal(TimeSpan.FromSeconds(10), result.Simple.RepeatInterval);
            Assert.Equal(2, result.Simple.TimesTriggered);
            Assert.Equal("value", result.JobDataMap["name"]);
        }

        [Fact]
        public void NCacheSerializer_round_trips_calendar()
        {
            var source = new NCacheCalendarData
            {
                SchedulerName = "scheduler-n35",
                CalendarName = "calendar-n35",
                CalendarJson = "{\"type\":\"holiday\"}"
            };

            var bytes = NCacheSerializer.Serialize(source);
            var result = NCacheSerializer.Deserialize<NCacheCalendarData>(bytes);

            Assert.Equal(source.SchedulerName, result.SchedulerName);
            Assert.Equal(source.CalendarName, result.CalendarName);
            Assert.Equal(source.CalendarJson, result.CalendarJson);
        }

        [Fact]
        public void NCacheSerializer_round_trips_fired_trigger()
        {
            var source = new NCacheFiredTriggerData
            {
                SchedulerName = "scheduler-n36",
                EntryId = "entry-n36",
                TriggerName = "trigger-n36",
                TriggerGroup = "group-n36",
                InstanceName = "instance-n36",
                FiredTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                ScheduledTimeUtc = DateTimeOffset.UtcNow,
                Priority = 9,
                State = FiredTriggerStates.Executing,
                JobName = "job-n36",
                JobGroup = "job-group-n36",
                IsNonConcurrent = true,
                RequestsRecovery = true,
                ExecutionGroup = "exec-n36"
            };

            var bytes = NCacheSerializer.Serialize(source);
            var result = NCacheSerializer.Deserialize<NCacheFiredTriggerData>(bytes);

            Assert.Equal(source.SchedulerName, result.SchedulerName);
            Assert.Equal(source.EntryId, result.EntryId);
            Assert.Equal(source.TriggerName, result.TriggerName);
            Assert.Equal(source.TriggerGroup, result.TriggerGroup);
            Assert.Equal(source.InstanceName, result.InstanceName);
            Assert.Equal(source.FiredTimeUtc, result.FiredTimeUtc);
            Assert.Equal(source.ScheduledTimeUtc, result.ScheduledTimeUtc);
            Assert.Equal(source.Priority, result.Priority);
            Assert.Equal(source.State, result.State);
            Assert.Equal(source.JobName, result.JobName);
            Assert.Equal(source.JobGroup, result.JobGroup);
            Assert.Equal(source.IsNonConcurrent, result.IsNonConcurrent);
            Assert.Equal(source.RequestsRecovery, result.RequestsRecovery);
            Assert.Equal(source.ExecutionGroup, result.ExecutionGroup);
        }

        [Fact]
        public void NCacheSerializer_round_trips_scheduler_state()
        {
            var source = new NCacheSchedulerState
            {
                SchedulerName = "scheduler-n37",
                InstanceName = "instance-n37",
                LastCheckinTimeUtc = DateTimeOffset.UtcNow,
                CheckinInterval = TimeSpan.FromSeconds(30)
            };

            var bytes = NCacheSerializer.Serialize(source);
            var result = NCacheSerializer.Deserialize<NCacheSchedulerState>(bytes);

            Assert.Equal(source.SchedulerName, result.SchedulerName);
            Assert.Equal(source.InstanceName, result.InstanceName);
            Assert.Equal(source.LastCheckinTimeUtc, result.LastCheckinTimeUtc);
            Assert.Equal(source.CheckinInterval, result.CheckinInterval);
        }

        [Fact]
        public void NCacheSerializer_round_trips_job_header()
        {
            var source = new NCacheJobHeaderData
            {
                SchedulerName = "scheduler-n38",
                JobName = "job-n38",
                JobGroup = "group-n38",
                Description = "header description",
                JobClassName = typeof(TestJob).AssemblyQualifiedName!,
                IsDurable = true,
                IsNonConcurrent = true,
                IsUpdateData = true,
                RequestsRecovery = true
            };

            var bytes = NCacheSerializer.Serialize(source);
            var result = NCacheSerializer.Deserialize<NCacheJobHeaderData>(bytes);

            Assert.Equal(source.SchedulerName, result.SchedulerName);
            Assert.Equal(source.JobName, result.JobName);
            Assert.Equal(source.JobGroup, result.JobGroup);
            Assert.Equal(source.Description, result.Description);
            Assert.Equal(source.JobClassName, result.JobClassName);
            Assert.Equal(source.IsDurable, result.IsDurable);
            Assert.Equal(source.IsNonConcurrent, result.IsNonConcurrent);
            Assert.Equal(source.IsUpdateData, result.IsUpdateData);
            Assert.Equal(source.RequestsRecovery, result.RequestsRecovery);
        }

        [Fact]
        public void NCacheSerializer_serializes_enums_as_names()
        {
            var source = CreateTriggerData("trigger-n39", "group-n39");
            source.TriggerKind = NCacheTriggerKind.CalendarInterval;

            var json = Encoding.UTF8.GetString(NCacheSerializer.Serialize(source));

            Assert.Contains("\"TriggerKind\":\"CalendarInterval\"", json);
            Assert.DoesNotContain("\"TriggerKind\":2", json);
        }

        [Fact]
        public void NCacheSerializer_omits_null_properties()
        {
            var source = CreateJobData("job-n40", "group-n40");
            source.Description = null;

            var json = Encoding.UTF8.GetString(NCacheSerializer.Serialize(source));

            Assert.DoesNotContain("\"Description\"", json);
        }

        [Fact]
        public void NCacheSerializer_throws_when_deserialization_returns_null()
        {
            var bytes = Encoding.UTF8.GetBytes("null");

            Assert.Throws<InvalidOperationException>(
                () => NCacheSerializer.Deserialize<NCacheJobData>(bytes));
        }

        [Fact]
        public void NCacheSerializer_preserves_job_data_map_types()
        {
            var source = CreateJobData("job-n42", "group-n42");
            source.JobDataMap = new Dictionary<string, object?>
            {
                ["int"] = 42,
                ["long"] = 42L,
                ["float"] = 1.5f,
                ["double"] = 2.5d,
                ["bool"] = true,
                ["string"] = "value"
            };

            var bytes = NCacheSerializer.Serialize(source);
            var result = NCacheSerializer.Deserialize<NCacheJobData>(bytes);

            Assert.IsType<int>(result.JobDataMap["int"]);
            Assert.IsType<long>(result.JobDataMap["long"]);
            Assert.IsType<float>(result.JobDataMap["float"]);
            Assert.IsType<double>(result.JobDataMap["double"]);
            Assert.IsType<bool>(result.JobDataMap["bool"]);
            Assert.IsType<string>(result.JobDataMap["string"]);
        }

        [Fact]
        public void NCacheSerializer_round_trips_nested_model_data()
        {
            var source = CreateTriggerData("trigger-n43", "group-n43");
            source.TriggerKind = NCacheTriggerKind.DailyTimeInterval;
            source.DailyTimeInterval = new NCacheDailyTimeIntervalTriggerData
            {
                RepeatInterval = 15,
                RepeatIntervalUnit = IntervalUnit.Minute.ToString(),
                TimesTriggered = 5,
                TimeZoneId = TimeZoneInfo.Utc.Id,
                StartTimeOfDay = "08:00:00",
                EndTimeOfDay = "17:00:00",
                DaysOfWeek = new HashSet<int> { 2, 4, 6 },
                RepeatCount = 10
            };

            var bytes = NCacheSerializer.Serialize(source);
            var result = NCacheSerializer.Deserialize<NCacheTriggerData>(bytes);

            Assert.NotNull(result.DailyTimeInterval);
            Assert.Equal(15, result.DailyTimeInterval!.RepeatInterval);
            Assert.Equal(IntervalUnit.Minute.ToString(), result.DailyTimeInterval.RepeatIntervalUnit);
            Assert.Equal(5, result.DailyTimeInterval.TimesTriggered);
            Assert.Equal(TimeZoneInfo.Utc.Id, result.DailyTimeInterval.TimeZoneId);
            Assert.Equal("08:00:00", result.DailyTimeInterval.StartTimeOfDay);
            Assert.Equal("17:00:00", result.DailyTimeInterval.EndTimeOfDay);
            Assert.Equal(new HashSet<int> { 2, 4, 6 }, result.DailyTimeInterval.DaysOfWeek);
            Assert.Equal(10, result.DailyTimeInterval.RepeatCount);
        }

        private static IJobDetail CreateJobDetail(string name, string group, string? description = null)
        {
            return JobBuilder.Create<TestJob>()
                .WithIdentity(name, group)
                .WithDescription(description)
                .Build();
        }

        private static NCacheJobData CreateJobData(string name, string group)
        {
            return new NCacheJobData
            {
                SchedulerName = "scheduler-m",
                JobName = name,
                JobGroup = group,
                JobClassName = typeof(TestJob).AssemblyQualifiedName!
            };
        }

        private static NCacheTriggerData CreateTriggerData(string name, string group)
        {
            return new NCacheTriggerData
            {
                SchedulerName = "scheduler-m",
                TriggerName = name,
                TriggerGroup = group,
                JobName = "job-" + name,
                JobGroup = "job-group-" + group,
                TriggerState = TriggerStates.Waiting,
                TriggerKind = NCacheTriggerKind.Simple,
                StartTimeUtc = DateTimeOffset.UtcNow,

                Simple = new NCacheSimpleTriggerData
                {
                    RepeatCount = 0,
                    RepeatInterval = TimeSpan.FromMinutes(1),
                    TimesTriggered = 0
                }
            };
        }

        private static SimpleTriggerImpl CreateSimpleTrigger(
            string triggerName,
            string triggerGroup,
            string jobName = "job",
            string jobGroup = "job-group")
        {
            return new SimpleTriggerImpl
            {
                Key = new TriggerKey(triggerName, triggerGroup),
                JobKey = new JobKey(jobName, jobGroup),
                StartTimeUtc = DateTimeOffset.UtcNow,
                RepeatCount = 0,
                RepeatInterval = TimeSpan.FromMinutes(1),
                TimesTriggered = 0
            };
        }

        private static CronTriggerImpl CreateCronTrigger(string triggerName, string triggerGroup)
        {
            return new CronTriggerImpl
            {
                Key = new TriggerKey(triggerName, triggerGroup),
                JobKey = new JobKey("job", "job-group"),
                StartTimeUtc = DateTimeOffset.UtcNow,
                CronExpressionString = "0/5 * * * * ?",
                TimeZone = TimeZoneInfo.Utc
            };
        }

        private static CalendarIntervalTriggerImpl CreateCalendarIntervalTrigger(string triggerName, string triggerGroup)
        {
            return new CalendarIntervalTriggerImpl
            {
                Key = new TriggerKey(triggerName, triggerGroup),
                JobKey = new JobKey("job", "job-group"),
                StartTimeUtc = DateTimeOffset.UtcNow,
                RepeatInterval = 1,
                RepeatIntervalUnit = IntervalUnit.Day,
                TimesTriggered = 0,
                TimeZone = TimeZoneInfo.Utc
            };
        }

        private static DailyTimeIntervalTriggerImpl CreateDailyTimeIntervalTrigger(string triggerName, string triggerGroup)
        {
            return new DailyTimeIntervalTriggerImpl
            {
                Key = new TriggerKey(triggerName, triggerGroup),
                JobKey = new JobKey("job", "job-group"),
                StartTimeUtc = DateTimeOffset.UtcNow,
                RepeatInterval = 1,
                RepeatIntervalUnit = IntervalUnit.Minute,
                TimesTriggered = 0,
                StartTimeOfDay = new TimeOnly(8, 0, 0),
                EndTimeOfDay = new TimeOnly(18, 0, 0),
                DaysOfWeek = new HashSet<DayOfWeek>
                {
                    DayOfWeek.Monday,
                    DayOfWeek.Wednesday,
                    DayOfWeek.Friday
                },
                RepeatCount = -1,
                TimeZone = TimeZoneInfo.Utc
            };
        }

        private static string SerializeMap(Dictionary<string, object?> map)
        {
            var options = new JsonSerializerOptions();
            options.Converters.Add(new JobDataMapValueConverter());
            return JsonSerializer.Serialize(map, options);
        }

        private static Dictionary<string, object?> DeserializeMap(string json)
        {
            var options = new JsonSerializerOptions();
            options.Converters.Add(new JobDataMapValueConverter());
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, options)
                   ?? throw new InvalidOperationException("Map deserialized to null.");
        }

        public sealed class TestJob : IJob
        {
            public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
            {
                return ValueTask.CompletedTask;
            }
        }

        [DisallowConcurrentExecution]
        public sealed class TestNonConcurrentJob : IJob
        {
            public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
            {
                return ValueTask.CompletedTask;
            }
        }

        private sealed class CustomTrigger : TriggerBase
        {
            public override IScheduleBuilder GetScheduleBuilder() => SimpleScheduleBuilder.Create();
            public override DateTimeOffset? FinalFireTimeUtc => null;
            protected override bool HasMillisecondPrecision => true;
            public override bool MayFireAgain => true;
            public override DateTimeOffset? NextFireTimeUtc { get; set; }
            public override DateTimeOffset? PreviousFireTimeUtc { get; set; }
            public override DateTimeOffset? ComputeFirstFireTimeUtc(ICalendar? calendar) => StartTimeUtc;
            public override DateTimeOffset? GetFireTimeAfter(DateTimeOffset? afterTime) => null;
            public override void Triggered(ICalendar? calendar) { }
            public override void UpdateAfterMisfire(ICalendar? calendar) { }
            public override void UpdateWithNewCalendar(ICalendar? calendar, TimeSpan misfireThreshold) { }
            protected override bool ValidateMisfireInstruction(int misfireInstruction) => true;
        }

    }
}