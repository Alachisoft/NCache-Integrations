# NCache Output Cache Provider for ASP.NET

The `AspNet.OutputCache.NCache.Opensource` NuGet package enables classic ASP.NET applications to use NCache as a distributed Output Cache provider.

The provider implements `System.Web.Caching.OutputCacheProvider` and allows ASP.NET Web Forms and ASP.NET MVC applications running on .NET Framework to store generated page and action output in an NCache cluster instead of maintaining a separate output cache in each ASP.NET worker process.

## Package Versions

| **Package**                            | **Version** |
| -------------------------------------- | ----------- |
| `AspNet.OutputCache.NCache.Opensource` | 5.3.6.1     |
| `Alachisoft.NCache.SDK`                | >= 5.3.6.2  |

## Overview

ASP.NET Output Caching stores generated page or action responses and reuses them for subsequent requests. Different versions of a response can be cached based on request information such as query string parameters.

By default, ASP.NET Output Cache is maintained within individual ASP.NET worker processes. In a multi-server web farm, each application server therefore maintains its own output cache. Cached output can also be lost when a worker process terminates or an IIS application pool is recycled.

NCache provides a distributed, out-of-process ASP.NET Output Cache provider. Generated page and action responses are stored in an NCache cluster and can be shared by all application servers connected to the same cache.

`NOutputCacheProvider` integrates with the ASP.NET Output Cache pipeline. ASP.NET uses the provider to retrieve, add, update, and remove cached output while NCache provides the distributed storage.

This reduces repeated page rendering and database access while allowing cached output to remain available across application servers and IIS application pool recycles.

## Key Features

- **Distributed Output Caching:** Stores ASP.NET page and action output in a distributed NCache cluster.
- **Web Farm Support:** Allows multiple ASP.NET application servers to share the same cached responses.
- **Out-of-Process Storage:** Stores output outside individual ASP.NET worker processes.
- **Persistence across IIS Recycles:** Cached output remains available when a worker process terminates or an IIS application pool is recycled.
- **Scalability:** Allows cache capacity to grow by adding servers to the NCache cluster.
- **Reduced Page Processing:** Avoids repeatedly rendering pages or actions when a valid cached response is available.
- **Reduced Database Access:** Reuses generated output instead of repeating the database operations required to generate it.
- **Standard ASP.NET Integration:** Uses the standard ASP.NET Output Cache infrastructure.
- **Configuration-Based Deployment:** Registers NCache as the Output Cache provider through the application's *Web.config* file.
- **Configurable Error Handling:** Controls whether exceptions from NCache operations are propagated to the application.
- **Provider Logging:** Supports standard and detailed logging for provider operations.

## What Is Installed

Installing `AspNet.OutputCache.NCache.Opensource` adds the assemblies and dependencies required to connect the ASP.NET Output Cache pipeline to NCache.

The package includes:
- The `NOutputCacheProvider` implementation
- The NCache .NET client libraries
- The assemblies required to communicate with an NCache cluster
- Dependencies required by the provider

The provider must be registered separately in the application's *Web.config* file.

## Prerequisites

Before using this package, ensure that you have:

1. **Classic ASP.NET Application:** An ASP.NET Web Forms or ASP.NET MVC application targeting .NET Framework.
2. **Supported .NET Framework Version:** The application must target a .NET Framework version supported by the installed NCache release.
3. **NCache Installation:** NCache must be installed on the cache-server machines.
4. **Running Cache:** A cache, such as `demoCache`, must already be created and running.
5. **Cache Connectivity:** The ASP.NET application server must be able to communicate with the NCache servers.
6. **Serializable Data:** Data handled by the Output Cache provider must be serializable.
7. **Required Namespace:** The provider is available through the following namespace:

```csharp
using Alachisoft.NCache.OutputCacheProvider;
```

## Installation

Install the Open Source ASP.NET Output Cache provider through the NuGet Package Manager Console:

```powershell
Install-Package AspNet.OutputCache.NCache.Opensource
```

You can also install the package through the Visual Studio NuGet Package Manager:

