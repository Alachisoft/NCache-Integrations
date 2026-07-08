# NHibernate.NCache.Opensource

**NCache second-level cache provider for NHibernate**, turning NHibernate's per-session, in-process cache into a distributed cache that's kept in sync across every node in your application tier.

## Overview

NCache implements NHibernate's `ICacheProvider` and `ICache` interfaces (`NCacheProvider`, `NCache`), so an existing NHibernate application can switch its second-level cache to NCache through configuration alone � no code changes to your entities or repositories are required. Cache regions, expirations, and priorities are defined separately from your NHibernate mappings, and each application instance is identified by an `application-id` so multiple apps can share one NCache deployment without colliding.

## Package Versions

| Package | Version |
|---|---|
| NHibernate.NCache.Opensource | 5.3.6.2 |
| Alachisoft.NCache.Opensource.SDK | >= 5.3.6.2 |
| NHibernate | >= 5.6.0 |
| System.Configuration.ConfigurationManager | >= 8.0.0 |

## Installation

Install via NuGet Package Manager:

```bash
dotnet add package NHibernate.NCache.Opensource
```

Or via Package Manager Console:

```powershell
Install-Package NHibernate.NCache.Opensource
```

## Prerequisites

Before using this package, ensure you have:

1. **NCache Server** � a running NCache cluster
2. **Distributed Cache** � a cache created on the cluster whose name matches the `cache-name` referenced by your region configuration(s)
3. **NHibernate 5.6.0+** � an existing NHibernate application with mapped entities

## Setup

This integration needs a little more wiring than a typical cache provider, since regions and the application identity live in their own config file.

### 1. Plug NCacheProvider into NHibernate

In `hibernate.cfg.xml` (wherever `<session-factory>` is configured), point the second-level cache at NCache:

```xml
<hibernate-configuration xmlns="urn:nhibernate-configuration-2.2">
  <session-factory>
    <property name="cache.provider_class">Alachisoft.NCache.Integrations.NHibernate.Cache.NCacheProvider, Alachisoft.NCache.Integrations.NHibernate.Cache</property>
    <property name="cache.use_second_level_cache">true</property>
  </session-factory>
</hibernate-configuration>
```

### 2. Identify your application

Add an `appSettings` entry to `App.config`/`Web.config`:

```xml
<appSettings>
  <add key="ncache.application_id" value="myapp" />
</appSettings>
```

This key is required � the provider throws a `ConfigurationException` on startup if it's missing.

### 3. Define cache regions in NCacheNHibernate.xml

Create an `NCacheNHibernate.xml` file describing your regions. The provider searches for it in the application's root directory, `.\bin\`, `%NCHome%\config`, and the NCache install directory's `config` folder, in that order:

```xml
<configuration>
  <application-config application-id="myapp" enable-cache-exception="true" default-region-name="default" key-case-sensitivity="false">
    <cache-regions>
      <region name="default" cache-name="demoCache" priority="default" expiration-type="none" expiration-period="0"/>
      <region name="AbsoluteExpirationRegion" cache-name="demoCache" priority="High" expiration-type="sliding" expiration-period="180"/>
    </cache-regions>
  </application-config>
</configuration>
```

`default-region-name` must point to a region defined in the same file, or startup fails with a configuration error. Region attributes:

| Attribute | Description |
|---|---|
| `name` | Unique region identifier referenced from mappings or code |
| `cache-name` | The NCache cache this region's items are stored in |
| `priority` | Eviction priority: `Low`, `BelowNormal`, `Default`, `Normal`, `AboveNormal`, `NotRemovable` |
| `expiration-type` | `absolute`, `sliding`, or `none` |
| `expiration-period` | Required (and must be > 0) when expiration-type isn't `none` |

### 4. Mark what gets cached

Tag an entity's mapping file:

```xml
<cache usage="read-write" region="AbsoluteExpirationRegion"/>
```

or flag individual queries in code:

```csharp
var customer = await session.CreateCriteria<Customer>()
    .Add(Restrictions.Eq("CustomerID", customerId))
    .SetCacheable(true)
    .UniqueResultAsync<Customer>();
```

### 5. Build the session factory

```csharp
_sessionFactory = Fluently.Configure()
    .Database(MsSqlConfiguration.MsSql2008.ConnectionString(conn_str))
    .Mappings(m => m.FluentMappings.AddFromAssemblyOf<CustomerMap>())
    .ExposeConfiguration(cfg => cfg.Configure())   // loads hibernate.cfg.xml and wires up NCacheProvider
    .BuildSessionFactory();
```

From here, any query marked `.SetCacheable(true)` is transparently served from the distributed cache, and `_sessionFactory.Evict(typeof(Customer))` (or `EvictQueries()`) clears cached entries when you need to invalidate manually.

### 6. NHibernate Sample App

You can use the NHibernate integration against the sample app that exists in the NCache-Samples repository [NCache Samples](https://github.com/Alachisoft/NCache-Samples/tree/master/dotnet-framework/NHibernate/oss/NHibernate)

## License

Copyright � Alachisoft. All rights reserved.

This code and information is provided "as is" without warranty of any kind, either expressed or implied, including but not limited to the implied warranties of merchantability and fitness for a particular purpose. Use of NCache itself is subject to the license terms of the edition you've deployed (Open Source, Professional, or Enterprise).

## Resources

- [NCache Documentation](https://www.alachisoft.com/resources/docs/)
- [NHibernate Second-Level Cache Guide](https://www.alachisoft.com/resources/docs/ncache/prog-guide/ncache-as-nhibernate-second-level-cache.html)
- [NCache Open Source](https://github.com/Alachisoft/NCache)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)