# NCache NHibernate Second-Level Cache Provider

The `NHibernate.NCache.Opensource` NuGet package enables NHibernate applications to use NCache as a distributed second-level cache.

The integration implements NHibernate's `ICacheProvider` and `ICache` interfaces through `NCacheProvider` and NCache's cache implementation. This allows cached entities and query results to be shared across multiple application instances instead of remaining limited to individual NHibernate sessions or application processes.

## Package Versions

| **Package**                                 | **Version** |
| ------------------------------------------- | ----------- |
| `NHibernate.NCache.Opensource`              | 5.3.6.2     |
| `Alachisoft.NCache.Opensource.SDK`          | >= 5.3.6.2  |
| `NHibernate`                                | >= 5.6.0    |
| `System.Configuration.ConfigurationManager` | >= 8.0.0    |

## Overview

NHibernate provides a first-level cache for each session. Objects retrieved within a session are stored in that session's local cache and reused during the lifetime of the session. However, the first-level cache is not shared with other sessions and is discarded when the session closes.

NCache provides a distributed second-level cache for NHibernate. Cached entities can be shared across multiple sessions and application instances, reducing repetitive database access and improving application scalability.

NCache integrates with NHibernate by implementing the `ICacheProvider` and `ICache` interfaces. The provider is enabled through NHibernate configuration, while cache regions and NCache-specific settings are defined separately in *NCacheNHibernate.xml*.

The integration also supports query caching and database dependencies so cached entities can be invalidated when corresponding database records change.

## Key Features

- **Distributed Second-Level Cache:** Stores NHibernate entities in a distributed NCache cluster.
- **Cross-Session Caching:** Allows cached entities to be reused across multiple NHibernate sessions.
- **Web Farm Support:** Shares cached data across multiple application servers.
- **Multiple Cache Regions:** Supports multiple NHibernate cache regions with independent configuration.
- **Multiple NCache Instances:** Allows different regions to use different NCache caches.
- **Shared NCache Instance:** Allows multiple NHibernate regions to use the same NCache cache.
- **Expiration Policies:** Supports absolute, sliding, and no expiration on a region basis.
- **Cache Item Priority:** Supports configurable eviction priority for each region.
- **Query Caching:** Stores reusable NHibernate queries and their result information.
- **Database Synchronization:** Supports database dependencies for invalidating cached entities when database data changes.
- **Application Isolation:** Uses `application-id` to identify the configuration associated with each NHibernate application.
- **Configurable Error Handling:** Controls whether NCache exceptions are propagated through the NHibernate provider.
- **Synchronous and Asynchronous Operations:** Supports NHibernate synchronous and asynchronous query, flush, and eviction operations.

## What Is Installed

Installing `NHibernate.NCache.Opensource` adds the components required to configure NCache as an NHibernate second-level cache provider.

The package provides:

- `NCacheProvider`
- NCache implementation of the NHibernate cache interface
- NCache client libraries required to communicate with the cache cluster
- Support for NHibernate cache regions
- Support for query caching
- Support for database dependency configuration

The provider must be enabled in the NHibernate configuration, and NCache-specific region settings must be defined in *NCacheNHibernate.xml*.

## Prerequisites

Before using this package, ensure that you have:

1. **NHibernate Application:** An application configured to use NHibernate.
2. **NCache Installation:** NCache must be installed on the cache-server machines.
3. **Running Cache:** At least one NCache cache, such as `demoCache`, must already be created and running.
4. **Cache Connectivity:** The application must be able to communicate with the NCache servers.
5. **NHibernate Mappings:** Entities that need second-level caching must be marked as cacheable in their NHibernate mappings.
6. **Application ID:** A unique `ncache.application_id` must be configured for the application.
7. **NCacheNHibernate.xml:** Cache regions used by the application must be configured in *NCacheNHibernate.xml*.

## Installation

Install the Open Source NHibernate integration through the NuGet Package Manager Console:

```powershell
Install-Package NHibernate.NCache.Opensource
```

You can also install the package through the .NET CLI:

```bash
dotnet add package NHibernate.NCache.Opensource
```

Alternatively, install it through the Visual Studio NuGet Package Manager:

1. Right-click the NHibernate project in **Solution Explorer**.
2. Select **Manage NuGet Packages**.
3. Search for `NHibernate.NCache.Opensource`.
4. Select the package.
5. Select **Install**.

## Configure NCache as the Second-Level Cache Provider

Configure NCache as the NHibernate second-level cache provider in the application's NHibernate configuration.

```xml
<hibernate-configuration xmlns="urn:nhibernate-configuration-2.2">
  <session-factory>
    <property name="cache.provider_class">
      Alachisoft.NCache.Integrations.NHibernate.Cache.NCacheProvider,
      Alachisoft.NCache.Integrations.NHibernate.Cache
    </property>

    <property name="cache.use_second_level_cache">
      true
    </property>
  </session-factory>
</hibernate-configuration>
```

Setting `cache.use_second_level_cache` to `true` enables NHibernate second-level caching.

