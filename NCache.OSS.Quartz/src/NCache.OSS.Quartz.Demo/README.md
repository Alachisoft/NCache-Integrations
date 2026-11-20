# NCache.OSS.Quartz.Demo

A small ASP.NET Core web app that shows **Quartz.NET 4** running on the **NCache job store**
(`NCache.OSS.Quartz`). It is the Quartz counterpart of the Hangfire NCache demo: one page with
a card per scenario, a live response / execution log, live stats read back from the store,
and the official Quartz dashboard mounted next to it.

```
src/NCache.OSS.Quartz.Demo/
  Program.cs                 Quartz + UseNCacheStore(configuration) + dashboard wiring
  Controllers/JobsController.cs   the scenario endpoints (/api/jobs/...)
  Controllers/HomeController.cs   serves the page
  Jobs/DemoJobs.cs           the IJob classes (DI-created, arguments from the JobDataMap)
  Services/DemoJobService.cs the "work" behind every job
  Services/ExecutionLog.cs   in-memory log + IJobListener (start / done / fail with the 4.2 outcome, retry
                             scheduled or exhausted, node)
  Views/Home/Index.cshtml    the page
  appsettings.json           Quartz:NCache section = NCacheJobStoreOptions
  client.ncconf.sample       template for an app-local NCache client config (see below)
```

## Running it

You need a running NCache OSS cache (default name `defaultCache`) that the machine's NCache
client configuration can reach.

```
dotnet run --project src/NCache.OSS.Quartz.Demo
```

Then open <http://localhost:5210/>. The Quartz dashboard is at <http://localhost:5210/quartz>
(HTTP API at `/quartz-api`).

To see the cluster in action, start a second instance with another instance id — the
`second-node` launch profile does exactly that:

```
dotnet run --project src/NCache.OSS.Quartz.Demo --launch-profile second-node
```

Both instances share the same scheduler name, cache and key prefix, so they form one
cluster: jobs scheduled on either page run on whichever node acquires them, the *Cluster*
panel lists both, and killing one mid-run lets the other take over its `RequestsRecovery`
work.

### Configuration

Everything the store needs is in `appsettings.json` and is bound straight to
`NCacheJobStoreOptions` by `UseNCacheStore(configuration, "Quartz:NCache")`:

```json
"Quartz": {
  "NCache": {
    "CacheName": "defaultCache",
    "SchedulerName": "ncache-quartz-demo",
    "InstanceId": "demo-node-1",
    "KeyPrefix": "ncache:quartz:demo:",
    "MisfireThreshold": "00:00:15",
    "ClusterCheckinInterval": "00:00:05",
    "ClusterCheckinMisfireThreshold": "00:00:07.5",
    "LockExpiration": "00:00:30",
    "RepairRegistriesOnStart": true
  }
}
```

Any of them can be overridden from the environment, e.g. `Quartz__NCache__InstanceId=node-7`.

**NCache client configuration.** The demo uses the machine-wide `client.ncconf` of the NCache
installation. The NCache client also picks up a `client.ncconf` in the process's current or
base directory — and with `dotnet run` the current directory is the project folder — so the
template ships as `client.ncconf.sample`. Copy it to `client.ncconf` next to the built app
(or add it to the project with `CopyToOutputDirectory`) only if this app should carry its own
server list.

## What the cards do

