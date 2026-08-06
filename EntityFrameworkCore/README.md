# NCache Entity Framework Core

The `EntityFrameworkCore.NCache.OpenSource` NuGet package enables Entity Framework Core applications to use NCache as a distributed second-level cache.

The integration extends Entity Framework Core with synchronous and asynchronous LINQ extension methods for caching database query results, preloading reference data, querying cached entities without accessing the database, and directly managing cached entities through an EF Core `DbContext`.

## Package Versions

| **Package**                                | **Version**            |
| ------------------------------------------ | ---------------------- |
| `EntityFrameworkCore.NCache.OpenSource`    | Current NCache version |
| `Alachisoft.NCache.Opensource.SDK`         | >= 5.3.6.2             |
| `Microsoft.EntityFrameworkCore.Relational` | >= 8.0.16              |
| `Microsoft.Extensions.Caching.Memory`      | >= 8.0.1               |

## Overview

Entity Framework Core abstracts relational database access through object models and LINQ queries. In high-transaction applications, repeated database queries can become a performance and scalability bottleneck.

NCache integrates with Entity Framework Core as a distributed second-level cache. Query results can be stored in NCache and reused by subsequent requests, reducing database access and improving application response times.

The integration provides different caching approaches according to the type of application data:

- `FromCache` is intended for transactional data where queries normally access the database but repeated results can be served from cache.
- `LoadIntoCache` can preload data from the database into NCache.
- `FromCacheOnly` retrieves entities directly from NCache without accessing the database.
- `GetCache` provides a cache handle from the EF Core `DbContext` for direct cache operations.

Synchronous and asynchronous variants are available for the supported query-caching operations.

## Key Features

- **Distributed Second-Level Cache:** Stores Entity Framework Core query results in a distributed NCache cluster.
- **Reduced Database Access:** Serves repeated query results from cache instead of repeatedly querying the database.
- **Transactional Data Caching:** Uses `FromCache` and `FromCacheAsync` for frequently read and updated data.
- **Reference Data Caching:** Uses `LoadIntoCache` and `FromCacheOnly` for data that can be preloaded and served directly from cache.
- **Synchronous and Asynchronous APIs:** Supports both synchronous and asynchronous cache operations.
- **Flexible Storage Strategies:** Stores result sets either as a collection or as separate entities.
- **Direct Cache Access:** Provides a cache handle from the EF Core `DbContext` through `GetCache`.
- **Cache-Only Operations:** Supports direct insertion, removal, and invalidation without modifying the database.
- **Database Synchronization:** Supports database dependency configuration to invalidate cached data when the underlying database changes.
- **Expiration and Priority:** Supports cache item expiration and priority through `CachingOptions`.
- **Query Identification:** Supports `QueryIdentifier` for grouping and invalidating related cached entities.
- **Deferred Query Caching:** Supports deferred aggregate and element operators whose results can be cached.
- **Configurable Logging:** Integrates with `Microsoft.Extensions.Logging` and provides a default NCache logger.
- **Bulk Caching:** Supports configurable chunking when large result sets are inserted into the cache.

## What Is Installed

Installing `EntityFrameworkCore.NCache.OpenSource` adds the assemblies and dependencies required to integrate Entity Framework Core queries with NCache.

The package provides:

- Entity Framework Core caching extension methods
- `NCacheConfiguration`
- `CachingOptions`
- `FromCache` and `FromCacheAsync`
- `LoadIntoCache` and `LoadIntoCacheAsync`
- `FromCacheOnly` and `FromCacheOnlyAsync`
- `GetCache` for direct cache access through `DbContext`
- Query Deferred APIs
- Logging integration
- NCache client libraries required to communicate with the cache cluster

NCache must be configured before the EF Core caching extension methods are used.

## Prerequisites

Before using this package, ensure that you have:

1. **Entity Framework Core Application:** An application configured to use Entity Framework Core.
2. **Supported .NET Version:** The application must target a .NET or .NET Framework version supported by the installed package.
3. **NCache Installation:** NCache must be installed on the cache-server machines.
4. **Running Cache:** A cache, such as `demoCache`, must already be created and running.
5. **Cache Connectivity:** The application must be able to communicate with the NCache servers.
6. **Serializable Entities:** Entities stored in NCache must be serializable.
7. **Required Namespaces:** Include the following namespaces in the application:

