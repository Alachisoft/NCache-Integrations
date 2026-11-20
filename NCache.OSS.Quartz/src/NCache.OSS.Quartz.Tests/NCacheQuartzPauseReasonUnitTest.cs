using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Model;
using Quartz;
using Quartz.Extensibility;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzPauseReasonUnitTest : NCacheQuartzScenarioTestBase
{
    private static readonly PauseDetails Maintenance = new() { Reason = "maintenance window", RequestedBy = "ops@example" };

    [Fact]
    public async Task PauseTriggerWith_records_the_reason_the_requester_and_the_time()
    {
        var store = await NewNodeAsync();
        var trigger = await ScheduleAsync(store, "job", "t");
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        Assert.True(await store.PauseTriggerWith(trigger.Key, Maintenance));

        var pause = (await store.GetTriggerPause(trigger.Key))!;
        Assert.Equal("maintenance window", pause.Reason);
        Assert.Equal("ops@example", pause.RequestedBy);
        Assert.InRange(pause.PausedAtUtc, before, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));

        var header = Assert.Single((await store.QueryTriggers(new TriggerQuery())).Items);
        Assert.Equal(pause, header.Pause);
        Assert.Empty(await AcquireDueAsync(store));
    }

    [Fact]
    public async Task A_pause_with_only_a_reason_or_only_a_requester_records_just_that()
    {
        var store = await NewNodeAsync();
        var a = await ScheduleAsync(store, "job-a", "a");
        var b = await ScheduleAsync(store, "job-b", "b");

        await store.PauseTriggerWith(a.Key, new PauseDetails { Reason = "why" });
        await store.PauseTriggerWith(b.Key, new PauseDetails { RequestedBy = "who", Reason = "   " });

        var pauseA = (await store.GetTriggerPause(a.Key))!;
        Assert.Equal("why", pauseA.Reason);
        Assert.Null(pauseA.RequestedBy);

        var pauseB = (await store.GetTriggerPause(b.Key))!;
        Assert.Null(pauseB.Reason);
        Assert.Equal("who", pauseB.RequestedBy);
    }

    public static TheoryData<PauseDetails?> DetailsThatSayNothing() => new()
    {
        null,
        new PauseDetails(),
        new PauseDetails { Reason = "", RequestedBy = "" },
        new PauseDetails { Reason = "  \t", RequestedBy = "\r\n" }
    };

    [Theory]
    [MemberData(nameof(DetailsThatSayNothing))]
    public async Task Details_that_say_nothing_pause_without_recording_anything(PauseDetails? details)
    {
        var store = await NewNodeAsync();
        var trigger = await ScheduleAsync(store, "job", "t");

        Assert.True(await store.PauseTriggerWith(trigger.Key, details));
        await store.PauseTriggerGroupsWith(GroupMatcher<TriggerKey>.GroupEquals("other"), details);
        await store.PauseJobGroupsWith(GroupMatcher<JobKey>.GroupEquals("other-jobs"), details);

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
        Assert.Null(await store.GetTriggerPause(trigger.Key));
        Assert.Null((await Storage.GetTriggerAsync("tests", "t"))!.Pause);
        Assert.Null(Assert.Single((await store.QueryTriggers(new TriggerQuery())).Items).Pause);
        Assert.Null(await store.GetTriggerGroupPause("other"));
        Assert.Null(await store.GetJobGroupPause("other-jobs"));
        Assert.Null(_cache!.Get<byte[]>(NCacheKeys.PausedTriggerGroupRecords(_prefix, SchedulerName)));
        Assert.Null(_cache!.Get<byte[]>(NCacheKeys.PausedJobGroupRecords(_prefix, SchedulerName)));
    }

    [Fact]
    public async Task Texts_longer_than_the_limits_are_cut_and_a_surrogate_pair_is_never_split()
    {
        var store = await NewNodeAsync();
        var plain = await ScheduleAsync(store, "job-a", "a");
        var paired = await ScheduleAsync(store, "job-b", "b");
        var exact = await ScheduleAsync(store, "job-c", "c");

        await store.PauseTriggerWith(plain.Key, new PauseDetails { Reason = new string('r', 300), RequestedBy = new string('u', 300) });
        await store.PauseTriggerWith(paired.Key, new PauseDetails
        {
            Reason = new string('r', PauseDetails.MaxReasonLength - 1) + "\U0001F600tail",
            RequestedBy = new string('u', PauseDetails.MaxRequestedByLength - 1) + "\U0001F600tail"
        });
        await store.PauseTriggerWith(exact.Key, new PauseDetails
        {
            Reason = new string('r', PauseDetails.MaxReasonLength),
            RequestedBy = new string('u', PauseDetails.MaxRequestedByLength - 2) + "\U0001F600"
        });

        var cut = (await store.GetTriggerPause(plain.Key))!;
        Assert.Equal(new string('r', 250), cut.Reason);
        Assert.Equal(new string('u', 200), cut.RequestedBy);

        var whole = (await store.GetTriggerPause(paired.Key))!;
        Assert.Equal(new string('r', 249), whole.Reason);
        Assert.Equal(new string('u', 199), whole.RequestedBy);

        var untouched = (await store.GetTriggerPause(exact.Key))!;
        Assert.Equal(250, untouched.Reason!.Length);
        Assert.Equal(new string('u', 198) + "\U0001F600", untouched.RequestedBy);
    }

    [Fact]
    public async Task A_trigger_that_is_already_paused_keeps_the_record_it_had()
    {
        var store = await NewNodeAsync();
        var trigger = await ScheduleAsync(store, "job", "t");
        await store.PauseTriggerWith(trigger.Key, new PauseDetails { Reason = "first" });
        var first = await store.GetTriggerPause(trigger.Key);

        Assert.False(await store.PauseTriggerWith(trigger.Key, new PauseDetails { Reason = "second" }));
        Assert.False(await store.PauseTrigger(trigger.Key));
        await store.PauseJobWith(trigger.JobKey, new PauseDetails { Reason = "third" });
        await store.PauseTriggerGroupsWith(GroupMatcher<TriggerKey>.GroupEquals("tests"), new PauseDetails { Reason = "fourth" });
        await store.PauseAllWith(new PauseDetails { Reason = "fifth" });

        Assert.Equal(first, await store.GetTriggerPause(trigger.Key));
    }

    [Fact]
    public async Task A_trigger_paused_without_a_reason_does_not_gain_one_from_a_later_pause()
    {
        var store = await NewNodeAsync();
        var trigger = await ScheduleAsync(store, "job", "t");
        await store.PauseTrigger(trigger.Key);

        Assert.False(await store.PauseTriggerWith(trigger.Key, Maintenance));

        Assert.Null((await Storage.GetTriggerAsync("tests", "t"))!.Pause);
        Assert.Null(await store.GetTriggerPause(trigger.Key));
    }

    [Fact]
    public async Task A_trigger_that_is_not_stored_is_not_paused_and_has_no_record()
    {
        var store = await NewNodeAsync();
        var missing = new TriggerKey("nope", "tests");

        Assert.False(await store.PauseTriggerWith(missing, Maintenance));
        Assert.Null(await store.GetTriggerPause(missing));
        Assert.False(await store.PauseJobWith(new JobKey("nope", "tests"), Maintenance));
    }

    [Theory]
    [InlineData(TriggerStates.Complete)]
    [InlineData(TriggerStates.Error)]
    [InlineData(TriggerStates.Awaiting)]
    public async Task A_trigger_in_a_state_that_cannot_be_paused_gets_no_record(string state)
    {
        var store = await NewNodeAsync();
        var trigger = await ScheduleAsync(store, "job", "t", DateTimeOffset.UtcNow.AddHours(1));
        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        row.TriggerState = state;
        await Index.RemoveAsync(row);
        await Storage.UpdateTriggerAsync(row);

        Assert.False(await store.PauseTriggerWith(trigger.Key, Maintenance));
        await store.PauseJobWith(trigger.JobKey, Maintenance);

        var after = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal(state, after.TriggerState);
        Assert.Null(after.Pause);
        Assert.Null(await store.GetTriggerPause(trigger.Key));
    }

    [Fact]
    public async Task A_trigger_that_is_not_paused_answers_no_pause_even_inside_a_group_with_a_record()
    {
        var store = await NewNodeAsync();
        var trigger = await ScheduleAsync(store, "job", "t");
        await store.PauseTriggerGroupsWith(GroupMatcher<TriggerKey>.GroupEquals("tests"), Maintenance);

        Assert.True(await store.ResumeTrigger(trigger.Key));

        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));
        Assert.Null(await store.GetTriggerPause(trigger.Key));
        Assert.NotNull(await store.GetTriggerGroupPause("tests"));
    }

    [Fact]
    public async Task A_blocked_trigger_is_paused_with_its_record_and_keeps_it_when_the_running_job_ends()
    {
        var store = await NewNodeAsync();
        var job = Job("job", nonConcurrent: true);
        var trigger = Repeating("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2), TimeSpan.FromSeconds(1));
        await store.ScheduleJob(job, trigger);
        var running = await FireOneAsync(store);

        Assert.True(await store.PauseTriggerWith(trigger.Key, Maintenance));

        Assert.Equal(TriggerStates.PausedAndBlocked, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        Assert.Equal("maintenance window", (await store.GetTriggerPause(trigger.Key))!.Reason);
        Assert.NotNull(Assert.Single((await store.QueryTriggers(new TriggerQuery())).Items).Pause);

        await store.TriggeredJobComplete(running.Trigger, running.JobDetail, SchedulerInstruction.NoInstruction);

        Assert.Equal(TriggerStates.Paused, (await Storage.GetTriggerAsync("tests", "t"))!.TriggerState);
        Assert.Equal("maintenance window", (await store.GetTriggerPause(trigger.Key))!.Reason);
    }

    [Fact]
    public async Task An_acquired_trigger_is_paused_with_its_record_and_its_acquisition_does_not_fire()
    {
        var store = await NewNodeAsync();
        var trigger = await ScheduleAsync(store, "job", "t");
        var acquired = Assert.Single(await AcquireDueAsync(store));

        Assert.True(await store.PauseTriggerWith(trigger.Key, Maintenance));

        var fired = Assert.Single(await store.TriggersFired(new[] { acquired }));
        Assert.Null(fired.TriggerFiredBundle);
        await store.ReleaseAcquiredTrigger(acquired);

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
        Assert.Equal("maintenance window", (await store.GetTriggerPause(trigger.Key))!.Reason);
        Assert.Empty(await Storage.GetAllFiredTriggersAsync());
    }

    [Fact]
    public async Task Resume_forgets_the_record_and_a_later_reasonless_pause_does_not_bring_it_back()
    {
        var store = await NewNodeAsync();
        var trigger = await ScheduleAsync(store, "job", "t");
        await store.PauseTriggerWith(trigger.Key, Maintenance);

        Assert.True(await store.ResumeTrigger(trigger.Key));

        Assert.Null(await store.GetTriggerPause(trigger.Key));
        Assert.Null((await Storage.GetTriggerAsync("tests", "t"))!.Pause);

        await store.PauseTrigger(trigger.Key);

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
        Assert.Null(await store.GetTriggerPause(trigger.Key));
    }

    [Fact]
    public async Task Resume_of_an_overdue_trigger_forgets_the_record_while_applying_its_misfire()
    {
        var store = await NewNodeAsync();
        var job = Job("job");
        var trigger = Repeating("t", "tests", job.Key, DateTimeOffset.UtcNow.AddHours(-3), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);
        await store.PauseTriggerWith(trigger.Key, Maintenance);

        Assert.True(await store.ResumeTrigger(trigger.Key));

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal(TriggerStates.Waiting, row.TriggerState);
        Assert.Null(row.Pause);
        Assert.True(row.NextFireTimeUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task PauseJobWith_gives_every_trigger_of_the_job_the_record_and_ResumeJob_takes_it_away()
    {
        var store = await NewNodeAsync();
        var job = Job("job");
        var a = Repeating("a", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2), TimeSpan.FromHours(1));
        var b = Repeating("b", "other", job.Key, DateTimeOffset.UtcNow.AddSeconds(-2), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, a);
        await store.AddTrigger(b);
        var bystander = await ScheduleAsync(store, "bystander-job", "bystander");

        Assert.True(await store.PauseJobWith(job.Key, Maintenance));

        var pauseA = await store.GetTriggerPause(a.Key);
        Assert.Equal("maintenance window", pauseA!.Reason);
        Assert.Equal(pauseA, await store.GetTriggerPause(b.Key));
        Assert.Null(await store.GetTriggerPause(bystander.Key));
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(bystander.Key));

        Assert.True(await store.ResumeJob(job.Key));

        Assert.Null(await store.GetTriggerPause(a.Key));
        Assert.Null(await store.GetTriggerPause(b.Key));
        Assert.Null((await Storage.GetTriggerAsync("other", "b"))!.Pause);
    }

    [Fact]
    public async Task PauseTriggerGroupsWith_records_the_group_and_its_triggers_and_a_trigger_added_later_answers_with_the_groups_record()
    {
        var store = await NewNodeAsync();
        var existing = await ScheduleAsync(store, "job", "t");

        var paused = await store.PauseTriggerGroupsWith(GroupMatcher<TriggerKey>.GroupEquals("tests"), Maintenance);

        Assert.Equal(new[] { "tests" }, paused);
        var group = (await store.GetTriggerGroupPause("tests"))!;
        Assert.Equal("maintenance window", group.Reason);
        Assert.Equal(group, await store.GetTriggerPause(existing.Key));

        var job = Job("late-job");
        await store.AddJob(job);
        var late = Repeating("late", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.AddTrigger(late);

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(late.Key));
        Assert.Null((await Storage.GetTriggerAsync("tests", "late"))!.Pause);
        Assert.Equal(group, await store.GetTriggerPause(late.Key));
        Assert.Null(await store.GetTriggerGroupPause("other"));
        Assert.Null(await store.GetJobGroupPause("tests"));
    }

    [Fact]
    public async Task A_group_with_no_triggers_yet_is_paused_with_its_record_for_the_triggers_that_come()
    {
        var store = await NewNodeAsync();

        var paused = await store.PauseTriggerGroupsWith(GroupMatcher<TriggerKey>.GroupEquals("future"), Maintenance);

        Assert.Equal(new[] { "future" }, paused);

        var job = Job("job");
        await store.AddJob(job);
        var trigger = Repeating("t", "future", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.AddTrigger(trigger);

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(trigger.Key));
        Assert.Equal("maintenance window", (await store.GetTriggerPause(trigger.Key))!.Reason);
    }

    [Fact]
    public async Task A_group_that_is_already_paused_keeps_its_record_but_a_trigger_the_second_pause_moves_carries_the_new_one()
    {
        var store = await NewNodeAsync();
        var stays = await ScheduleAsync(store, "job-a", "stays");
        var moves = await ScheduleAsync(store, "job-b", "moves");
        await store.PauseTriggerGroupsWith(GroupMatcher<TriggerKey>.GroupEquals("tests"), new PauseDetails { Reason = "first" });
        await store.ResumeTrigger(moves.Key);

        await store.PauseTriggerGroupsWith(GroupMatcher<TriggerKey>.GroupEquals("tests"), new PauseDetails { Reason = "second" });

        Assert.Equal("first", (await store.GetTriggerGroupPause("tests"))!.Reason);
        Assert.Equal("first", (await store.GetTriggerPause(stays.Key))!.Reason);
        Assert.Equal("second", (await store.GetTriggerPause(moves.Key))!.Reason);
    }

    [Fact]
    public async Task A_group_paused_without_a_reason_does_not_gain_one_from_a_second_pause()
    {
        var store = await NewNodeAsync();
        var trigger = await ScheduleAsync(store, "job", "t");
        await store.PauseTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals("tests"));

        await store.PauseTriggerGroupsWith(GroupMatcher<TriggerKey>.GroupEquals("tests"), Maintenance);

        Assert.Null(await store.GetTriggerGroupPause("tests"));
        Assert.Null(await store.GetTriggerPause(trigger.Key));
    }

    [Fact]
    public async Task PauseJobGroupsWith_records_the_job_group_and_a_trigger_added_later_answers_with_it()
    {
        var store = await NewNodeAsync();
        var job = Job("job", group: "billing");
        var existing = Repeating("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, existing);

        var paused = await store.PauseJobGroupsWith(GroupMatcher<JobKey>.GroupEquals("billing"), Maintenance);

        Assert.Equal(new[] { "billing" }, paused);
        var group = (await store.GetJobGroupPause("billing"))!;
        Assert.Equal("maintenance window", group.Reason);
        Assert.Equal(group, await store.GetTriggerPause(existing.Key));
        Assert.Null(await store.GetTriggerGroupPause("billing"));
        Assert.Null(await store.GetTriggerGroupPause("tests"));

        var late = Repeating("late", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.AddTrigger(late);

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(late.Key));
        Assert.Null((await Storage.GetTriggerAsync("tests", "late"))!.Pause);
        Assert.Equal(group, await store.GetTriggerPause(late.Key));
    }

    [Fact]
    public async Task The_triggers_own_record_wins_over_its_trigger_groups_which_wins_over_its_job_groups()
    {
        var store = await NewNodeAsync();
        var job = Job("job", group: "billing");
        await store.AddJob(job);
        await store.PauseJobGroupsWith(GroupMatcher<JobKey>.GroupEquals("billing"), new PauseDetails { Reason = "job group" });

        var byJobGroup = Repeating("by-job-group", "free", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.AddTrigger(byJobGroup);

        await store.PauseTriggerGroupsWith(GroupMatcher<TriggerKey>.GroupEquals("held"), new PauseDetails { Reason = "trigger group" });
        var byTriggerGroup = Repeating("by-trigger-group", "held", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.AddTrigger(byTriggerGroup);

        await store.ResumeTrigger(byTriggerGroup.Key);
        await store.PauseTriggerWith(byTriggerGroup.Key, new PauseDetails { Reason = "own" });

        var second = Repeating("second", "held", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.AddTrigger(second);

        Assert.Equal("job group", (await store.GetTriggerPause(byJobGroup.Key))!.Reason);
        Assert.Equal("own", (await store.GetTriggerPause(byTriggerGroup.Key))!.Reason);
        Assert.Equal("trigger group", (await store.GetTriggerPause(second.Key))!.Reason);
    }

    [Fact]
    public async Task ResumeTriggerGroups_and_ResumeJobGroups_remove_the_group_records()
    {
        var store = await NewNodeAsync();
        var job = Job("job", group: "billing");
        var trigger = Repeating("t", "tests", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);
        await store.PauseTriggerGroupsWith(GroupMatcher<TriggerKey>.GroupEquals("tests"), Maintenance);
        await store.PauseTriggerGroupsWith(GroupMatcher<TriggerKey>.GroupEquals("untouched"), Maintenance);
        await store.PauseJobGroupsWith(GroupMatcher<JobKey>.GroupEquals("billing"), Maintenance);

        await store.ResumeTriggerGroups(GroupMatcher<TriggerKey>.GroupEquals("tests"));

        Assert.Null(await store.GetTriggerGroupPause("tests"));
        Assert.NotNull(await store.GetTriggerGroupPause("untouched"));
        Assert.NotNull(await store.GetJobGroupPause("billing"));
        Assert.Null(await store.GetTriggerPause(trigger.Key));
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(trigger.Key));

        await store.ResumeJobGroups(GroupMatcher<JobKey>.GroupEquals("billing"));

        Assert.Null(await store.GetJobGroupPause("billing"));
        Assert.Null(_cache!.Get<byte[]>(NCacheKeys.PausedJobGroupRecords(_prefix, SchedulerName)));
    }

    [Fact]
    public async Task PauseAllWith_records_every_group_and_trigger_and_ResumeAll_removes_every_record()
    {
        var store = await NewNodeAsync();
        var job = Job("job", group: "billing");
        var a = Repeating("a", "alpha", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        var b = Repeating("b", "beta", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, a);
        await store.AddTrigger(b);
        await store.PauseJobGroupsWith(GroupMatcher<JobKey>.GroupEquals("billing"), new PauseDetails { Reason = "job group" });
        await store.ResumeJob(job.Key);

        await store.PauseAllWith(Maintenance);

        Assert.Equal("maintenance window", (await store.GetTriggerGroupPause("alpha"))!.Reason);
        Assert.Equal("maintenance window", (await store.GetTriggerGroupPause("beta"))!.Reason);
        Assert.Equal("maintenance window", (await store.GetTriggerPause(a.Key))!.Reason);
        Assert.Equal("maintenance window", (await store.GetTriggerPause(b.Key))!.Reason);
        Assert.Empty(await AcquireDueAsync(store, maxCount: 5));

        var late = Repeating("late", "gamma", job.Key, DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromHours(1));
        await store.AddTrigger(late);
        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(late.Key));

        await store.ResumeAll();

        Assert.Null(await store.GetTriggerGroupPause("alpha"));
        Assert.Null(await store.GetTriggerGroupPause("beta"));
        Assert.Null(await store.GetJobGroupPause("billing"));
        Assert.Null(await store.GetTriggerPause(a.Key));
        Assert.Null(await store.GetTriggerPause(b.Key));
        Assert.Null((await Storage.GetTriggerAsync("alpha", "a"))!.Pause);
        Assert.Null(_cache!.Get<byte[]>(NCacheKeys.PausedTriggerGroupRecords(_prefix, SchedulerName)));
        Assert.Null(_cache!.Get<byte[]>(NCacheKeys.PausedJobGroupRecords(_prefix, SchedulerName)));
        Assert.Equal(3, (await AcquireDueAsync(store, maxCount: 5)).Count);
    }

    [Fact]
    public async Task Clear_removes_every_pause_record()
    {
        var store = await NewNodeAsync();
        var trigger = await ScheduleAsync(store, "job", "t");
        await store.PauseAllWith(Maintenance);
        await store.PauseJobGroupsWith(GroupMatcher<JobKey>.GroupEquals("tests"), Maintenance);

        await store.Clear();

        Assert.Null(await store.GetTriggerGroupPause("tests"));
        Assert.Null(await store.GetJobGroupPause("tests"));
        Assert.Null(await store.GetTriggerPause(trigger.Key));
        Assert.Null(_cache!.Get<byte[]>(NCacheKeys.PausedTriggerGroupRecords(_prefix, SchedulerName)));
        Assert.Null(_cache!.Get<byte[]>(NCacheKeys.PausedJobGroupRecords(_prefix, SchedulerName)));

        var again = await ScheduleAsync(store, "job", "t");
        Assert.Equal(TriggerState.Normal, await store.GetTriggerState(again.Key));
    }

    [Fact]
    public async Task ReplaceTrigger_of_a_paused_trigger_keeps_its_record_and_a_replaced_running_one_has_none()
    {
        var store = await NewNodeAsync();
        var paused = await ScheduleAsync(store, "job-a", "paused");
        var normal = await ScheduleAsync(store, "job-b", "normal");
        await store.PauseTriggerWith(paused.Key, Maintenance);
        var recorded = await store.GetTriggerPause(paused.Key);

        await store.ReplaceTrigger(paused.Key, Repeating("paused", "tests", paused.JobKey, DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromHours(1)));
        await store.ReplaceTrigger(normal.Key, Repeating("normal", "tests", normal.JobKey, DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromHours(1)));

        Assert.Equal(TriggerState.Paused, await store.GetTriggerState(paused.Key));
        Assert.Equal(recorded, await store.GetTriggerPause(paused.Key));
        Assert.Null(await store.GetTriggerPause(normal.Key));
    }

    [Fact]
    public async Task A_record_written_by_one_node_is_answered_by_another()
    {
        var nodeA = await NewNodeAsync("node-a");
        var nodeB = await NewNodeAsync("node-b");
        var trigger = await ScheduleAsync(nodeA, "job", "t");

        await nodeA.PauseTriggerWith(trigger.Key, Maintenance);
        await nodeA.PauseJobGroupsWith(GroupMatcher<JobKey>.GroupEquals("tests"), new PauseDetails { Reason = "jobs" });

        Assert.Equal("maintenance window", (await nodeB.GetTriggerPause(trigger.Key))!.Reason);
        Assert.Equal("jobs", (await nodeB.GetJobGroupPause("tests"))!.Reason);

        await nodeB.ResumeAll();

        Assert.Null(await nodeA.GetTriggerPause(trigger.Key));
        Assert.Null(await nodeA.GetJobGroupPause("tests"));
    }

    [Fact]
    public async Task A_pause_whose_write_fails_leaves_the_trigger_running_and_without_a_record()
    {
        var (cache, control) = FaultInjectingCache.Wrap(_cache!);
        var store = await NewNodeAsync(cache: cache);
        var trigger = await ScheduleAsync(store, "job", "t");

        control.FailWhen = FaultInjectingCache.Call("Insert", k => k.EndsWith(":trigger:tests.t", StringComparison.Ordinal));
        await Assert.ThrowsAnyAsync<Exception>(() => store.PauseTriggerWith(trigger.Key, Maintenance).AsTask());
        control.FailWhen = null;

        var row = (await Storage.GetTriggerAsync("tests", "t"))!;
        Assert.Equal(TriggerStates.Waiting, row.TriggerState);
        Assert.Null(row.Pause);
        Assert.Null(await store.GetTriggerPause(trigger.Key));
        Assert.Contains(await Index.GetDueCandidatesAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10), c => c.TriggerName == "t");

        Assert.True(await store.PauseTriggerWith(trigger.Key, Maintenance));
        Assert.Equal("maintenance window", (await store.GetTriggerPause(trigger.Key))!.Reason);
    }

    [Fact]
    public async Task Null_keys_matchers_and_group_names_are_refused()
    {
        var store = await NewNodeAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.PauseTriggerWith(null!, Maintenance).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.PauseJobWith(null!, Maintenance).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.PauseTriggerGroupsWith(null!, Maintenance).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.PauseJobGroupsWith(null!, Maintenance).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.GetTriggerPause(null!).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.GetTriggerGroupPause(null!).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.GetJobGroupPause(null!).AsTask());
    }

    private static async Task<IOperableTrigger> ScheduleAsync(NCacheJobStore store, string jobName, string triggerName, DateTimeOffset? at = null)
    {
        var job = Job(jobName);
        var trigger = Repeating(triggerName, "tests", job.Key, at ?? DateTimeOffset.UtcNow.AddSeconds(-2), TimeSpan.FromHours(1));
        await store.ScheduleJob(job, trigger);
        return trigger;
    }
}