1. Right-click the ASP.NET project in **Solution Explorer**.
2. Select **Manage NuGet Packages**.
3. Search for `AspNet.OutputCache.NCache.Opensource`.
4. Select the package.
5. Select **Install**.

## Configure the Output Cache Provider

Register NCache as the default ASP.NET Output Cache provider in the application's *Web.config* file.

Add the following configuration under the `<system.web>` element:

```xml
<system.web>
  <caching>
    <outputCache defaultProvider="NOutputCacheProvider">
      <providers>
        <add name="NOutputCacheProvider"
             type="Alachisoft.NCache.OutputCacheProvider.NOutputCacheProvider, Alachisoft.NCache.OutputCacheProvider, Version=x.x.x.x, Culture=neutral, PublicKeyToken=cff5926ed6a53769"
             cacheName="demoCache"
             exceptionsEnabled="false"
             enableDetailLogs="false"
             enableLogs="true"
             writeExceptionsToEventLog="false" />
      </providers>
    </outputCache>
  </caching>
</system.web>
```

Replace `Version=x.x.x.x` with the actual NCache version installed with the package.

The value assigned to `cacheName` must match the name of an existing and running NCache cache.

## Configuration Members

The ASP.NET Output Cache provider supports the following configuration members:

| **Member**          | **Required** | **Default** | **Description**                                                                                                                                                   |
| ------------------- | -----------: | ----------: | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `cacheName`         |          Yes |           — | Specifies the name of the NCache cache used for storing cached page and action output. If no cache name is specified, a configuration exception is thrown.        |
| `exceptionsEnabled` |           No |     `false` | Specifies whether exceptions from NCache operations are propagated to the application. Enabling this option can be useful during development and troubleshooting. |
| `enableLogs`        |           No |     `false` | Enables logging of important provider events, including initialization, disposal, and exceptions.                                                                 |
| `enableDetailLogs`  |           No |     `false` | Enables detailed logging containing information useful for debugging and troubleshooting.                                                                         |

## Enable Output Caching

After registering NCache as the default Output Cache provider, use the standard ASP.NET Output Cache functionality. No NCache-specific caching code is required in individual pages or controllers.

### ASP.NET Web Forms

Add the `OutputCache` directive to the page whose output should be cached:

```aspx
<%@ OutputCache VaryByParam="ID" Duration="300" %>
```

The `Duration` value is specified in seconds.

In this example, ASP.NET caches the generated output for 300 seconds and maintains a separate cached response for each value of the `ID` parameter.

To cache one version of a page regardless of request parameters:

```aspx
<%@ OutputCache Duration="60" VaryByParam="none" %>
```

### ASP.NET MVC

Apply the standard `OutputCache` attribute to an action:

```csharp
[OutputCache(Duration = 60, VaryByParam = "none")]
public ActionResult Index()
{
    return View();
}
```

ASP.NET passes the generated response to `NOutputCacheProvider`, which stores it in the configured NCache cache.

## How Output Caching Works

When a request is received:

1. ASP.NET determines the appropriate Output Cache entry based on the requested page or action and its configured variation parameters.
2. ASP.NET queries `NOutputCacheProvider` for an existing cached response.
3. If a matching cached response exists, it is returned without rendering the page or action again.
4. If no cached response exists, ASP.NET generates the response normally.
5. The generated output is passed to `NOutputCacheProvider`.
6. NCache stores the output in the configured distributed cache.
7. All application servers connected to the same NCache cache can retrieve the stored response.
8. The cached output remains available independently of the ASP.NET worker process that originally generated it.

This allows application servers in a web farm to use a shared distributed Output Cache instead of maintaining isolated per-process caches.

## Retrieve Output Cache Data

NCache associates ASP.NET Output Cache data with the `NC_ASP.net_output_data` tag.

You can retrieve Output Cache entries through this tag:

```csharp
using System.Collections;
using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Caching;

ICache cache = CacheManager.GetCache("demoCache");

Hashtable allOutputCacheData =
    cache.SearchService.GetByTag(
        new Tag("NC_ASP.net_output_data"));
```

This allows application code to locate data associated specifically with ASP.NET Output Caching.

## Custom Output Cache Hooks