```csharp
using Alachisoft.NCache.EntityFrameworkCore;
using Alachisoft.NCache.Runtime.Caching;
```

## Installation

Install the Open Source Entity Framework Core integration through the NuGet Package Manager Console:

```powershell
Install-Package EntityFrameworkCore.NCache.OpenSource
```

You can also install the package through the .NET CLI:

```bash
dotnet add package EntityFrameworkCore.NCache.OpenSource
```

Alternatively, install it through the Visual Studio NuGet Package Manager:

1. Right-click the EF Core project in **Solution Explorer**.
2. Select **Manage NuGet Packages**.
3. Search for `EntityFrameworkCore.NCache.OpenSource`.
4. Select the package.
5. Select **Install**.

## Configure EF Core Cache

NCache must be configured before the Entity Framework Core caching extension methods are used.

### Mark Entities as Serializable

Entities stored in NCache must be serializable.

```csharp
[Serializable]
public partial class Customers
{
    // Properties
}
```

Mark each entity that can be stored in NCache with `[Serializable]`.

## Configure NCache in DbContext

Use `NCacheConfiguration.Configure` in the application's `DbContext` to specify the cache and database dependency configuration.

```csharp
public partial class NorthwindContext : DbContext
{
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder)
    {
        string cacheId =
            ConfigurationManager.AppSettings["CacheId"];

        string connString =
            ConfigurationManager.AppSettings["ConnString"];

        bool errorEnabled =
            bool.Parse(
                ConfigurationManager.AppSettings["ErrorEnabled"]);

        int bulkInsertChunkSize =
            int.Parse(
                ConfigurationManager.AppSettings["BulkInsertChunkSize"]);

        var connectionOptions =
            new CacheConnectionOptions();

        connectionOptions.RetryInterval =
            TimeSpan.FromSeconds(3);

        connectionOptions.ConnectionRetries = 2;

        connectionOptions.ServerList =
            new List<ServerInfo>
            {
                new ServerInfo("20.200.20.11", 9800)
            };

        NCacheConfiguration.Configure(
            cacheId,
            DependencyType.SqlServer,
            connectionOptions,
            errorEnabled,
            bulkInsertChunkSize);

        optionsBuilder.UseSqlServer(connString);
    }
}
```

> **Important:** NCache must be configured through `NCacheConfiguration.Configure` before the EF Core caching APIs are used. Otherwise, an exception is thrown indicating that the NCache initialization configuration has not been provided.

## Configuration Options

`NCacheConfiguration` provides the following primary configuration options:

| **Member**            | **Description**|
| --------------------- | ---------------------------------- |
| `CacheId`             | Specifies the name of the NCache cache used by the Entity Framework Core application.                                |
| `DatabaseType`        | Specifies the database dependency type, such as `SqlServer` or `Other`.                                              |
| `InitParams`          | Specifies cache connection settings through `CacheConnectionOptions`.                                                |
| `errorEnabled`        | Specifies whether cache-related failures for caching operations should propagate exceptions. The default is `false`. |
| `bulkInsertChunkSize` | Specifies the number of entities inserted into the cache per bulk chunk. The default is `1000`.                      |

When `DependencyType.SqlServer` is configured, SQL dependency requires SQL Server Service Broker and the required schema configuration.

Entity Framework Core does not support Oracle through this integration.

## Configure SQL Dependency

When SQL Server dependency is used, configure the default database schema in the EF Core model:

```csharp
protected override void OnModelCreating(
    ModelBuilder modelBuilder)
{
    modelBuilder.HasDefaultSchema("dbo");

    // Remaining entity model configuration
}
```

SQL Server Service Broker must also be enabled for SQL dependency-based invalidation.

When the underlying database data changes, NCache can invalidate the affected cached query data so that subsequent requests retrieve fresh results.

## Cache Query Results with FromCache

`FromCache` is intended for transactional data where the application needs database-backed query results while benefiting from distributed caching.

On the first request, the query result is obtained and stored in NCache. Subsequent executions can retrieve the result from the cache, avoiding an unnecessary database trip.

```csharp
using (var context = new NorthwindContext())
{
    var options = new CachingOptions
    {
        StoreAs = StoreAs.SeparateEntities
    };

    var resultSet =
        (from customer in context.Customers
         where customer.CustomerId == someCustomerId
         select customer)
        .FromCache(options)
        .ToList();
}
```