| Card | Quartz feature | What the NCache store does |
|---|---|---|
| 1. Fire-and-forget | one-shot trigger, `StartNow()` | non-durable job is removed once its trigger has fired |
| 2. Delayed job | `StartAt(+15s)` | the trigger survives an app restart in the cache |
| 3. Recurring job / reschedule | durable job + cron trigger (Quartz cron: seconds first); re-registering the same id calls `RescheduleJob` | `ReplaceTrigger`: state and previous fire time are kept. *Update details* calls `UpdateTriggerDetails`, which the store does not support (it reports the `NotSupportedException` and why); *Update priority* makes the same change the supported way: `GetTrigger` → `GetTriggerBuilder().WithPriority(9).StartNow()` → `RescheduleJob`, which the store's `ReplaceTrigger` applies keeping state, pause and last fire time |
| 4. Failing job + retries | `WithRetryPolicy(RetryPolicy.Fixed(3, 5s))` | persists the retry instant and attempt between attempts |
| 5. Continuations (Quartz 4.2) | one batch: a report plus two follow-ups built with `StartAfter(parent)`, one `OnSuccess`, one `OnFailure \| OnCancellation`; a second button makes the parent fail | both follow-ups sit in the store as **Awaiting** (never acquired); the report's completion releases the one its outcome names and deletes the other, on whichever node ran the report |
| 6. Non-concurrent job | `[DisallowConcurrentExecution]`, two triggers each fired every second | keeps the job's other trigger **Blocked** while a run is in progress (the running one reads Executing), cluster-wide |
| 7. Priorities + limits | `WithPriority`, `WithExecutionGroup("serial")`, `UseExecutionLimits` (limit 1, **cluster** scope; the `UseExecutionLimits` call is currently commented out in `Program.cs`, so the three jobs run in priority order but not one at a time) | hands out highest priority first and counts in-flight firings across nodes |
| 8. Bulk schedule | `ScheduleJobs` with 10,000 one-shot jobs in the `emails` group (the `emails` node-scoped limit is commented out with card 7's) | one locked, bulk write to schedule them; their 10,000 `DeleteTrigger` completions afterwards are batched and delete the spent triggers and jobs in bulk, so the store is idle again seconds after the last job ran |
| 9. Long-running job | 30-second job, `RequestRecovery()` | the trigger reads Executing while it runs and cannot be paused (a one-shot has fired for the last time); the row's *Interrupt* button signals the execution's cancellation token (`IScheduler.Interrupt`), and the store completes the firing as Cancelled |
| 10. Calendars | `WeeklyCalendar` (skip weekends) or `HolidayCalendar` (skip today) + `WithCalendarName` | persists the calendar, links triggers by name, recomputes linked triggers on replace, applies it to misfire recalculation |
| 11. Preferred node | `WithPreferredNode(PreferredNode.For(id))` or `PreferredNode.Auto` | only the named node acquires it while alive; another node takes over when it stops checking in; `Auto` claims are recorded and released |
| 12. Crash this node | 20-second `RequestsRecovery` job pinned to this node, then `Process.Kill()` — no shutdown | the surviving node notices the missed check-ins, creates a `RECOVERING_JOBS` trigger, re-runs the job with `Recovering = true`, and takes over triggers pinned to the dead node |
| 13. Misfires | cron every 10s, paused, resumed after the misfire threshold (15s in this demo) | applies the trigger's misfire instruction: `FireAndProceed`, `DoNothing` or `IgnoreMisfires` (fires every missed occurrence) |
| 14. Finite repeat / Complete | 3-fire simple trigger; a job that throws `JobExecutionException { UnscheduleFiringTrigger = true }` | after the last occurrence Quartz deletes the trigger (durable job stays); `SetTriggerComplete` leaves a trigger in **Complete** |
| 15. Other trigger kinds | `DailyTimeIntervalTrigger`, `CalendarIntervalTrigger`, `RecurrenceTrigger` (RRULE) | persisted with their time zones and rebuilt on read |
| 16. Overlap policy (Quartz 4.3) | a trigger every 2s × 6 for a job that takes 5s, built with `WithOverlapPolicy(Skip \| BufferOne \| CancelPrevious \| AllowAll)` | **Skip**: advances the trigger past the occurrence, answers `TriggerFiredResult.Declined` and raises `TriggerSkipped` (*SKIP* in the log). **BufferOne**: stores the trigger **Blocked** while its firing runs and releases it at completion. **CancelPrevious**: hands the scheduler the running firings of this node to interrupt (a firing on another node is waited for instead). **AllowAll**: fires beside the running one |
| 17. Pause with a reason (Quartz 4.3) | `PauseJobWith` / `PauseAllWith` with `PauseDetails { Reason, RequestedBy }`; the table reads `GetTriggerPause` | keeps the record (reason, requester, time) on the trigger row and per paused group until the resume; texts are cut to 250 / 200 characters |
| 18. Progress reporting (Quartz 4.3) | a 20-second job calling `context.ReportProgress(percent, message)` every second | writes the latest report onto the firing record (`UpdateFireInstanceProgress`), so `QueryFireInstances` shows it from every node; a report for a firing that has completed is not written |
| 19. Schedule with a conflict rule (Quartz 4.3) | `IScheduler.ScheduleTrigger(trigger, TriggerConflict.Throw \| Replace \| Keep \| KeepEarlier)` on a fixed key | decides under its lock what becomes of the stored trigger and reports `Created`, `Replaced` or `Kept` with the fire time of the trigger that is stored |
| 20. Per-trigger misfire threshold (Quartz 4.6) | two identical jobs stored paused through `ScheduleJobOptions { PauseReason, PauseRequestedBy }`, one trigger with its own `WithMisfireThreshold(3s)`; both resumed ~9s after their fire time | stores the trigger paused in the same locked write (`SupportsStoringPaused`), keeps the trigger's own threshold on its row, and judges each trigger by its own threshold (or the store's when it has none) on resume, acquisition, release from Blocked and calendar replacement: `-own` misfires and skips ahead, `-store` runs late |
| 21. Served / refused execution groups (Quartz 4.6) | 4 `reports` + 4 `emails` jobs with `WithExecutionGroup`; the `second-node` profile sets `Demo:RefusedExecutionGroups` = `reports`, which `Program.cs` passes to `RefuseExecutionGroups` | never acquires a trigger outside the node's served groups (or inside its refused ones) and does not misfire-handle it, so `reports` runs only on `demo-node-1` while `emails` runs on either node |

