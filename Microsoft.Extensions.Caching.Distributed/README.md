# NCache Microsoft Extensions Caching

The `NCache.Microsoft.Extensions.Caching.Opensource` NuGet package enables ASP.NET Core applications to use NCache as the distributed implementation of `Microsoft.Extensions.Caching.Distributed.IDistributedCache`.

The integration registers NCache with ASP.NET Core dependency injection through the `AddNCacheDistributedCache` extension method. Applications can continue using the standard `IDistributedCache` interface while cached data is stored in a distributed NCache cluster.

## Package Versions

| **Package**                                            | **Version** |
| ------------------------------------------------------ | ----------- |
| `NCache.Microsoft.Extensions.Caching.Opensource`       | 5.3.6.2     |
| `Alachisoft.NCache.Opensource.SDK`                     | >= 5.3.6.2  |
| `Microsoft.AspNetCore.DataProtection`                  | >= 2.0.0    |
| `Microsoft.AspNetCore.Http.Abstractions`               | >= 2.0.0    |
| `Microsoft.Extensions.Caching.Abstractions`            | >= 2.0.0    |
| `Microsoft.Extensions.Configuration.Abstractions`      | >= 2.0.0    |
| `Microsoft.Extensions.Configuration.Json`              | >= 2.0.0    |
| `Microsoft.Extensions.Options.ConfigurationExtensions` | >= 2.0.0    |

## Overview

ASP.NET Core provides the `IDistributedCache` interface as a standard abstraction for distributed caching. Applications can use this interface without depending directly on a specific distributed cache implementation.

NCache provides an implementation of `IDistributedCache` that stores application data in a distributed NCache cluster. This allows cached data to be shared across multiple application instances instead of being maintained separately in each application's local memory.

The integration is registered through ASP.NET Core dependency injection using `AddNCacheDistributedCache`. Once configured, application components can request `IDistributedCache` and continue using its standard APIs while NCache handles the underlying distributed cache operations.

NCache also supports configuring multiple cache instances through `AddNCacheDistributedCacheProvider`.

## Key Features

- **IDistributedCache Implementation:** Implements the standard `Microsoft.Extensions.Caching.Distributed.IDistributedCache` interface.
- **Distributed Object Caching:** Stores application data in a distributed NCache cluster.
- **ASP.NET Core Integration:** Integrates with the standard ASP.NET Core dependency injection system.
- **Application Scalability:** Allows multiple application instances to share the same distributed cache.
- **Single-Cache Configuration:** Registers one NCache cache as the application's default `IDistributedCache`.
- **Multiple-Cache Configuration:** Supports configuring multiple NCache caches through `AddNCacheDistributedCacheProvider`.
- **Configuration-Based Registration:** Supports NCache configuration through *appsettings.json*.
- **Programmatic Registration:** Supports defining cache settings directly in application code.
- **ASP.NET Core Session Support:** Can serve as the distributed cache used by ASP.NET Core Session middleware.
- **Configurable Error Handling:** Controls whether NCache exceptions are propagated to the application.
- **Provider Logging:** Supports standard and detailed NCache logging.
- **Operation Retries:** Supports configurable retries and retry intervals for failed cache operations.

## What Is Installed

Installing `NCache.Microsoft.Extensions.Caching.Opensource` adds the components required to use NCache through the ASP.NET Core `IDistributedCache` abstraction.

The package provides:

- The NCache implementation of `IDistributedCache`
- The `AddNCacheDistributedCache` extension method
- The `AddNCacheDistributedCacheProvider` extension method
- NCache distributed cache configuration types
- NCache client libraries required to communicate with the cache cluster

NCache must be registered in the application's service collection before `IDistributedCache` can use it.

## Prerequisites

Before using this package, ensure that you have:

1. **ASP.NET Core Application:** An application using the `IDistributedCache` abstraction.
2. **NCache Installation:** NCache must be installed on the cache-server machines.
3. **Running Cache:** A cache, such as `demoCache`, must already be created and running.
4. **Cache Connectivity:** The application must be able to communicate with the NCache servers.
5. **Serializable Data:** Data stored through the NCache distributed cache provider must be serializable.
6. **Required Namespace:** Include the following namespace in the application:

```csharp
using Alachisoft.NCache.Caching.Distributed;
```

## Installation

Install the Open Source NCache `IDistributedCache` provider through the NuGet Package Manager Console:

```powershell
Install-Package NCache.Microsoft.Extensions.Caching.Opensource
```

You can also install the package through the .NET CLI:

```bash
dotnet add package NCache.Microsoft.Extensions.Caching.Opensource
```

Alternatively, install it through the Visual Studio NuGet Package Manager:

1. Right-click the ASP.NET Core project in **Solution Explorer**.
2. Select **Manage NuGet Packages**.
3. Search for `NCache.Microsoft.Extensions.Caching.Opensource`.
4. Select the package.
5. Select **Install**.

## Configure a Single Cache

Use `AddNCacheDistributedCache` to register one NCache cache as the application's `IDistributedCache` implementation.