The `cache.provider_class` property registers NCache as the provider used by NHibernate.

## Configure the Application ID

NCache identifies each NHibernate application through a unique `application-id`.

Add the following setting to the application's *App.config* or *Web.config*:

```xml
<appSettings>
  <add key="ncache.application_id" value="myapp" />
</appSettings>
```

The value must match an `application-id` defined in *NCacheNHibernate.xml*.

Multiple NHibernate applications can define separate application configurations in the same *NCacheNHibernate.xml* file.

## Configure Cache Regions

NCache uses *NCacheNHibernate.xml* to configure NHibernate cache regions.

The file can be placed in the application's root directory or in the NCache configuration directory:

```text
%NCHOME%\config
```

The `application-id` must match the `ncache.application_id` configured in the application. The `default-region-name` must identify a region defined under `cache-regions`.

## Mark Entities for Second-Level Caching

Enabling second-level caching does not automatically cache every NHibernate entity.

Mark the required entity as cacheable in its mapping configuration:

```xml
<cache usage="read-write"
       region="AbsoluteExpirationRegion" />
```

The `region` identifies the NCache region configuration used for the entity.

If no region is specified, NHibernate uses the entity's fully qualified name as the region name and the provider falls back to the configured default region when required.

NHibernate supports the following caching concurrency strategies:

- `read-write`
- `nonstrict-read-write`
- `read-only`

## Enable Query Caching

NHibernate query caching stores query information so repeatedly executed queries can reuse cached results instead of repeatedly accessing the database.

Enable query caching in the NHibernate configuration:

```xml
<property name="cache.use_query_cache">
  true
</property>
```

Enabling query caching does not automatically cache every query. Individual queries must also be marked as cacheable.

For example:

```csharp
IQuery query =
    session.CreateQuery("from Customer c")
           .SetCacheable(true);
```

Query caching is most appropriate for queries whose results do not change frequently.

The query and the primary keys associated with its result set are maintained in NHibernate's standard query cache region:

```text
NHibernate.Cache.StandardQueryCache
```

Entities returned by the query are stored in their corresponding entity regions.

## Use NHibernate Queries

After NCache is configured, continue using standard NHibernate APIs.

For example:

```csharp
var customer =
    await session.CreateCriteria<Customer>()
        .Add(
            Restrictions.Eq(
                "CustomerID",
                customerId))
        .SetCacheable(true)
        .UniqueResultAsync<Customer>();
```

When a query is marked cacheable, NHibernate uses its query-cache behavior while NCache provides the distributed second-level storage.

## Configure Database Synchronization

NCache database dependencies allow cached NHibernate entities to be invalidated when corresponding records change in the underlying database.

Configure database dependencies inside the application's *NCacheNHibernate.xml* configuration:

```xml
<configuration>
  <application-config
      application-id="myapp"
      enable-cache-exception="true"
      default-region-name="default"
      key-case-sensitivity="false">

    <database-dependencies>

      <dependency
          entity-name="nhibernator.BLL.Customer"
          type="sql"
          sql-statement="SELECT ContactName FROM dbo.Customers WHERE CustomerID = ?"
          cache-key-format="NHibernateNCache:[en]#[pk]" />

    </database-dependencies>

  </application-config>
</configuration>
```

Each dependency is associated with an entity through its fully qualified name.

## Database Dependency Configuration

The following database dependency settings are supported:

| **Attribute**             | **Description** |
| ------------------------- | --------------- |
| `entity-name`             | Specifies the fully qualified name of the entity associated with the dependency. |
| `type`                    | Specifies the dependency type, such as `SQL`, `Oracle`, or `OleDB`.              |
| `sql-statement`           | Specifies the SQL statement used to create the database dependency.              |
| `cache-key-format`        | Specifies the key format generated for cached entities.                          |
| `composite-key-separator` | Specifies the separator used when an entity contains a composite primary key.    |

The cache key format can include:

- `[pk]` for the record's primary key
- `[en]` for the entity name

The default cache key format is:

```text
NHibernateNCache:[en]#[pk]
```

Each entity can have a maximum of one configured database dependency.

## Use Asynchronous Operations

NCache supports NHibernate asynchronous operations with the second-level cache.

### Query Asynchronously

```csharp
customers =
    await query.ListAsync<Customer>();
```

### Flush Asynchronously

```csharp
session.Save(customer);

await session.FlushAsync();
```

### Evict Asynchronously

Remove a specific entity:

```csharp
await factory.EvictAsync(
    typeof(Customer),
    customerId);
```

Or remove all cached instances of an entity type:

```csharp
await factory.EvictAsync(
    typeof(Customer));
```

Asynchronous operations can help keep applications responsive in high-concurrency scenarios.

## Use Synchronous Operations

Standard NHibernate synchronous operations can also be used with NCache.

### Query Synchronously

```csharp
var customers =
    query.List<Customer>();
```

### Flush Synchronously

```csharp
session.Save(customer);

session.Flush();
```

### Evict Synchronously

Remove a specific cached entity:

