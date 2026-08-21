# NCache.OSS.DistributedLock

An NCache implementation of the [DistributedLock](https://github.com/madelson/DistributedLock) API, providing distributed locking, semaphore, and reader-writer lock primitives backed by an NCache cluster. This package plugs into the `DistributedLock.Core` abstractions (`IDistributedLock`, `IDistributedSemaphore`, `IDistributedReaderWriterLock`), so it can be used as a drop-in synchronization provider anywhere those interfaces are expected — with lock keys auto-expired in NCache as a safety net against processes that crash while holding a lock.

# Installation

```
Install-Package NCache.OSS.DistributedLock
```

or, using the .NET CLI:

```
dotnet add package NCache.OSS.DistributedLock
```

# What is Installed

Installing this package adds the following NCache configuration files to your project:

- **client.ncconf** — Defines the cache client settings, including the cache name and the list of server nodes the client connects to.
- **config.ncconf** — Contains the cache configuration (cache topology, partitions, eviction policy, etc.) used when the cache is created/registered.

# Prerequisites

- Targets **.NET Standard 2.0**.
- An NCache OSS **5.3.6.1** (or later) server should be up and running, with the target cache already created/registered on it.

# Getting Started

## Setting up NCache

Before running your application (If NCache is not installed on current machine), you need to update `client.ncconf` so the client knows which cache to connect to and where to find it:

1. Open `client.ncconf`.
2. Locate the cache entry named `myreplicatedcache` and rename it to the name of your actual cache (the cache you created/registered on the NCache server).
3. Under the `server` settings, update the server address to point to one of the nodes in the cluster where that cache is running.

Example:

```xml
<cache id="myreplicatedcache" ... >
  <server name="10.0.5.1" ... />
</cache>
```

Change `myreplicatedcache` → `YourCacheName`, and `10.0.5.1` → the IP/hostname of a node hosting `YourCacheName`.

## Setting up Package

1. Connect to your NCache cache and create a synchronization provider:

   ```csharp
   using Alachisoft.NCache.Client;
   using NCache.DistributedLock.Providers;

   ICache cache = CacheManager.GetCache("YourCacheName");

   var provider = new NCacheDistributedSynchronizationProvider(cache);
   ```

   `NCacheDistributedSynchronizationProvider` implements `IDistributedLockProvider`, `IDistributedSemaphoreProvider`, and `IDistributedReaderWriterLockProvider`, so it can create any of the three primitives below.

2. **Distributed Lock** — mutual-exclusion lock backed by an NCache key:

   ```csharp
   using NCache.DistributedLock.Locks;

   var distributedLock = new NCacheDistributedLock("my-resource-name", cache);
   // or: var distributedLock = provider.CreateLock("my-resource-name");

   using (var handle = distributedLock.Acquire(TimeSpan.FromSeconds(30)))
   {
       // critical section
   }
   // lock is released when the handle is disposed
   ```

3. **Distributed Semaphore** — bounded-count lock allowing up to `N` concurrent holders, with automatic cleanup of acquisitions left behind by crashed processes:

   ```csharp
   var semaphore = new NCacheDistributedSemaphore("my-resource-name", maxCount: 3, cache);
   // or: var semaphore = provider.CreateSemaphore("my-resource-name", 3);

   using (var handle = semaphore.Acquire(TimeSpan.FromSeconds(30)))
   {
       // up to 3 concurrent holders allowed
   }
   ```

4. **Distributed Reader-Writer Lock** — multiple concurrent readers or a single exclusive writer, with writers waiting for active readers to release before acquiring:

   ```csharp
   var rwLock = new NCacheDistributedReaderWriterLock("my-resource-name", cache);
   // or: var rwLock = provider.CreateReaderWriterLock("my-resource-name");

   using (var readHandle = rwLock.AcquireReadLock(TimeSpan.FromSeconds(30)))
   {
       // shared read access
   }

   using (var writeHandle = rwLock.AcquireWriteLock(TimeSpan.FromSeconds(30)))
   {
       // exclusive write access
   }
   ```

# Configuration

Connection to the cache (server list, timeouts, retries, security) is configured through `client.ncconf`, or programmatically via `CacheConnectionOptions` when calling `CacheManager.GetCache()`. No additional JSON or code-based configuration is required beyond obtaining the `ICache` instance shown above.

# Resources

- [NCache Integration Docs](https://www.alachisoft.com/resources/docs/ncache/prog-guide/idistributed-lock.html)
- [Client SDK Docs](https://www.alachisoft.com/resources/docs/ncache/prog-guide/client-side-api-programming.html)
- [Programmer's Guide](https://www.alachisoft.com/resources/docs/ncache/prog-guide/dot-net-third-party-integrations.html)
- [DistributedLock GitHub](https://github.com/madelson/DistributedLock)

# Technical Support

This is the Open Source (OSS) edition of NCache and is provided as is. Technical support is not provided for the OSS version.

If you encounter an issue, you are encouraged to investigate the source code, consult the available documentation, or engage with the open-source community. Feature requests and support services are available only with the Enterprise edition of NCache.

# License

Copyright © 2026 Alachisoft. All rights reserved.