Every `RUN` line in the log carries the *occurrence* (scheduled fire time): two runs a few
milliseconds apart with different occurrences are missed occurrences being caught up within
the misfire threshold, not a duplicate execution.

Below the cards, *Stored jobs & triggers* lists what is in the store right now (state, schedule,
next / last fire, priority, calendar, preferred node, execution group, retry attempt, and for a
continuation the parent it waits for and the condition) with
pause / resume / delete per job, plus *Pause All*, *Resume All* and *Clear Store*; *Cluster*
lists every registered scheduler instance and its check-in state.

REST surface used by the page (all JSON, under `/api/jobs`): `POST enqueue-email`,
`POST schedule-report`, `POST recurring-cleanup`, `POST update-details`, `POST update-details-reschedule`, `POST failing-job`,
`POST continuation`, `POST non-concurrent`, `POST priority`, `POST bulk-emails`, `POST long-job`,
`POST calendar-job`, `POST pinned-job`, `POST crash`, `POST misfire`, `POST finite-job`,
`POST complete-job`, `POST interval-job`, `DELETE delete/{group}/{name}`,
`POST pause/{group}/{name}`, `POST resume/{group}/{name}` (both report whether anything was paused or
resumed), `POST interrupt/{group}/{name}`, `POST pause-all`, `POST resume-all`,
`POST clear`, `GET stats`, `GET log?since=<sequence>`.

The *Fired* counter is Quartz's own `SchedulerMetadata.JobsExecuted`, which counts a job when it
is fired, so it moves the moment a run starts; *Completed* (and the failed count beside it) come
from the demo's job listener and move when a run on this node finishes.

### Known store behaviour visible in the demo

* After a crash recovery (card 12) the *original* one-shot trigger of the recovered firing was
  recorded as fired before the node died, so the completion that would have deleted it never
  happened. The surviving node deletes that spent **Complete** trigger and its non-durable job as
  part of the recovery, as Quartz's ADO store does; only the `RECOVERING_JOBS` trigger's run shows
  in the log and the table is clean afterwards.
* `Program.cs` runs with `MaxBatchSize` 5, a 1-second idle wait and a 10-thread pool; `UseExecutionLimits` caps the `serial`
  group at 1 across the cluster and `emails` at 3 per node (cards 7 and 8).

## Security note

The Quartz dashboard and HTTP API are fully mutating and add no authentication of their own.
This demo maps both with `AllowAnonymous()` because it runs on localhost; put an authorization
policy on `MapQuartzDashboard` / `MapQuartzHttpApi` for anything reachable by others.