NCache also provides Custom Output Cache Hooks that allow application-specific logic to modify cache item metadata before generated output is stored.

Custom hooks can be used to assign:
- Tags
- Named Tags
- Key dependencies
- A different expiration value at runtime

A custom hook implements the `IOutputCacheHook` interface. Its `OnCachingOutput` method is invoked after ASP.NET generates the output but before the provider stores it in NCache.

This provides an intermediate point where cache metadata or expiration can be modified according to application requirements.

> **NCache Open Source limitation:** Custom Output Cache Hooks are not supported by NCache Open Source. To use `IOutputCacheHook`, use the Enterprise `AspNet.OutputCache.NCache` package.

Do not configure `hookAssemblyName` or `hookClassName` when using `AspNet.OutputCache.NCache.Opensource`.

## Run the Sample

Because this provider targets classic ASP.NET on .NET Framework, run the sample through Visual Studio, IIS Express, or IIS.

### Using Visual Studio

1. Open the sample solution in Visual Studio.
2. Restore the NuGet packages.
3. Make sure that NCache is running.
4. Create and start the cache specified by `cacheName` in *Web.config*.
5. Verify that the application server can connect to the NCache cluster.
6. Build the solution.
7. Run the application using IIS Express or the configured IIS profile.
8. Open the application URL displayed by Visual Studio.

For example:

```text
https://localhost:44310/
```

The exact port may vary according to the sample project's IIS Express configuration.

Refresh the page multiple times to observe responses being served from the cache. After the configured Output Cache duration expires, ASP.NET generates a fresh response and stores it in NCache.

### Using the Command Line

For environments with NuGet and MSBuild installed, restore and build the solution with:

```powershell
nuget restore
msbuild /p:Configuration=Debug
```

Host the resulting application through IIS or IIS Express. `dotnet run` is not used because this provider targets classic ASP.NET and `System.Web`.

## Validation

Before running the application, verify the following:
- `AspNet.OutputCache.NCache.Opensource` is installed.
- The configured NCache cache exists.
- The cache is running.
- The application server can connect to the NCache cluster.
- The `cacheName` attribute is present in *Web.config*.
- The configured `cacheName` matches the running cache.
- The provider type and assembly name are correctly configured.
- The assembly version specified in *Web.config* matches the installed NCache version.
- The application targets a supported .NET Framework version.
- Data handled by the Output Cache provider is serializable.
- Custom Output Cache Hooks are not configured when using the Open Source package.

If `cacheName` is not specified, the provider throws a configuration exception.

If the configured cache does not exist, is not running, or cannot be reached, Output Cache operations cannot be completed successfully.

## Best Practices

- Use the same distributed cache across all application instances in a web farm.
- Use a cache name specific to the application or deployment environment.
- Set an Output Cache duration appropriate for how frequently the page content changes.
- Use `VaryByParam` when output differs according to request parameters.
- Set `exceptionsEnabled="true"` during development when detailed provider failures need to be diagnosed.
- Keep `exceptionsEnabled="false"` when cache failures should not be propagated to page output.
- Enable detailed logs only while troubleshooting.
- Ensure the NCache cluster has sufficient capacity for the expected volume of cached page and action output.
- Use separate cache configurations for development, testing, and production environments.
- Update the provider assembly version in *Web.config* when upgrading NCache.
- Do not configure Enterprise-only Custom Output Cache Hooks with the Open Source package.

## Resources

- [NCache Documentation](https://www.alachisoft.com/resources/docs/)
- [ASP.NET Output Cache Provider](https://www.alachisoft.com/resources/docs/ncache/prog-guide/aspnet-output-cache.html)
- [NCache Open Source](https://github.com/Alachisoft/NCache)
- [NCache NuGet Packages](https://www.nuget.org/profiles/NCache)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)

## Technical Support

Alachisoft provides various technical support resources.

- Visit the [Alachisoft Support Center](https://www.alachisoft.com/support.html) to select a support resource appropriate for your issue.
- To request an additional feature or report a documentation discrepancy, contact [support@alachisoft.com](mailto:support@alachisoft.com).

## Copyrights

Copyright © 2026 Alachisoft. All rights reserved.