The asynchronous equivalent is `FromCacheAsync`:

```csharp
using (var context = new NorthwindContext())
{
    var options = new CachingOptions
    {
        StoreAs = StoreAs.SeparateEntities
    };

    var resultSet =
        await context.Customers
            .Where(
                customer =>
                    customer.CustomerId ==
                    someCustomerId)
            .FromCacheAsync(options);
}
```

Asynchronous APIs do not return a cache key through an `out` parameter because asynchronous method signatures do not support `out` parameters.

## Configure Query Storage

NCache supports two storage strategies through `CachingOptions.StoreAs`.

### Store as a Collection

Use `StoreAs.Collection` to store the complete result set as one cache entry:

```csharp
var options = new CachingOptions
{
    StoreAs = StoreAs.Collection
};
```

This is useful when the result set is normally retrieved and managed as one unit.

### Store as Separate Entities

Use `StoreAs.SeparateEntities` to store each entity separately:

```csharp
var options = new CachingOptions
{
    StoreAs = StoreAs.SeparateEntities
};
```

This provides finer-grained cache management because individual entities can be updated, queried, or removed separately.

For large result sets, `bulkInsertChunkSize` divides the entities into smaller bulk operations. The default value is `1000`.

Using asynchronous APIs is recommended when large datasets are being cached because the application receives the response after the complete result set has been processed.

## Cache Reference Data

For data that is read frequently and changes less often, NCache provides `LoadIntoCache` and `FromCacheOnly`.

### Load Data with LoadIntoCache

`LoadIntoCache` retrieves data from the database and loads it into NCache so that it can subsequently be served from the distributed cache.

```csharp
using (var context = new NorthwindContext())
{
    var options = new CachingOptions
    {
        StoreAs = StoreAs.Collection
    };

    var resultSet =
        (from order in context.Orders
         where order.Customer.CustomerId ==
               someCustomerId
         select order)
        .LoadIntoCache(
            out string cacheKey,
            options);
}
```

To store entities separately:

```csharp
using (var context = new NorthwindContext())
{
    var options = new CachingOptions
    {
        StoreAs = StoreAs.SeparateEntities
    };

    var resultSet =
        (from order in context.Orders
         where order.Customer.CustomerId ==
               someCustomerId
         select order)
        .LoadIntoCache(options);
}
```

The asynchronous equivalent is `LoadIntoCacheAsync`.

## Query Only the Cache with FromCacheOnly

`FromCacheOnly` queries entities already stored in NCache without accessing the database.

```csharp
using (var context = new NorthwindContext())
{
    var resultSet =
        (from customer in context.Customers
         where customer.CustomerId ==
               someCustomerId
         select customer)
        .FromCacheOnly();
}
```

If the requested entity does not exist in NCache, the database is not queried and the returned result is empty.

> **Important:** `FromCacheOnly` requires entities to be stored using `StoreAs.SeparateEntities`.

The entities must also be indexed before they can be queried through `FromCacheOnly`.

The asynchronous equivalent is `FromCacheOnlyAsync`.

## Access NCache through DbContext

The `GetCache` extension method provides direct access to the NCache cache associated with an EF Core context.

```csharp
using (var context = new NorthwindContext())
{
    Cache cache = context.GetCache();

    // Perform cache-only operations
}
```

The context must remain active while cache-only operations are performed. Using the cache wrapper after the associated `DbContext` has been disposed results in an `ObjectDisposedException`.

If NCache has not been initialized, `GetCache` and subsequent cache operations fail.

## Insert Entities Directly into Cache

Use `Insert` to add an entity directly to NCache without querying the database:

```csharp
using (var context = new NorthwindContext())
{
    var customer = new Customers
    {
        CustomerId = "HANIH",
        ContactName = "Hanih Moos",
        ContactTitle = "Sales Representative",
        CompanyName = "Blauer See Delikatessen"
    };

    var options = new CachingOptions
    {
        QueryIdentifier = new Tag("CustomerEntity"),
        Priority = Runtime.CacheItemPriority.Default
    };

    Cache cache = context.GetCache();

    cache.Insert(
        customer,
        out string cacheKey,
        options);
}
```

