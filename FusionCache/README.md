# NCache Backplane for FusionCache

**NCache implementation of `ZiggyCreatures.Caching.Fusion.Backplane.IFusionCacheBackplane`**, letting [FusionCache](https://github.com/ZiggyCreatures/FusionCache) use an NCache topic as its backplane so that cache invalidation/eviction notifications are propagated across every node sharing the same distributed cache.

## Package Versions

| Package | Version |
|---|---|
| Alachisoft.NCache.Opensource.SDK | >= 5.3.6.2 |
| ZiggyCreatures.FusionCache | >= 2.6.0 |
| Newtonsoft.Json | (used internally for message serialization) |

Targets `netstandard2.0`.

## Installation

```powershell
Install-Package NCache.OSS.ZiggyCreatures.FusionCache.Backplane  
dotnet add package NCache.OSS.ZiggyCreatures.FusionCache.Backplane 
```

## Prerequisites

Before using this package, ensure you have:

1. **NCache Server** – a running NCache cluster
2. **A Cache** – created on the cluster, matching the `cacheName` you pass to `NCacheBackplane`
3. **A FusionCache instance** already configured in your application

## Overview

`NCacheBackplane` connects to an NCache cache via `CacheManager.GetCache` and uses NCache's **Messaging Service** (Pub/Sub topics) to broadcast and receive `BackplaneMessage` payloads between FusionCache instances. Each `BackplaneMessage` is serialized to JSON and published on a topic named after `BackplaneSubscriptionOptions.ChannelName`; if the topic doesn't already exist on the cache, it's created on first connection.

**Key benefits:**

- Drop-in `IFusionCacheBackplane` implementation — no changes needed to how you use FusionCache
- Full sync and async API surface (`Subscribe`/`SubscribeAsync`, `Publish`/`PublishAsync`, `Unsubscribe`/`UnsubscribeAsync`)
- Connection to NCache is established lazily on first subscribe/publish, guarded by a semaphore so only one connection attempt happens at a time
- Messages are published with `DeliveryOption.All` (non-persistent), so all currently connected subscribers receive them

## Configuration

`NCacheBackplane` is constructed with a cache name and an optional `NCacheBackplaneOptions` instance:

```csharp
var backplane = new NCacheBackplane(
    cacheName: "myCache",
    options: new NCacheBackplaneOptions
    {
        AppName = "MyApp",
        ClientBindIP = "10.0.0.5",
        LoadBalance = true,
        ConnectionTimeout = TimeSpan.FromSeconds(30),
        ConnectionRetries = 3,
        RetryInterval = TimeSpan.FromSeconds(2),
        ServerList = new List<FusionCacheServerInfo>
        {
            new FusionCacheServerInfo("ncache-node-1", 9800),
            new FusionCacheServerInfo("ncache-node-2")
        }
    },
    logger: myLogger);
```

`NCacheBackplaneOptions` maps directly onto NCache's `CacheConnectionOptions` — only the properties you set are forwarded, so anything left `null` falls back to NCache's own defaults:

| Property | Description |
|---|---|
| `ServerList` | List of `FusionCacheServerInfo` (name + optional port) identifying cluster nodes |
| `ClientBindIP` | Local IP the client binds to |
| `LoadBalance` | Whether the client load-balances across servers |
| `ClientRequestTimeOut` | Per-request timeout |
| `ConnectionTimeout` | Timeout for establishing the initial connection |
| `ConnectionRetries` | Number of connection retry attempts |
| `RetryInterval` | Delay between connection retries |
| `RetryConnectionDelay` | Delay before attempting reconnection after a dropped connection |
| `AppName` | Application name reported to NCache |
| `EnableClientLogs` | Enables NCache client-side logging |
| `LogLevel` | NCache client log verbosity |

## Usage

Register the backplane with FusionCache like any other `IFusionCacheBackplane`:

```csharp
services.AddFusionCache()
    .WithBackplane(_ => new NCacheBackplane("myCache"));
```

Once subscribed, FusionCache handles calling `Publish`/`PublishAsync` when entries are set or removed, and `NCacheBackplane` forwards incoming messages from the NCache topic back into FusionCache via the configured `IncomingMessageHandler`/`IncomingMessageHandlerAsync`.

## Using NCache as the L2 Distributed Cache

In addition to acting as the FusionCache backplane, NCache can also be configured as FusionCache's L2 distributed cache by using the IDistributedCache implementation provided by the NCache.Microsoft.Caching.Extension.Opensource package.

This configuration gives you:

- L1 Cache – FusionCache's in-memory cache (per application instance)
- L2 Cache – NCache distributed cache via IDistributedCache
- Backplane – NCache Messaging Service for cache synchronization and invalidation across application instances

### Install

```powershell
Install-Package NCache.Microsoft.Caching.Extension.Opensource

dotnet add package NCache.Microsoft.Caching.Extension.Opensource
```

### Configure NCache as L2 cache in FusionCache

```csharp
var cacheName = "demoCache";

var config = new NCacheConfiguration
{
    CacheName = cacheName,
    EnableLogs = true,
    ExceptionsEnabled = true
};

var ncacheOptions = Options.Create(config);

builder.Services.AddFusionCache(nodeName)
    .WithDistributedCache(new NCacheDistributedCache(ncacheOptions));
```

## Sample

### What it does: 

Two simulated FusionCache "nodes" (Node A / Node B) share one Redis L2 cache and one Redis backplane. Each has its own in-memory L1, but a Set/Remove on either node updates Redis and notifies the other node so its L1 stays in sync — no manual invalidation needed.

### How to run it:

```powershell
dotnet restore && dotnet run
```

Open the printed URL (e.g. http://localhost:5000)


### How the results prove it's working:


1) Set a value on Node A → it appears on Node B too (reading from the shared Redis L2).
2) Set a different value on Node B.
3) Watch Node A: its activity log shows 
    ```⇦ backplane: received 'EntrySet' for key 'product:42'```, and its displayed value updates to match Node B's — without Node A ever calling Set itself.
4) Click Remove on either node → the other node's log shows an EntryRemove notification and its value clears too.

Note: If step 3/4 doesn't happen (each node updates itself but never reacts to the other), the backplane isn't wired correctly — see the notes in Program.cs about BackplaneChannelPrefix.

## Resources

- [NCache FusionCache](https://www.alachisoft.com/resources/docs/ncache/prog-guide/fusioncache.html) 
- [NCache FusionCache Backplane](https://www.alachisoft.com/resources/docs/ncache/prog-guide/fusioncache-backplane.html)
- [NCache FusionCache L2](https://www.alachisoft.com/resources/docs/ncache/prog-guide/fusioncache-provider.html)
- [FusionCache](https://github.com/ZiggyCreatures/FusionCache)
- [NuGet Package NCache.Fusion.Cache](https://www.nuget.org/packages/NCache.OSS.ZiggyCreatures.FusionCache.Backplane)
- [NCache Open Source](https://github.com/Alachisoft/NCache)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)

## Technical Support

Alachisoft© provides various sources of technical support.

- Please refer to http://www.alachisoft.com/support.html to select a support resource you find suitable for your issue.
- To request additional features in the future, or if you notice any discrepancy regarding this document, please drop an email to [support@alachisoft.com](mailto:support@alachisoft.com).

## License

Copyright © 2026 Alachisoft. All rights reserved.