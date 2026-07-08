# NCache Integration for CacheManager.Core

**NCache implementation of CacheManager.Core cache handles and backplane**, allowing **CacheManager.Core** to use an NCache cache as its distributed cache handle while leveraging NCache's Messaging Service as a backplane for cache synchronization across multiple application instances.

## Package Versions

| Package | Version |
|---|---|
| Alachisoft.NCache.Opensource.SDK | >= 5.3.6.2 |
| CacheManager.Core | Compatible with CacheManager.Core |
| Microsoft.Extensions.Logging.Abstractions | Supported |

Targets `netstandard2.0`.

## Installation

```powershell
Install-Package NCache.OSS.CacheManager.Core
dotnet add package NCache.OSS.CacheManager.Core
```

## Prerequisites

Before using this package, ensure you have:

1. **NCache Server** – a running NCache cluster
2. **An NCache cache** – created on the cluster, matching the `CacheName` specified in `NCacheOptions`
3. **A CacheManager.Core instance** already configured in your application

## Overview

The NCache CacheManager.Core integration provides both a distributed cache handle and a cache synchronization backplane for CacheManager.Core.

The package includes:

- **`NCacheCacheHandle<T>`** — a `BaseCacheHandle` implementation that stores cache entries in an NCache cache
- **`NCacheCacheBackplane`** — a CacheManager backplane implementation that uses NCache's Messaging Service (Pub/Sub topics) to propagate cache invalidation events across multiple application instances

Cache connections are established through `CacheManager.GetCache` using the supplied `NCacheOptions`. When configured with a backplane, invalidation messages are published over an NCache topic, allowing all connected CacheManager instances to keep their local caches synchronized.

**Key benefits:**

- Native CacheManager.Core cache handle implementation backed by NCache
- Distributed cache synchronization using NCache Messaging Service
- Supports CacheManager CRUD operations
- Supports absolute and sliding expiration
- Configurable NCache connectivity through a single options class
- Drop-in integration with existing CacheManager.Core configuration

> **Limitations**
>
> - Tag-based eviction is **not supported**
> - Some advanced functionality may be limited by the NCache Open Source edition

## Configuration

NCache integration is configured by passing an `NCacheOptions` instance when registering the cache handle and backplane.

```csharp
var services = new ServiceCollection();

services.AddLogging(cfg =>
{
    cfg.AddConsole();
    cfg.SetMinimumLevel(LogLevel.Information);
});

var provider = services.BuildServiceProvider();
var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

var options = new NCacheOptions
{
    CacheName = "demoCache",
    ServerList = new List<NCacheOptions.ServerConfig>
    {
        new NCacheOptions.ServerConfig
        {
            Ip = "127.0.0.1"
        }
    }
};

var cache = CacheFactory.Build<string>("myCache", settings =>
{
    settings.WithDictionaryHandle();

    settings.WithHandle(
        typeof(NCacheCacheHandle<>),
        "cache_handle_name",
        true,
        options);

    settings.WithBackplane(
        typeof(NCacheCacheBackplane),
        "ncache_config_key",
        "example_topic_name",
        options);
},
loggerFactory);
```

`NCacheOptions` controls how the integration connects to NCache.

| Property | Description |
|---|---|
| `CacheName` | Name of the NCache cache instance. The cache must already exist in the cluster. |
| `ServerList` | List of NCache server nodes used to establish the connection. |
| `ServerConfig.Ip` | IP address or hostname of an NCache server. |
| `ServerConfig.Port` | Server port. Defaults to `9800` if not specified. |

## Usage

Register the NCache cache handle and backplane when configuring CacheManager.Core:

```csharp
var cache = CacheFactory.Build<string>("myCache", settings =>
{
    settings.WithDictionaryHandle();

    settings.WithHandle(
        typeof(NCacheCacheHandle<>),
        "cache_handle_name",
        true,
        options);

    settings.WithBackplane(
        typeof(NCacheCacheBackplane),
        "ncache_config_key",
        "example_topic_name",
        options);
},
loggerFactory);
```

Once configured:

- CacheManager CRUD operations are executed against the configured NCache cache.
- Absolute and sliding expiration policies are enforced by NCache.
- Cache invalidation notifications are propagated through the NCache backplane, allowing multiple application instances to maintain synchronized local caches.

## Validation

During initialization, the integration validates the supplied configuration before establishing a connection.

The following checks are performed:

- `NCacheOptions` must not be `null`
- `CacheName` must be specified
- Every `ServerList` entry must contain a valid IP address
- Port values, when specified, must be within the range `1–65535`

After validation, a cache connection is established using:

```csharp
CacheManager.GetCache(CacheName, CacheConnectionOptions)
```

If validation fails or the cache cannot be reached, an exception is thrown during initialization.

## Best Practices

- Create the NCache cache before starting your application.
- Configure `ServerList` for distributed deployments.
- Avoid caching excessively large objects.
- Use a dedicated NCache cluster for each environment.
- Configure the backplane when multiple application instances share the same cache.

## Sample

Demonstrates configuring **CacheManager.Core** to use **NCache** as a distributed cache handle and backplane. The sample shows how to configure `NCacheCacheHandle` and `NCacheCacheBackplane`, then perform common cache operations including CRUD operations, region-based caching, expiration, and cache synchronization.

Follow the following instructions to run the sample.

```bash
dotnet restore
dotnet run
```

The sample performs the following operations automatically:

- Configures CacheManager with an in-memory dictionary handle, an NCache distributed cache handle, and an NCache backplane.
- Adds an item with an absolute expiration.
- Retrieves the cached item.
- Checks whether the cached item exists.
- Removes the cached item.
- Adds multiple items to a cache region.
- Retrieves an item from the region.
- Checks whether the regional item exists.
- Clears the entire cache region.

Observe the console output to verify that each operation completes successfully and that cache operations are synchronized through the configured NCache backplane.

## Resources

- [NCache Documentation](https://www.alachisoft.com/resources/docs/)
- [CacheManager.Core](https://github.com/MichaCo/CacheManager)
- [NuGet Package NCache.OSS.CacheManager.Core](https://www.nuget.org/packages/NCache.OSS.CacheManager.Core)
- [NCache CacheManager.Core](https://www.alachisoft.com/resources/docs/ncache/prog-guide/cache-manager.html)
- [NCache Open Source](https://github.com/Alachisoft/NCache)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)

## Technical Support

Alachisoft© provides various sources of technical support.

- Please refer to http://www.alachisoft.com/support.html to select a support resource suitable for your issue.
- To request additional features in the future, or if you notice any discrepancy regarding this document, please drop an email to support@alachisoft.com.

## License

Copyright © 2026 Alachisoft. All rights reserved.