`Insert` stores entities as separate entities. If the entity already exists in NCache, the existing cached entity is updated.

The generated cache key can be retained for subsequent cache operations.

## Remove Cached Entities

Use `Remove` to remove an entity from NCache without removing it from the database.

```csharp
using (var context = new NorthwindContext())
{
    Cache cache = context.GetCache();

    cache.Remove(cacheKey);
}
```

An entity instance can also be supplied:

```csharp
cache.Remove(customer);
```

Removing stale cache data allows a subsequent `FromCache` or `LoadIntoCache` operation to retrieve fresh data from the database.

## Remove by Query Identifier

`QueryIdentifier` can group related cached entities so that they can be invalidated together.

```csharp
using (var context = new NorthwindContext())
{
    var options = new CachingOptions
    {
        QueryIdentifier =
            new Tag("CustomerEntity")
    };

    Cache cache = context.GetCache();

    cache.RemoveByQueryIdentifier(
        options.QueryIdentifier);
}
```

This removes the matching entities from NCache without modifying the underlying database.

## Configure Caching Options

`CachingOptions` controls how EF Core results and entities are stored in NCache.

For example:

```csharp
var options = new CachingOptions
{
    QueryIdentifier = "CustomerEntity",
    CreateDbDependency = true,
    StoreAs = StoreAs.SeparateEntities,
    Priority = Runtime.CacheItemPriority.High
};

options.SetAbsoluteExpiration(
    DateTime.Now.AddSeconds(20));
```

Common options include:

| **Option**           | **Description**                                                                     |
| -------------------- | ----------------------------------------------------------------------------------- |
| `StoreAs`            | Determines whether results are stored as one collection or as separate entities.    |
| `QueryIdentifier`    | Identifies related cached entities for tracking or bulk invalidation.               |
| `CreateDbDependency` | Enables database dependency when supported by the configured database type.         |
| `Priority`           | Specifies the NCache cache item priority.                                           |
| Absolute Expiration  | Removes the cached item after the configured absolute time.                         |
| Sliding Expiration   | Extends the lifetime of a cached item according to access activity when configured. |

## Use Query Deferred APIs

Immediate LINQ resolution methods such as aggregate and element operators normally resolve the query before it can be passed to the caching extension.

NCache provides Query Deferred APIs that defer execution so the resulting value itself can be cached.

Supported aggregate operators include:
- `DeferredAverage`
- `DeferredCount`
- `DeferredMin`
- `DeferredMax`
- `DeferredSum`

For example:

```csharp
var result =
    database.Products
        .Select(product => product.UnitPrice)
        .DeferredAverage()
        .FromCache(options);
```

Supported element operators for `FromCache` include:
- `DeferredElementAtOrDefault`
- `DeferredFirst`
- `DeferredFirstOrDefault`
- `DeferredLast`
- `DeferredLastOrDefault`
- `DeferredSingle`
- `DeferredSingleOrDefault`

Additional deferred operators include `DeferredAll`, `DeferredLongCount`, and `DeferredContains`.

Deferred query results are values rather than entities, so they cannot be stored as separate entities.

## Configure Logging

NCache EF Core integration uses `Microsoft.Extensions.Logging` for logging query caching and provider activity.

Configure the default NCache logger through `NCacheConfiguration.ConfigureLogger`:

```csharp
public partial class NorthwindContext : DbContext
{
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder)
    {
        NCacheConfiguration.ConfigureLogger(
            logLevel: LogLevel.Trace);
    }
}
```

To use the default logging configuration:

```csharp
NCacheConfiguration.ConfigureLogger();
```

By default, NCache logs at the `Debug` level.

Default logs are written to:

```text
%NCHOME%\log-files
```

on Windows, or:

```text
/opt/ncache/log-files
```

on Linux.

A custom `ILoggerFactory` can also be supplied to `ConfigureLogger` when the application needs to use its own logging provider.

## How EF Core Caching Works

When an EF Core query uses NCache:

1. The application configures NCache through `NCacheConfiguration`.
2. The EF Core query uses one of the NCache extension methods.
3. NCache determines how the result should be processed according to the selected API and `CachingOptions`.
4. `FromCache` checks NCache and uses the database when the required result is not available.
5. `LoadIntoCache` loads the required database data into NCache.
6. `FromCacheOnly` queries only entities already stored in NCache.
7. Query results can be stored as a collection or as separate entities.
8. Database dependencies, expiration, priority, and query identifiers can be applied where configured.
9. Subsequent requests can use cached data instead of repeatedly querying the database.

