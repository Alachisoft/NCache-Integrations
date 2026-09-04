# NCache Storage Provider for Hangfire

An [NCache](https://www.alachisoft.com/ncache/) implementation of [Hangfire](https://www.hangfire.io/) job storage, backing queues, job data, state, and server registration with an NCache cluster instead of SQL Server or Redis.

This package plugs into Hangfire's `JobStorage` abstraction, so it can be used as a drop in storage provider anywhere Hangfire is configured including across multiple worker instances in a web farm, with jobs, queues, and state shared through the NCache cluster.

## Features

- **Distributed Queues:**  FIFO job queues backed by NCache, with pub/sub driven wake up so workers are notified as jobs are enqueued instead of relying solely on polling.
- **Distributed Locking:** Job state changes, counters, and queue mutations are coordinated through NCache backed distributed locks, safe across multiple server processes.
- **Server Registration & Heartbeats:** Each Hangfire server announces itself, its queues, and its worker count into the cache, with automatic cleanup of servers that stop heartbeating.
- **Fetched Job Recovery:** An orphan recovery process requeues jobs left in a "processing" state past the configured invisibility timeout, e.g. after a server crash. 
- **Pub/Sub with Polling Fallback:** Falls back to interval polling automatically if pub/sub notifications are disabled or unavailable.

## Installation

```
dotnet add package NCache.OSS.Hangfire
```

## Requirements

- .NET Standard 2.0 / .NET Framework 4.6.2 or higher
- An NCache client connection (`Alachisoft.NCache.Client`) to a running NCache OSS cache/cluster 
## Usage

### Point the client at your server

Set your NCache server IP in `client.ncconf` (copied to the output directory on build):

```xml
<cache id="myCache" enable-client-logs="False" log-level="error">
    <server name="YOUR_SERVER_IP"/>
</cache>
```

`cache id` must match the cache name you use in code. A `config.ncconf` ships alongside it holding cache-side settings.

### Register NCache as the job storage

```csharp
var cacheName = builder.Configuration.GetValue<string>("NCache:CacheName") ?? "myCache";

builder.Services.AddHangfire(config => config
    .UseNCacheStorage(cacheName));
 
```

- First argument: your NCache cache name.

### Configuration

To tune queue polling, lock timing, or terminal-state list sizes, pass an `NCacheStorageOptions` instance:

```csharp
var storageOptions = new NCacheStorageOptions
{
    QueuePollInterval = TimeSpan.FromSeconds(60),
    InvisibilityTimeout = TimeSpan.FromMinutes(5),
    DistributedLockTimeout = TimeSpan.FromSeconds(30),
    LockAcquireMaxWait = TimeSpan.FromSeconds(15),
    EnablePubSubNotifications = true,
    SucceededListSize = 10000,
    DeletedListSize = 1000,
    FailedListSize = 1000
};

builder.Services.AddHangfire(config => config
    .UseNCacheStorage(cacheName, storageOptions));
```

| Option | Description |
|---|---|
| `QueuePollInterval` | Polling interval used as a fallback when pub/sub notifications are disabled. |
| `InvisibilityTimeout` | How long a fetched job stays hidden from other workers before it's eligible for orphan recovery. |
| `DistributedLockTimeout` | Lease duration for distributed locks acquired against NCache. |
| `LockAcquireMaxWait` | Maximum time to wait while attempting to acquire a distributed lock before giving up. |
| `EnablePubSubNotifications` | Enables NCache topic-based wake up for queue fetches; falls back to polling when `false`. |
| `SucceededListSize` / `DeletedListSize` / `FailedListSize` | Number of recent jobs retained per terminal state for the Dashboard. |

## Important

If NCache is not installed on the machine, you must ensure that `client.ncconf` and `config.ncconf` contain all required configuration information.

## Documentation
- [NCache Programmer Guide](http://www.alachisoft.com/resources/docs/ncache/prog-guide/)
- [NCache Documentation](http://www.alachisoft.com/resources/docs/#ncache)
- [Hangfire Documentation](https://docs.hangfire.io/)
 
## License
Copyrights 2026: Alachisoft, all rights reserved.