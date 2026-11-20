# NCache CacheManager.Core Integration

NCache integration for [CacheManager.Core](https://cachemanager.net/). This package adds an NCache cache handle (`NCacheCacheHandle<T>`) so NCache can serve as a distributed cache layer in a CacheManager pipeline, and an NCache backplane (`NCacheCacheBackplane`) that keeps the other cache layers in sync across application instances using NCache Pub/Sub.

## Installation

```
Install-Package NCache.OSS.CacheManager.Core
```

or, using the .NET CLI:

```
dotnet add package NCache.OSS.CacheManager.Core
```

# What is Installed

The package contains the integration assemblies. When configuring servers through files, supply the following NCache configuration files separately:

- **client.ncconf** — Defines the cache client settings, including the cache name and the list of server nodes the client connects to.
- **config.ncconf** — Contains the cache configuration (cache topology, partitions, eviction policy, etc.) used when the cache is created/registered.

## Prerequisites

- .NET Standard 2.0 or .NET Framework 4.7.2 compatible application.
- An NCache 5.3.603 (or later) server should be up and running, with the target cache already created/registered on it.

### Cache item serialization

The handle stores a serializable wrapper containing CacheManager metadata and
the original value. Object-typed caches restore the original value type;
keys, regions, timestamps, expiration settings, and the default-expiration flag
are retained. Values must support the configured NCache serialization format
and their types must be available to every application reading the cache.

The wrapper and region-aware keys change the stored item format and key layout.
Remove entries written by earlier versions before upgrading, and upgrade all
clients sharing those entries together.

## Getting Started

### Named configuration and fluent APIs

```csharp
using CacheManager.Core;
using NCache.OSS.CacheManager.Core;

using var cache = CacheFactory.Build<string>(settings => settings
    .WithNCacheConfiguration("ncache", builder => builder
        .WithCacheName("defaultCache")
        .WithEndpoint("20.200.20.44"))
    .WithNCacheCacheHandle("ncache", false));

cache.Put("test-key", "test-value");
System.Console.WriteLine(cache.Get("test-key"));
```

`WithEndpoint(ip, port)` accepts an IP address and defaults to port 9800. Call it
multiple times for multiple servers. Omit endpoints to use `client.ncconf`.
`WithNCacheConfiguration(key, options)` also accepts an existing `NCacheOptions`.

For a local layer synchronized through the OSS Pub/Sub backplane:

```csharp
using var cache = CacheFactory.Build<string>(settings =>
{
    settings.WithDictionaryHandle();
    settings.WithNCacheConfiguration("ncache", builder => builder
            .WithCacheName("defaultCache").WithEndpoint("20.200.20.44"))
        .WithNCacheBackplane("ncache", "my_topic")
        .WithNCacheCacheHandle("ncache", true);
});
```

`WithNCacheBackplane(key)` uses the existing default backplane channel behavior.
At least one handle must be a backplane source; `WithNCacheCacheHandle` defaults
its `isBackplaneSource` argument to `true`.

For DI registration, import `Microsoft.Extensions.DependencyInjection` and use
either `services.AddNCacheConfiguration("ncache", options => { ... })` or
`services.AddNCacheConfiguration("ncache", configuration.GetSection("ncache"))`.
The section binds `CacheName` and `ServerList` entries with `Ip` and `Port`.
Then use `WithNCacheCacheHandle("ncache", false)` when building the manager.

All registration APIs populate `NCacheConfigurations`, a process-wide named
registry, immediately. DI registration is not scoped to a service provider.
You can also call `NCacheConfigurations.AddConfiguration(key, options)` and
`GetConfiguration(key)` directly. Re-registering a key replaces it for subsequent
lookups. Handles and backplanes capture their options when added to a builder;
replacement does not reconfigure an existing manager. Register during startup
and do not mutate options while managers are in use.

Missing keys or invalid cache names, IP addresses, and ports fail before a
connection is opened. Existing `WithHandle` and `WithBackplane` calls that pass
`NCacheOptions` directly remain supported. The integration continues to use
the OSS SDK's `CacheConnectionOptions` and stored region indexes.

### Setting up NCache

The servers can be given in code (shown below). If you leave `ServerList` empty, the client reads them from a `client.ncconf` in your project; in that case, if NCache is not installed on the current machine, add one and set the `cache id` to your cache name:

```xml
<configuration>
  <ncache-server port="9800" />
  <cache id="defaultCache" enable-client-logs="False" log-level="error">
    <server name="10.0.0.1" />
  </cache>
</configuration>
```

Change `defaultCache` to the name of your actual cache (the cache you created/registered on the NCache server), and `10.0.0.1` to the IP/hostname of a node hosting it. If you do not list the servers in `NCacheOptions.ServerList` (shown below), the client reads them from `client.ncconf`.

### Setting up Package

Describe the connection once with an `NCacheOptions` instance, then add `NCacheCacheHandle<>` (and optionally `NCacheCacheBackplane`) to a CacheManager configuration:

```csharp
using CacheManager.Core;
using NCache.OSS.CacheManager.Core;

var options = new NCacheOptions
{
    CacheName = "defaultCache",
    ServerList = new List<NCacheOptions.ServerConfig>
    {
        new NCacheOptions.ServerConfig { Ip = "10.0.0.1", Port = 9800 }
    }
};

var cache = CacheFactory.Build<string>("myCache", settings =>
{
    settings.WithHandle(typeof(NCacheCacheHandle<>), "ncache", false, options);
});

cache.Add("greeting", "hello");
var value = cache.Get("greeting");
```

If `ServerList` is left empty, the client falls back to the servers listed in `client.ncconf`.

To combine NCache with an in-process layer and keep several application instances in sync, add a local handle, mark the NCache handle as the backplane source (`true`) and register the backplane. The third argument of `WithBackplane` is the NCache topic name used for the invalidation messages:

```csharp
var cache = CacheFactory.Build<string>("myCache", settings =>
{
    settings.WithDictionaryHandle();

    settings.WithHandle(typeof(NCacheCacheHandle<>), "ncache", true, options);

    settings.WithBackplane(typeof(NCacheCacheBackplane), "ncache", "my_topic", options);
}, loggerFactory);
```

Changes made through one manager are published to the topic, and every other manager subscribed to it evicts the stale entry from its local layer.

## Configuration

`NCacheOptions` describes the connection to NCache. The cache must already exist on the server.

| Property | Description |
|---|---|
| `CacheName` | Name of the NCache cache to connect to. Required. |
| `ServerList` | NCache server nodes to connect to (`Ip`, and `Port` which defaults to 9800). If empty, `client.ncconf` is used. |
| `Port` | Default NCache client port (9800). |

The integration provides:

- `NCacheCacheHandle<T>` — A `BaseCacheHandle<T>` implementation that performs the `ICacheManager` operations (add, put, get, update, remove, clear, exists) against NCache, with absolute and sliding expiration and region support.
- `NCacheCacheBackplane` — A `CacheBackplane` implementation that publishes change and remove notifications over an NCache topic so other instances can invalidate their local layers.
- `NCacheOptions` — Connection settings shared by the handle and the backplane.

Tag-based eviction is not supported. Regions are emulated with an index key per region, so clearing a region removes the keys recorded in that index.

## Resources

- [NCache Integration Docs](https://www.alachisoft.com/resources/docs/ncache/prog-guide/dot-net-third-party-integrations.html)
- [CacheManager](https://cachemanager.net/)
- [Programmer's Guide](https://www.alachisoft.com/resources/docs/ncache/prog-guide/)

## Technical Support

Alachisoft provides various technical support resources.

Visit the Alachisoft Support Center to select a support resource appropriate for your issue.

To request additional features or report a documentation discrepancy, contact support@alachisoft.com.

## License

Copyright © 2026 Alachisoft. All rights reserved.