This reduces database load while giving applications control over how transactional and reference data are stored and invalidated.

## Run the Sample

An Open Source Entity Framework Core sample is available in the NCache Samples repository:

[NCache Entity Framework Core Sample](https://github.com/Alachisoft/NCache-Samples/tree/master/dotnet/EFCoreCaching/oss)

### Using Visual Studio

1. Clone or download the NCache Samples repository.
2. Open the Entity Framework Core caching sample in Visual Studio.
3. Restore the NuGet packages.
4. Make sure that NCache is running.
5. Create and start the cache configured by the sample.
6. Verify that the configured database is available.
7. Verify that the application can connect to the NCache cluster.
8. Build the application.
9. Run the sample and execute the available EF Core caching scenarios.

### Using the Command Line

Restore the application dependencies:

```bash
dotnet restore
```

Build the application:

```bash
dotnet build
```

Run the application:

```bash
dotnet run
```

Use the available sample operations to verify database access and cached query behavior.

## Validation

Before running the application, verify the following:

- `EntityFrameworkCore.NCache.OpenSource` is installed.
- The configured NCache cache exists.
- The cache is running.
- The application can connect to the NCache cluster.
- `Alachisoft.NCache.EntityFrameworkCore` is included where the caching extension methods are used.
- `Alachisoft.NCache.Runtime.Caching` is included where NCache caching types are required.
- Entities stored in NCache are serializable.
- `NCacheConfiguration.Configure` is called before the caching extension methods are used.
- `CacheId` matches an existing and running NCache cache.
- The selected `DependencyType` matches the database configuration.
- SQL Server Service Broker is enabled when SQL dependency is used.
- The required EF Core entities are indexed before using `FromCacheOnly`.
- `StoreAs.SeparateEntities` is used for entities queried through `FromCacheOnly`.
- The associated `DbContext` remains active while using a cache handle returned by `GetCache`.

If NCache is not configured, the EF Core caching operations fail because no cache initialization configuration is available.

## Best Practices

- Use `FromCache` for transactional data that is frequently queried and periodically updated.
- Use `LoadIntoCache` to preload frequently requested reference data.
- Use `FromCacheOnly` when the application should retrieve data exclusively from NCache without accessing the database.
- Store data as `SeparateEntities` when individual entity updates, queries, or invalidation are required.
- Store data as a `Collection` when the complete result set is normally retrieved and invalidated together.
- Use asynchronous APIs when caching large datasets.
- Adjust `bulkInsertChunkSize` for large result sets to avoid oversized bulk operations.
- Keep the `DbContext` active while performing direct cache operations through `GetCache`.
- Use `QueryIdentifier` to group related cached entities that may need to be invalidated together.
- Configure database dependency when cached data must automatically reflect supported database changes.
- Enable `errorEnabled` according to whether cache-update failures should interrupt application execution.
- Configure logging while diagnosing caching, connectivity, or query synchronization issues.
- Use separate cache configurations for development, testing, and production environments.
- Monitor the NCache cluster to ensure sufficient capacity for the expected EF Core caching workload.

## Resources

- [NCache Documentation](https://www.alachisoft.com/resources/docs/)
- [Entity Framework Core Caching with NCache](https://www.alachisoft.com/resources/docs/ncache/prog-guide/entity-framework-core-caching.html)
- [EntityFrameworkCore.NCache.OpenSource](https://www.nuget.org/packages/EntityFrameworkCore.NCache.OpenSource)
- [NCache Entity Framework Core Sample](https://github.com/Alachisoft/NCache-Samples/tree/master/dotnet/EFCoreCaching/oss)
- [NCache Open Source](https://github.com/Alachisoft/NCache)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)

## Technical Support

Alachisoft provides various technical support resources.

* Visit the [Alachisoft Support Center](https://www.alachisoft.com/support.html) to select a support resource appropriate for your issue.
* To request an additional feature or report a documentation discrepancy, contact [support@alachisoft.com](mailto:support@alachisoft.com).

## Copyrights

Copyright © 2026 Alachisoft. All rights reserved.
