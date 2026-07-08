# NCache Entity Framework Core

## Overview

**NCache Entity Framework Core integration** extends Entity Framework Core with distributed query caching capabilities, allowing applications to cache query results in NCache using LINQ extension methods. It supports both transactional data and reference data caching while reducing database load and improving application performance.

The package provides synchronous and asynchronous extension methods that integrate directly with `IQueryable`, enabling applications to cache query results without modifying existing LINQ queries.

**Key benefits:**

- Distributed query caching for Entity Framework Core
- Synchronous and asynchronous query caching APIs
- Supports both transactional and reference data caching
- Flexible caching strategies for storing query results
- Reduces repetitive database queries and improves application scalability

## Package Versions

### .NET 8.0, NET 6.0

| Package                                  | Version    |
| ---------------------------------------- | ---------- |
| Alachisoft.NCache.Opensource.SDK         | >= 5.3.6.2 |
| Microsoft.EntityFrameworkCore.Relational | >= 8.0.16  |
| Microsoft.Extensions.Caching.Memory      | >= 8.0.1   |

### .NET Standard 2.0, NET Framework 4.6.2

| Package                                  | Version    |
| ---------------------------------------- | ---------- |
| Alachisoft.NCache.Opensource.SDK         | >= 5.3.6.2 |
| Microsoft.EntityFrameworkCore.Relational | >= 3.1.0   |
| Microsoft.Extensions.Caching.Memory      | >= 3.1.0   |

## Prerequisites

Before using this package, ensure you have:

1. **NCache Server** – a running NCache cluster.
2. **An NCache cache** – created and running on the cluster.
3. **EntityFrameworkCore.NCache** (Enterprise) or **EntityFrameworkCore.NCache.OpenSource** installed.
4. Include the following namespaces in your application:
   - `Alachisoft.NCache.EntityFrameworkCore`
   - `Alachisoft.NCache.Runtime.Caching`
5. Ensure the entities being cached are serializable.

## Configuration

The integration is configured by installing the appropriate Entity Framework Core provider package and invoking the provided LINQ extension methods on `IQueryable` instances.

Caching behavior is controlled through the `CachingOptions` class, which allows developers to configure how query results are stored in NCache.

| Property | Description |
|----------|-------------|
| `StoreAs.Collection` | Stores the complete query result under a single cache key. |
| `StoreAs.SeparateEntities` | Stores each entity individually using separate cache keys, enabling fine-grained cache updates. |
| `bulkInsertChunkSize` | Controls the number of entities inserted per bulk operation. Defaults to `1000` and is recommended for large datasets. |
| `errorEnabled` | Determines whether cache update failures should propagate exceptions. Defaults to `false`. |

## Query Caching APIs

NCache provides the following Entity Framework Core extension methods.

| Synchronous | Asynchronous |
|-------------|--------------|
| `FromCache` | `FromCacheAsync` |
| `LoadIntoCache` | `LoadIntoCacheAsync` |
| `FromCacheOnly` | `FromCacheOnlyAsync` |

### FromCache

`FromCache` executes the LINQ query against the database on the first request and stores the resulting dataset in NCache. Subsequent executions of the same query retrieve the result directly from the cache, reducing database round-trips.

This API is recommended for **transactional data**, where database updates occur frequently but repeated reads can benefit from caching.

### LoadIntoCache

`LoadIntoCache` executes the query and stores the result in NCache without returning it directly to the application. It is intended for pre-loading frequently accessed datasets into cache before application requests are received.

This API is commonly used for **reference data** and cache warm-up scenarios.

### FromCacheOnly

`FromCacheOnly` retrieves data exclusively from NCache and never queries the database. If the requested data does not exist in cache, an empty result is returned.

This API requires entities to be stored using `StoreAs.SeparateEntities` and appropriate query indexes to be configured.

## Query Caching

NCache supports two strategies for storing Entity Framework Core query results:

- **Collection** – stores the complete query result as a single cache entry.
- **SeparateEntities** – stores each entity individually, allowing finer-grained cache updates and queries.

For large datasets, bulk insertion can be optimized by configuring `bulkInsertChunkSize`, allowing entities to be cached in manageable chunks while avoiding connection timeouts.

## Entity Framework Core Sample App

You can use the Entity Framework Core integration against the sample app that exists in the NCache-Samples repository [NCache Samples](https://github.com/Alachisoft/NCache-Samples/tree/master/dotnet/EFCoreCaching/oss)

## References

Reference documentation is available at:\
https://www.alachisoft.com/resources/docs/ncache/prog-guide/entity-framework-core-caching.html


## Resources

- [NCache Documentation](https://www.alachisoft.com/resources/docs/)
- [NCache Open Source](https://github.com/Alachisoft/NCache)
- [EFCore Nuget](https://www.nuget.org/packages/EntityFrameworkCore.NCache.OpenSource)
- [NCache EntityFramework Core Sample](https://www.alachisoft.com/resources/docs/ncache/prog-guide/entity-framework-core-caching.html)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)

## Technical Support

Alachisoft&copy; provides various sources of technical support.

- Please refer to http://www.alachisoft.com/support.html to select a support resource you find suitable for your issue.
- To request additional features in the future, or if you notice any discrepancy regarding this document, please drop an email to [support@alachisoft.com](mailto:support@alachisoft.com).

## Copyrights

Copyright 2026 Alachisoft&copy;