You can configure the provider directly in application code or through *appsettings.json*.

### Configure through Program.cs

Register NCache directly in the service collection:

```csharp
builder.Services.AddNCacheDistributedCache(configuration =>
{
    configuration.CacheName = "demoCache";
    configuration.EnableLogs = true;
    configuration.ExceptionsEnabled = false;
});
```

For applications using `Startup.cs`:

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddNCacheDistributedCache(configuration =>
    {
        configuration.CacheName = "demoCache";
        configuration.EnableLogs = true;
        configuration.ExceptionsEnabled = false;
    });
}
```

The value assigned to `CacheName` must match an existing and running NCache cache.

### Configure through appsettings.json

Add the NCache configuration to *appsettings.json*:

```json
{
  "NCacheSettings": {
    "CacheName": "demoCache",
    "EnableLogs": true,
    "RequestTimeout": 90
  }
}
```

Register the configuration section in *Program.cs*:

```csharp
builder.Services.AddNCacheDistributedCache(
    builder.Configuration.GetSection("NCacheSettings"));
```

For an application using `Startup.cs`:

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddNCacheDistributedCache(
        Configuration.GetSection("NCacheSettings"));
}
```

This approach allows cache configuration to be changed without modifying application code.

## Configure Multiple Caches

Use `AddNCacheDistributedCacheProvider` when the application needs to configure multiple NCache caches.

Configure them directly in code:

```csharp
services.AddNCacheDistributedCacheProvider(options =>
{
    options.CacheConfigurations = new NCacheConfiguration[]
    {
        new NCacheConfiguration
        {
            CacheName = "demoClusteredCache",
            EnableLogs = true,
            ExceptionsEnabled = false
        },
        new NCacheConfiguration
        {
            CacheName = "demoCache",
            EnableLogs = true,
            ExceptionsEnabled = false
        }
    };
});
```

Multiple caches can also be defined in *appsettings.json*:

```json
{
  "NCacheFactorySettings": {
    "NCacheConfigurations": [
      {
        "CacheName": "demoClusteredCache",
        "EnableLogs": true,
        "RequestTimeout": 90
      },
      {
        "CacheName": "demoCache",
        "EnableLogs": true,
        "RequestTimeout": 90
      }
    ]
  }
}
```

Register the configuration section:

```csharp
services.AddNCacheDistributedCacheProvider(
    Configuration.GetSection("NCacheFactorySettings"));
```

## Configuration Properties

The NCache `IDistributedCache` provider supports the following configuration properties:

| **Property**                | **Required** | **Default** | **Description**                                                                                                                                    |
| --------------------------- | -----------: | ----------: | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CacheName`                 |          Yes |           — | Specifies the name of the NCache cache used by the distributed cache provider. If no cache name is specified, a configuration exception is thrown. |
| `EnableLogs`                |           No |     `false` | Enables NCache error logging for the distributed cache provider.                                                                                   |
| `EnableDetailLogs`          |           No |     `false` | Enables detailed debugging information in the NCache provider logs.                                                                                |
| `ExceptionsEnabled`         |           No |     `false` | Specifies whether exceptions from NCache operations are propagated to the application.                                                             |
| `WriteExceptionsToEventLog` |           No |     `false` | Specifies whether exceptions from NCache operations are written to the event log.                                                                  |
| `RequestTimeout`            |          Yes |        `90` | Specifies the timeout, in seconds, for client requests.                                                                                            |
| `OperationsRetry`           |           No |         `0` | Specifies the number of times an operation is retried if the connection is lost while the operation is executing.                                  |
| `OperationRetryInterval`    |           No |         `0` | Specifies the interval between operation retry attempts.                                                                                           |

Standard provider logs are created under:

```text
%NCHOME%\log-files\SessionState
```

on Windows, or:

```text
/opt/ncache/log-files/SessionState
```

on Linux.

## Use IDistributedCache

After registering NCache, inject the standard `IDistributedCache` interface into the application.

```csharp
using Microsoft.Extensions.Caching.Distributed;

public class ProductService
{
    private readonly IDistributedCache _cache;

    public ProductService(IDistributedCache cache)
    {
        _cache = cache;
    }
}
```

Use the standard `IDistributedCache` methods to store, retrieve, refresh, and remove data.

For example:

```csharp
public async Task StoreValueAsync(
    string key,
    string value)
{
    var options =
        new DistributedCacheEntryOptions()
            .SetAbsoluteExpiration(
                TimeSpan.FromMinutes(10));

    await _cache.SetStringAsync(
        key,
        value,
        options);
}
```

Retrieve the cached value:

```csharp
public async Task<string> GetValueAsync(
    string key)
{
    return await _cache.GetStringAsync(key);
}
```

Remove the cached entry:

```csharp
public async Task RemoveValueAsync(
    string key)
{
    await _cache.RemoveAsync(key);
}
```

The application continues using the standard Microsoft `IDistributedCache` APIs. NCache transparently performs the corresponding distributed cache operations.

## Use NCache with ASP.NET Core Session

ASP.NET Core Session uses `IDistributedCache` as its backing cache.

After registering NCache through `AddNCacheDistributedCache`, add Session services:

```csharp
builder.Services.AddNCacheDistributedCache(configuration =>
{
    configuration.CacheName = "demoCache";
});