```csharp
factory.Evict(
    typeof(Customer),
    customerId);
```

Or remove all cached instances of an entity type:

```csharp
factory.Evict(
    typeof(Customer));
```

## How NHibernate Caching Works

When NHibernate uses NCache as its second-level cache:

1. NHibernate creates its normal first-level cache for each session.
2. NCache is registered as the application's second-level cache provider.
3. Entities marked as cacheable are associated with configured NHibernate regions.
4. Each region maps to an NCache cache and its configured expiration and priority settings.
5. When an entity is requested, NHibernate first checks the session-level cache.
6. If the entity is not available there, NHibernate can retrieve it from the NCache-backed second-level cache.
7. If the entity is not cached, NHibernate retrieves it from the database and can place it in the second-level cache.
8. Other sessions and application instances can then reuse the cached entity.
9. Query caching can reuse previously executed query results.
10. Database dependencies can invalidate cached entities when corresponding database records change.

This reduces repetitive database access while allowing cached NHibernate data to be shared across application instances.

## Run the Sample

An Open Source NHibernate sample is available in the NCache Samples repository:

[NCache NHibernate Sample](https://github.com/Alachisoft/NCache-Samples/tree/master/dotnet-framework/NHibernate/oss/NHibernate)

### Using Visual Studio

1. Clone or download the NCache Samples repository.
2. Open the NHibernate sample solution in Visual Studio.
3. Restore the NuGet packages.
4. Make sure that NCache is running.
5. Create and start the cache referenced by the sample's *NCacheNHibernate.xml* file.
6. Verify that the sample's `ncache.application_id` matches the corresponding `application-id`.
7. Verify that the database connection is available.
8. Build the solution.
9. Run the application.
10. Execute the sample operations to observe NHibernate second-level and query caching behavior.

### Using the Command Line

Restore the application dependencies:

```bash
dotnet restore
```

Build the application:

```bash
dotnet build
```

Run the application according to the target framework and sample configuration.

## Validation

Before running the application, verify the following:

- `NHibernate.NCache.Opensource` is installed.
- The configured NCache cache exists.
- The cache is running.
- The application can connect to the NCache cluster.
- `cache.provider_class` points to `NCacheProvider`.
- `cache.use_second_level_cache` is set to `true`.
- `ncache.application_id` is configured.
- The application ID matches an `application-id` in *NCacheNHibernate.xml*.
- The configured `default-region-name` exists in `cache-regions`.
- Each configured region references an existing NCache cache.
- Absolute or sliding expiration regions specify an expiration period greater than `0`.
- Entities that need second-level caching are marked cacheable in their NHibernate mappings.
- `cache.use_query_cache` is enabled before using NHibernate query caching.
- Queries that need caching are explicitly marked with `SetCacheable(true)`.
- Database dependency configuration uses the appropriate dependency type for the configured database.

If the application ID or required region configuration is missing, the provider cannot select the correct NCache NHibernate configuration.

If the referenced cache does not exist, is not running, or cannot be reached, second-level cache operations cannot be completed successfully.

## Best Practices

- Use the same NCache region configuration across application instances that participate in the same NHibernate deployment.
- Use separate region names for data requiring different expiration or eviction behavior.
- Use the same NCache cache for multiple regions when centralized cache management is preferred.
- Use separate caches when regions need independent capacity or configuration.
- Use query caching only for queries whose results do not change frequently.
- Mark only entities that benefit from second-level caching as cacheable.
- Select the NHibernate concurrency strategy according to how the entity is read and updated.
- Configure absolute expiration for data that should expire after a fixed period.
- Configure sliding expiration for frequently accessed data that should remain cached while active.
- Use database dependencies when cached entities must be invalidated after external database changes.
- Use asynchronous NHibernate operations in high-concurrency applications where blocking operations should be minimized.
- Keep `key-case-sensitivity` consistent with the underlying database.
- Use separate application IDs and cache configurations for unrelated applications.
- Use separate NCache configurations for development, testing, and production environments.
- Monitor the NCache cluster to ensure sufficient capacity for cached NHibernate entities and query results.

## Resources

- [NCache Documentation](https://www.alachisoft.com/resources/docs/)
- [NHibernate Second-Level Cache with NCache](https://www.alachisoft.com/resources/docs/ncache/prog-guide/ncache-as-nhibernate-second-level-cache.html)
- [NHibernate.NCache.Opensource](https://www.nuget.org/packages/NHibernate.NCache.Opensource)
- [NCache NHibernate Sample](https://github.com/Alachisoft/NCache-Samples/tree/master/dotnet-framework/NHibernate/oss/NHibernate)
- [NCache Open Source](https://github.com/Alachisoft/NCache)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)

## Technical Support

Alachisoft provides various technical support resources.

- Visit the [Alachisoft Support Center](https://www.alachisoft.com/support.html) to select a support resource appropriate for your issue.
- To request an additional feature or report a documentation discrepancy, contact [support@alachisoft.com](mailto:support@alachisoft.com).

## Copyrights

Copyright © 2026 Alachisoft. All rights reserved.
