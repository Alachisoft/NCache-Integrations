# NCache Dapper Integration

NCache integration for Dapper. This package adds cache-aside query caching to Dapper's `IDbConnection` extension methods, using NCache as the distributed cache backing store.

## Installation

```
Install-Package NCache.OSS.Dapper
```

or, using the .NET CLI:

```
dotnet add package NCache.OSS.Dapper
```

## What is Installed

Installing this package adds the following NCache configuration files to your project:

- `client.ncconf` — Defines the cache client settings, including the cache name and the list of server nodes the client connects to.
- `config.ncconf` — Contains the cache configuration (cache topology, partitions, eviction policy, etc.) used when the cache is created/registered.

## Prerequisites

- .NET Standard 2.0 compatible application.
- An NCache 5.3.603 (or later) server should be up and running, with the target cache already created/registered on it.

## Getting Started

### Setting up NCache

Before running your application (if NCache is not installed on the current machine), you need to update `client.ncconf` so the client knows which cache to connect to and where to find it:

1. Open `client.ncconf`.
2. Locate the cache entry named `defaultCache` and rename it to the name of your actual cache (the cache you created/registered on the NCache server).
3. Under the server settings, update the server address to point to one of the nodes in the cluster where that cache is running.

Example:

```xml
<cache id="defaultCache" ... >
  <server name="10.0.0.1" ... />
</cache>
```

Change `defaultCache` → `YourCacheName`, and `10.0.0.1` → the IP/hostname of a node hosting `YourCacheName`.

### Setting up Package

`QueryCached` does not take a connection configuration instance directly as a parameter. Instead, it resolves its cache provider from a single, static registry (`DapperNCache.CacheProvider`). Build and connect an NCache `ICache` yourself using the NCache SDK, then register it once at application startup:

```csharp
var cache = CacheManager.GetCache("defaultCache", new CacheConnectionOptions
{
    ServerList = new List<ServerInfo>
    {
        new ServerInfo("127.0.0.1", 9800)
    }
});

DapperNCache.CacheProvider = new NCacheCacheProvider(cache);

// Optional: applied to every cached item that doesn't specify its own CacheOptions.Expiration
DapperNCache.DefaultExpiration = TimeSpan.FromMinutes(30);
DapperNCache.DefaultIsSliding = false;

// Optional: observe cache failures that are swallowed (ErrorEnabled = false)
DapperNCache.OnCacheError = (ex, context) => logger.LogWarning(ex, context);
```

Because `NCacheCacheProvider` takes an already-configured `ICache`, you're free to build that instance however you need — `CacheManager.GetCache` with any `CacheConnectionOptions`, a client config file, TLS settings, custom retry/timeout policies, etc.

With the provider registered, call `QueryCached` directly on any `IDbConnection`, the same way you'd call Dapper's own `Query`:

```csharp
using var connection = new SqlConnection(connectionString);

var users = connection.QueryCached<User>(
    "SELECT * FROM Users WHERE IsActive = @IsActive",
    new { IsActive = true },
    new CacheOptions { Expiration = TimeSpan.FromMinutes(5) });
```

A per-query `CacheOptions.Expiration` always overrides `DapperNCache.DefaultExpiration`; if neither is set, cached items never expire.

A custom provider can also be supplied per call instead of the globally registered one:

```csharp
var users = connection.QueryCached<User>(sql, param, options, cacheProvider: myCustomProvider);
```

## Configuration

Configure the connection to NCache by building your own `ICache` instance via the NCache SDK (e.g. `CacheManager.GetCache`) and passing it into `NCacheCacheProvider`. The cache must already exist on the server.

Configure per-query caching behavior through a `CacheOptions` instance.

| Property | Description |
|---|---|
| `Expiration` | Expiration for this cached item. Overrides `DapperNCache.DefaultExpiration` when set. |
| `IsSliding` | True for sliding expiration (resets TTL on cache hits); false for absolute expiration. |
| `ForceUpdate` | Bypasses any existing cache entry, re-executes the query against the database, and refreshes the cache. |
| `ErrorEnabled` | True to rethrow cache failures; false to log and fail open to the database. |
| `LockExpiration` | Expiration time for an acquired stampede lock |
| `LockTimeout` | The duration a caller will wait attempting to acquire the stampede lock before giving up. |


The integration provides:

- `QueryCached<T>` — `IDbConnection` extension methods that wrap a Dapper query with cache-aside behavior backed by NCache.
- `INCacheCacheProvider` — The provider abstraction (`Get`/`Set`/`Remove`) used by the extension methods; implement this to plug in a custom cache backend.
- `NCacheCacheProvider` — The default `INCacheCacheProvider` implementation, constructed from an already-configured NCache `ICache` instance.
- `DapperNCache` — Static registry holding the globally configured `CacheProvider`, `DefaultExpiration`, `DefaultIsSliding`, and the `OnCacheError` hook.

When a cached entry is missing or expired, the query is executed against the database and the result is written back to the cache under a key derived from the database name, SQL text, parameters, and result type, so identical queries share a cache entry and differently-shaped results never collide.

## Resources

- [NCache Integration Docs](https://www.alachisoft.com/resources/docs/ncache/prog-guide/client-side-aspnet-features.html)
- [Dapper](https://www.learndapper.com/)
- [Programmer's Guide](https://www.alachisoft.com/resources/docs/ncache/prog-guide/)

## Technical Support

Alachisoft provides various technical support resources.

Visit the Alachisoft Support Center to select a support resource appropriate for your issue.

To request additional features or report a documentation discrepancy, contact support@alachisoft.com.

## License

Copyright © 2026 Alachisoft. All rights reserved.