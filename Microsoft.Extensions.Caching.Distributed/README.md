# NCache.Microsoft.Extensions.Caching.Opensource

**NCache Open Source implementation of `Microsoft.Extensions.Caching.Distributed.IDistributedCache`**, allowing ASP.NET Core applications to use NCache as the underlying distributed cache provider through the standard `IDistributedCache` interface. It integrates with ASP.NET Core dependency injection to provide distributed object caching with linear scalability and high availability.

## Package Versions

### .NET Framework 4.6.2

| Package                                              | Version    |
| ---------------------------------------------------- | ---------- |
| Alachisoft.NCache.Opensource.SDK                     | >= 5.3.6.2 |
| Microsoft.AspNetCore.DataProtection                  | >= 2.0.0   |
| Microsoft.AspNetCore.Http.Abstractions               | >= 2.0.0   |
| Microsoft.Extensions.Caching.Abstractions            | >= 2.0.0   |
| Microsoft.Extensions.Configuration.Abstractions      | >= 2.0.0   |
| Microsoft.Extensions.Configuration.Json              | >= 2.0.0   |
| Microsoft.Extensions.Options.ConfigurationExtensions | >= 2.0.0   |

### .NET Standard 2.0

| Package                                               | Version    |
| ----------------------------------------------------- | ---------- |
| Alachisoft.NCache.Opensource.SDK                      | >= 5.3.6.2 |
| Microsoft.AspNetCore.DataProtection.Abstractions      | >= 2.1.0   |
| Microsoft.AspNetCore.Http.Abstractions                | >= 2.1.0   |
| Microsoft.AspNetCore.Http.Features                    | >= 2.1.0   |
| Microsoft.AspNetCore.Session                          | >= 2.1.0   |
| Microsoft.Extensions.Caching.Abstractions             | >= 2.1.0   |
| Microsoft.Extensions.Configuration                    | >= 2.1.0   |
| Microsoft.Extensions.Configuration.Abstractions       | >= 2.1.0   |
| Microsoft.Extensions.Configuration.Json               | >= 2.1.0   |
| Microsoft.Extensions.DependencyInjection.Abstractions | >= 2.1.0   |
| Microsoft.Extensions.Options                          | >= 2.1.0   |
| Microsoft.Extensions.Options.ConfigurationExtensions  | >= 2.1.0   |
| Microsoft.Extensions.Primitives                       | >= 2.1.0   |

## Installation

Install via NuGet Package Manager:

```bash
dotnet add package NCache.Microsoft.Extensions.Caching.Opensource
```

Or via Package Manager Console:

```powershell
Install-Package NCache.Microsoft.Extensions.Caching.Opensource
```

## Prerequisites

Before using this package, ensure you have:

1. **NCache Server** – a running NCache cluster.
2. **An NCache cache** – created and running on the cluster.
3. **An ASP.NET Core application** using the `IDistributedCache` abstraction.
4. Include the `Alachisoft.NCache.Caching.Distributed` namespace in your application.
5. Ensure cached objects are serializable.

## Overview

`NCache.Microsoft.Extensions.Caching.Opensource` provides an implementation of `Microsoft.Extensions.Caching.Distributed.IDistributedCache`, enabling ASP.NET Core applications to use NCache as the underlying distributed cache provider without modifying existing application code.

The package integrates with ASP.NET Core dependency injection through the `AddNCacheDistributedCache` and `AddNCacheDistributedCacheProvider` extension methods. Configuration can be provided directly in code or through `appsettings.json`, supporting both single-cache and multiple-cache deployments.

**Key benefits:**

- Drop-in implementation of `IDistributedCache`
- Seamless integration with ASP.NET Core dependency injection
- Supports single-cache and multiple-cache configurations
- Configuration through code or `appsettings.json`
- Distributed in-memory caching with high availability and linear scalability

## Configuration

Register NCache as the application's distributed cache provider using one of the provided extension methods.

For a single cache:

```csharp
services.AddNCacheDistributedCache(configuration =>
{
    configuration.CacheName = "demoCache";
    configuration.EnableLogs = true;
    configuration.ExceptionsEnabled = true;
});
```

Or configure the cache in `appsettings.json` and register it using:

```csharp
services.AddNCacheDistributedCache(
    Configuration.GetSection("NCacheSettings"));
```

Multiple cache configurations are supported through the `AddNCacheDistributedCacheProvider` extension method.

The following configuration properties are available:

| Property | Description |
|----------|-------------|
| `CacheName` | Name of the NCache cache instance. |
| `EnableLogs` | Enables NCache logging. |
| `EnableDetailLogs` | Enables detailed debug logging. |
| `ExceptionsEnabled` | Controls whether cache exceptions are propagated to the application. |
| `WriteExceptionsToEventLog` | Writes cache exceptions to the operating system event log. |
| `RequestTimeout` | Timeout for client requests in seconds. |
| `OperationsRetry` | Number of retries for failed cache operations. |
| `OperationRetryInterval` | Time interval between retry attempts. |

## Usage

Once registered, NCache becomes the underlying implementation of `IDistributedCache`.

Applications continue using the standard `IDistributedCache` interface while NCache transparently manages distributed cache operations.

The integration provides the following registration APIs:

- `AddNCacheDistributedCache`
- `AddNCacheDistributedCacheProvider`

After registration, ASP.NET Core middleware such as Session automatically uses NCache as the backing distributed cache provider.

## License

Copyright © 2005-2026 Alachisoft. All rights reserved.

## Resources

- [NCache Documentation](https://www.alachisoft.com/resources/docs/)
- [NCache Open Source](https://github.com/Alachisoft/NCache)
- [Microsoft IDistributedCache Documentation](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.caching.distributed.idistributedcache)
- [NCache IDistributedCache Documentation](https://www.alachisoft.com/resources/docs/ncache/prog-guide/aspnetcore-sessions-ncache-idistributedcache-provider.html?tabs=net)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)