# NCache Quartz Integration

NCache integration for [Quartz.NET](https://www.quartz-scheduler.net/). This package is a Quartz `IJobStore` implementation that keeps jobs, triggers, calendars and scheduler state in an NCache cluster, so every scheduler instance in a web farm shares the same jobs through NCache.

## Installation

```
Install-Package NCache.OSS.Quartz
```

or, using the .NET CLI:

```
dotnet add package NCache.OSS.Quartz
```

# What is Installed

Installing this package adds the NCache Quartz job store and its dependencies to your project.

The integration uses the following NCache configuration files:

**client.ncconf**: Defines the cache client settings, including the cache name and the list of server nodes the client connects to.

**config.ncconf**: Contains the cache configuration (cache topology, partitions, eviction policy, etc.) used when the cache is created/registered.

These configuration files are not added to your project by this package. Add `client.ncconf` if your application needs its own client configuration; `config.ncconf` is used on the NCache server when creating/registering the cache.

## Prerequisites

A .NET 10 compatible application, using Quartz 4.6.2.

An NCache 5.3.603 server should be up and running, with the target cache already created/registered on it.

## Getting Started

### Setting up NCache

The client reads the server list from the NCache client configuration. If NCache is not installed on the current machine, add a `client.ncconf` to your application, copy it to the output directory and set the `cache id` to your cache name:

Example:
```xml
<configuration>
  <ncache-server port="9800" />
  <cache id="defaultCache" enable-client-logs="False" log-level="error">
    <server name="10.0.0.1" />
  </cache>
</configuration>
```

Change `defaultCache` to the name of your actual cache (the cache you created/registered on the NCache server), and `10.0.0.1` to the IP/hostname of a node hosting it. The connection is resolved from `client.ncconf` by cache name, so the name passed to `UseNCacheStore` must match the cache `id` in that file.

### Setting up Package

Register NCache as the Quartz job store when configuring Quartz:

```csharp
using NCache.OSS.Quartz.Extensions;
using Quartz;

builder.Services.AddQuartz(q => q
    .UseNCacheStore("defaultCache"));

builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
```

To read the store options from application configuration, pass the configuration section:

```csharp
builder.Services.AddQuartz(q => q
    .UseNCacheStore(builder.Configuration, "Quartz:NCache"));
```

Outside of ASP.NET Core dependency injection, use `QuartzSchedulerBuilder` to create and start the scheduler directly:

```csharp
using NCache.OSS.Quartz.Extensions;
using Quartz;

await using var factory = QuartzSchedulerBuilder
    .Create(q => q.UseNCacheStore("defaultCache"))
    .Build();

var scheduler = await factory.GetScheduler();
await scheduler.Start();

Console.WriteLine("Scheduler running. Press Enter to stop.");
Console.ReadLine();

await scheduler.Shutdown(waitForJobsToComplete: true);
```

This does not require an application host or service registration. Quartz manages its own container internally, and disposing the factory releases it. You can also pass an `NCacheJobStoreOptions` instance to `UseNCacheStore` in this example.

To tune locking, misfire handling and cluster heartbeats, pass an `NCacheJobStoreOptions` instance:

```csharp
var storageOptions = new NCacheJobStoreOptions
{
    CacheName = "defaultCache",
    SchedulerName = "ncacheQuartz",
    InstanceId = "AUTO",
    LockExpiration = TimeSpan.FromSeconds(30),
    MisfireThreshold = TimeSpan.FromSeconds(60),
    ClusterCheckinInterval = TimeSpan.FromSeconds(10)
};

builder.Services.AddQuartz(q => q
    .UseNCacheStore(storageOptions));
```

Every scheduler node in the same cluster must use the same cache, scheduler name and key prefix. Each node needs a unique instance ID; leave `InstanceId` unset or use `"AUTO"` to generate one automatically.

## Configuration

Configure the storage through an `NCacheJobStoreOptions` instance.

| Property | Description |
|---|---|
| `CacheName` | Name of the NCache cache that stores the jobs and scheduler state. Required. Must match the cache `id` in `client.ncconf`. |
| `SchedulerName` | Name shared by scheduler instances in the same cluster. Defaults to the registered Quartz scheduler name. For a named Quartz registration, this must match the registration name. |
| `InstanceId` | Unique ID of this scheduler instance. Generated automatically when unset, empty or `"AUTO"`. |
| `KeyPrefix` | Prefix applied to the store's cache keys. Default `ncache:quartz:`. |
| `LockExpiration` | Lease duration of the distributed locks acquired against NCache. Default 30 seconds. |
| `LockAcquireTimeout` | Maximum time to wait while acquiring a distributed lock before giving up. Default `null` (no timeout; cancellation still applies). |
| `MisfireThreshold` | How late a trigger can be before the store treats it as misfired. Default 60 seconds. |
| `ClusterCheckinInterval` | Interval between scheduler heartbeats. Default 10 seconds. |
| `ClusterCheckinMisfireThreshold` | Additional time allowed beyond a scheduler's heartbeat interval before it is considered stale. Default 7.5 seconds. |
| `RepairRegistriesOnStart` | Rebuilds the store's registries and releases orphaned acquisitions when the scheduler starts. Default `true`. |
| `ConfigureSerializer` | Callback for configuring the System.Text.Json serializer registry used by the store for calendars, custom triggers and application types in a JobDataMap (declared with `AddTypeInfoResolver`; Quartz's serializer decides which of them it accepts). |

The integration provides:

`UseNCacheStore`: `IQuartzBuilder` extension methods that make NCache the active Quartz job store.

`NCacheJobStore`: The `IJobStore` implementation, including storage of jobs, triggers, calendars and scheduler state.

Distributed locks that protect shared scheduling state across multiple scheduler processes.

Scheduler registration and heartbeats, with detection and recovery of scheduler instances that stop heartbeating.

Recovery of jobs marked with `RequestsRecovery` after a scheduler failure, and handling of misfired triggers.

## Resources

[NCache Integration Docs](https://www.alachisoft.com/resources/docs/ncache/prog-guide/dot-net-third-party-integrations.html)

[Quartz Documentation](https://www.quartz-scheduler.net/documentation/)

[Programmer's Guide](https://www.alachisoft.com/resources/docs/ncache/prog-guide/)

## Technical Support

Alachisoft provides various technical support resources.

Visit the Alachisoft Support Center to select a support resource appropriate for your issue.

To request additional features or report a documentation discrepancy, contact support@alachisoft.com.

## License

Copyright © 2026 Alachisoft. All rights reserved.