builder.Services.AddSession();
```

Enable Session middleware in the application pipeline:

```csharp
app.UseSession();
```

ASP.NET Core Session then uses the registered NCache implementation of `IDistributedCache` to store session data.

## How the Distributed Cache Provider Works

When an application uses `IDistributedCache` with NCache:

1. NCache is registered in the ASP.NET Core service collection through `AddNCacheDistributedCache`.
2. ASP.NET Core resolves the NCache implementation when `IDistributedCache` is requested.
3. The application performs standard `IDistributedCache` operations.
4. NCache stores and retrieves the corresponding data from the configured distributed cache.
5. All application instances connected to the same cache can access the shared cached data.
6. Expiration and other options supplied through `DistributedCacheEntryOptions` are applied to the cached entry.
7. Applications can continue using the Microsoft caching abstraction without directly depending on NCache cache APIs.

This allows applications to use standard ASP.NET Core distributed caching patterns while gaining a shared distributed backing cache across multiple application instances.

## Run the Sample

An Open Source `IDistributedCache` sample is available in the NCache Samples repository:

[NCache IDistributedCache Sample](https://github.com/Alachisoft/NCache-Samples/tree/master/dotnet/IDistributedCache/oss)

### Using Visual Studio

1. Clone or download the NCache Samples repository.
2. Open the `IDistributedCache` sample in Visual Studio.
3. Restore the NuGet packages.
4. Make sure that NCache is running.
5. Create and start the cache configured by the sample.
6. Verify that the application can connect to the NCache cluster.
7. Build the application.
8. Run the sample.
9. Execute the available cache operations to verify that data is stored and retrieved through NCache.

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

Use the sample operations to verify distributed caching through `IDistributedCache`.

## Validation

Before running the application, verify the following:

- `NCache.Microsoft.Extensions.Caching.Opensource` is installed.
- The configured NCache cache exists.
- The cache is running.
- The application can connect to the NCache cluster.
- The `Alachisoft.NCache.Caching.Distributed` namespace is included.
- `AddNCacheDistributedCache` is registered when using a single cache.
- `AddNCacheDistributedCacheProvider` is registered when configuring multiple caches.
- `CacheName` matches an existing and running NCache cache.
- `RequestTimeout` is configured appropriately for the deployment.
- Data stored through the provider is serializable.
- Session services and `UseSession()` are configured when NCache is being used as the backing store for ASP.NET Core Session.

If no cache name is specified, the provider throws a configuration exception.

If the configured cache does not exist, is not running, or cannot be reached, the NCache distributed cache provider cannot complete cache operations.

## Best Practices

- Use the same distributed cache across application instances that need to share cached data.
- Use `AddNCacheDistributedCache` when the application requires one default distributed cache.
- Use `AddNCacheDistributedCacheProvider` when multiple cache configurations are required.
- Keep cache settings in *appsettings.json* when they may vary between deployment environments.
- Use appropriate expiration settings through `DistributedCacheEntryOptions`.
- Set `ExceptionsEnabled="true"` during development when detailed cache failures need to be diagnosed.
- Keep `ExceptionsEnabled="false"` when cache failures should not be propagated to the application.
- Enable detailed logs only while troubleshooting.
- Configure operation retries according to the application's connectivity and availability requirements.
- Avoid using excessively large values for distributed cache entries.
- Use separate cache configurations for development, testing, and production environments.
- Monitor the NCache cluster to ensure sufficient capacity for the expected distributed caching workload.

## Resources

- [NCache Documentation](https://www.alachisoft.com/resources/docs/)
- [NCache IDistributedCache Provider](https://www.alachisoft.com/resources/docs/ncache/prog-guide/aspnetcore-sessions-ncache-idistributedcache-provider.html?tabs=net)
- [NCache.Microsoft.Extensions.Caching.Opensource](https://www.nuget.org/packages/NCache.Microsoft.Extensions.Caching.Opensource)
- [Microsoft IDistributedCache Documentation](https://learn.microsoft.com/dotnet/api/microsoft.extensions.caching.distributed.idistributedcache)
- [NCache IDistributedCache Sample](https://github.com/Alachisoft/NCache-Samples/tree/master/dotnet/IDistributedCache/oss)
- [NCache Open Source](https://github.com/Alachisoft/NCache)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)

## Technical Support

Alachisoft provides various technical support resources.

- Visit the [Alachisoft Support Center](https://www.alachisoft.com/support.html) to select a support resource appropriate for your issue.
- To request an additional feature or report a documentation discrepancy, contact [support@alachisoft.com](mailto:support@alachisoft.com).

## Copyrights

Copyright © 2005–2026 Alachisoft. All rights reserved.
