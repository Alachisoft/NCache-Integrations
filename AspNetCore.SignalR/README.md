# NCache SignalR for ASP.NET Core

The `AspNetCore.SignalR.NCache.OpenSource` NuGet package enables ASP.NET Core SignalR applications to use NCache as a distributed backplane.

The integration extends SignalR's `ISignalRServerBuilder` interface through the `AddNCache` extension method. This allows ASP.NET Core SignalR applications running on multiple application servers to synchronize real-time updates through an NCache cluster.

## Package Versions

| **Package**                            | **Version**            |
| -------------------------------------- | ---------------------- |
| `AspNetCore.SignalR.NCache.OpenSource` | >= 5.3.6.2             |
| `Alachisoft.NCache.Opensource.SDK`     | >= 5.3.6.2             |
| ASP.NET Core SignalR                   | 1.1.0                  |

## Overview

ASP.NET Core SignalR enables servers to send real-time updates to connected clients as soon as an event occurs. This eliminates the need for clients to repeatedly poll the server or refresh a webpage to receive updated information.

In a web farm, SignalR clients may be connected to different application servers. Without a distributed backplane, an update generated on one server may not reach clients connected to another server.

NCache provides an integration for ASP.NET Core SignalR that synchronizes the participating application servers. Each server registers NCache through the `AddNCache` extension method and uses the same cache name and application-specific event key.

ASP.NET Core SignalR manages client connections, hubs, broadcasting, groups, and message delivery. NCache provides the distributed synchronization required when SignalR applications are deployed across multiple servers.

This allows clients connected to different application servers to receive real-time updates regardless of the server to which they are connected.

> For production deployments, it is recommended to run the NCache backplane in the same data center as the ASP.NET Core SignalR application.

## Key Features

- **Distributed SignalR Backplane:** Synchronizes ASP.NET Core SignalR updates across multiple application servers.
- **Web Farm Support:** Allows clients connected to different application servers to receive synchronized updates.
- **Real-Time Communication:** Supports immediate server-to-client communication without repeated client polling.
- **Horizontal Scalability:** Supports scaling SignalR applications across multiple application servers.
- **Standard SignalR Integration:** Extends the existing `ISignalRServerBuilder` interface.
- **Simple Registration:** Registers NCache through the `AddNCache` extension method.
- **Application-Specific Event Key:** Uses a common event key to identify updates belonging to the same SignalR application.
- **Configurable Cache Connections:** Supports NCache client connection settings through `ConnectionOptions`.
- **Configuration-Based Registration:** Supports settings through *appsettings.json*.
- **Programmatic Registration:** Supports multiple `AddNCache` overloads according to application requirements.

## What Is Installed

Installing `AspNetCore.SignalR.NCache.OpenSource` adds the components required to integrate NCache with ASP.NET Core SignalR.

The package provides:
- The `AddNCache` extension method for `ISignalRServerBuilder`
- `NCacheConfiguration` for SignalR backplane configuration
- NCache client connection support
- The assemblies required to communicate with an NCache cluster

The NCache backplane must be registered separately when ASP.NET Core SignalR is added to the application's service collection.

## Prerequisites

Before using this package, ensure that you have:

1. **ASP.NET Core SignalR Application**: An ASP.NET Core application using SignalR Core 1.1.0.
2. **Supported .NET Version**: The application must target a .NET version supported by the installed NCache release.
3. **NCache Installation**: NCache must be installed on the cache-server machines.
4. **Running Cache**: A cache, such as `demoCache`, must already be created and running.
5. **Cache Connectivity**: Every application server must be able to communicate with the NCache servers.
6. **Serializable Data**: Data transmitted through the SignalR integration must be serializable.
7. **Required Namespaces**: Include the following namespaces in the application:

```csharp
using Alachisoft.NCache.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR;
```

## Installation

Install the Open Source ASP.NET Core SignalR integration through the NuGet Package Manager Console:

```powershell
Install-Package AspNetCore.SignalR.NCache.OpenSource
```

For NCache Enterprise, install:

```powershell
Install-Package AspNetCore.SignalR.NCache
```

You can also install the Open Source package through the Visual Studio NuGet Package Manager:

1. Right-click the ASP.NET Core project in **Solution Explorer**.
2. Select **Manage NuGet Packages**.
3. Search for `AspNetCore.SignalR.NCache.OpenSource`.
4. Select the package.
5. Select **Install**.

## Configure NCache through appsettings.json

Add an `NCacheConfiguration` section to the application's *appsettings.json* file:

```json
{
  "NCacheConfiguration": {
    "CacheName": "demoCache",
    "EventKey": "chatApplication",
    "ConnectionOptions": {
      "ClientBindIP": "",
      "AppName": "DemoAppName",
      "LogLevel": "info",
      "ServerList": [
        {
          "Name": "20.200.20.40",
          "Port": 9800
        }
      ]
    }
  }
}
```

The cache specified by `CacheName` must already exist and be running.

If a connection property is not specified in `ConnectionOptions` or at the root level of `NCacheConfiguration`, the corresponding default value is obtained from *client.ncconf*.

## Configuration Properties

The ASP.NET Core SignalR integration uses the following configuration properties:

| **Property**        | **Required** | **Description**|
| ------------------- | -----------: | -------------- |
| `CacheName`         |          Yes | Specifies the name of the NCache cache used by the SignalR integration.                                                                            |
| `EventKey`          |          Yes | Specifies the unique application-specific key used to identify SignalR updates. All instances of the same application must use the same event key. |
| `ConnectionOptions` |           No | Specifies NCache client connection settings used when connecting to the cache.                                                                     |
| `ServerList`        |           No | Specifies one or more NCache servers used for cache connectivity.                                                                                  |
| `AppName`           |           No | Specifies the application name reported to NCache.                                                                                                 |
| `ClientBindIP`      |           No | Specifies the local IP address used by the NCache client connection.                                                                               |
| `LogLevel`          |           No | Specifies the NCache client logging level.                                                                                                         |

Connection settings that are not explicitly configured use the applicable defaults from *client.ncconf*.

## Register NCache as the SignalR Backplane

Register NCache on the `ISignalRServerBuilder` returned by `AddSignalR()`.

### Using Cache Name and Event Key

The first `AddNCache` overload accepts the cache name and event key:

```csharp
public static ISignalRServerBuilder AddNCache(
    this ISignalRServerBuilder signalRBuilder,
    string cacheName,
    string eventKey);
```

For an application using `Startup.cs`:

```csharp
public class Startup
{
    public IConfiguration Configuration { get; }

    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        string cacheName =
            Configuration["NCacheConfiguration:CacheName"];

        string eventKey =
            Configuration["NCacheConfiguration:EventKey"];

        services.AddSignalR()
            .AddNCache(cacheName, eventKey);
    }
}
```

The application reads the cache name and event key from *appsettings.json* and registers NCache when SignalR services are added.

### Using NCacheConfiguration

You can also configure NCache through an `Action<NCacheConfiguration>`:

```csharp
public static ISignalRServerBuilder AddNCache(
    this ISignalRServerBuilder signalRBuilder,
    Action<NCacheConfiguration> configure);
```

Register this overload as follows:

```csharp
public class Startup
{
    public IConfiguration Configuration { get; }

    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.Configure<NCacheConfiguration>(
            Configuration.GetSection("NCacheConfiguration"));

        services.AddSignalR()
            .AddNCache(ncacheOptions =>
            {
                ncacheOptions.CacheName =
                    Configuration["NCacheConfiguration:CacheName"];

                ncacheOptions.EventKey =
                    Configuration["NCacheConfiguration:EventKey"];
            });
    }
}
```

This approach is useful when the application's NCache settings are maintained through configuration and need to be applied during service registration.

## Configure SignalR Endpoints

After registering the NCache integration, configure the SignalR endpoint in the application.

For applications using the ASP.NET Core `Startup` model:

```csharp
public void Configure(IApplicationBuilder app)
{
    app.UseStaticFiles();

    app.UseSignalR(config =>
    {
        config.MapHub<MessageHub>("/messages");
    });

    app.UseMvc();
}
```

NCache is configured during service registration through `AddNCache`, while ASP.NET Core SignalR remains responsible for mapping hubs and managing connected clients.

## Use SignalR

After NCache has been registered, continue using the standard ASP.NET Core SignalR APIs. No NCache-specific code is required inside the SignalR hub.

For example:

```csharp
using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

public class MessageHub : Hub
{
    public async Task SendMessage(
        string user,
        string message)
    {
        await Clients.All.SendAsync(
            "ReceiveMessage",
            user,
            message);
    }
}
```

When a SignalR operation occurs, NCache synchronizes the participating application servers so that updates can reach clients connected through different servers.

## How the SignalR Integration Works

When ASP.NET Core SignalR is used with NCache:

1. Each ASP.NET Core application server registers NCache through `AddNCache`.
2. The same cache name and application-specific event key are configured across the application instances.
3. SignalR clients connect to their respective application servers.
4. NCache stores the item associated with the configured event key.
5. Client activity updates the state associated with that event key.
6. NCache synchronizes the participating application servers.
7. Each SignalR server delivers the corresponding updates to its connected clients.

This enables real-time communication across a web farm without requiring every client to connect to the same application server.

## Run the Sample

An Open Source ASP.NET Core SignalR chat sample is available in the NCache Samples repository:

[AspNetCore.SignalR.NCache Sample](https://github.com/Alachisoft/NCache-Samples/tree/master/dotnet/SignalRChat/oss)

### Using Visual Studio

1. Clone or download the NCache Samples repository.
2. Open the ASP.NET Core SignalR sample solution in Visual Studio.
3. Restore the NuGet packages.
4. Make sure that NCache is running.
5. Create and start the cache configured by the sample.
6. Verify that the configured NCache server addresses are accessible.
7. Build the solution.
8. Run the application.
9. Open the application in multiple browser windows to test real-time message delivery.

For a web-farm test, run separate instances of the application and configure each instance to use the same cache name and event key.

### Using the Command Line

Restore the application dependencies:

```bash
dotnet restore
```

Run the application:

```bash
dotnet run
```

Open the application URL displayed in the terminal and use multiple clients to verify real-time communication.

## Validation

Before running the application, verify the following:

- The configured NCache cache exists.
- The cache is running.
- Every application server can connect to the NCache cluster.
- `AspNetCore.SignalR.NCache.OpenSource` is installed.
- The `Alachisoft.NCache.AspNetCore.SignalR` namespace is included.
- ASP.NET Core SignalR 1.1.0 is being used as specified by the integration documentation.
- `CacheName` matches the running cache.
- `EventKey` is configured.
- Every instance of the same SignalR application uses the same cache name.
- Every instance of the same SignalR application uses the same event key.
- `AddNCache` is chained onto `AddSignalR()`.
- Configured NCache server addresses are accessible.
- Data transmitted through the integration is serializable.

If the cache does not exist, is not running, or cannot be reached, NCache cannot initialize the SignalR integration.

## Best Practices

- Run the NCache backplane in the same data center as the ASP.NET Core SignalR application.
- Use the same cache name and event key across all instances of the same application.
- Use a unique event key for unrelated SignalR applications.
- Register NCache through `AddNCache` when configuring SignalR services.
- Configure multiple NCache server addresses for distributed deployments where appropriate.
- Use an application-specific value for `AppName`.
- Keep NCache connection settings consistent across application instances.
- Ensure that data transmitted through the SignalR integration is serializable.
- Test the integration across multiple application instances before deploying to production.
- Monitor the NCache cluster to ensure sufficient capacity for the expected SignalR traffic.
- Use separate cache configurations for development, testing, and production environments.

## Resources

- [NCache Documentation](https://www.alachisoft.com/resources/docs/)
- [ASP.NET Core SignalR](https://www.alachisoft.com/resources/docs/ncache/prog-guide/asp-net-core-signalr.html)
- [NCache SignalR Core Integration](https://www.alachisoft.com/resources/docs/ncache/prog-guide/ncache-extension-signalr-core.html)
- [ASP.NET Core SignalR Sample](https://github.com/Alachisoft/NCache-Samples/tree/master/dotnet/SignalRChat/oss)
- [NCache Open Source](https://github.com/Alachisoft/NCache)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)

## Technical Support

Alachisoft provides various technical support resources.

- Visit the [Alachisoft Support Center](https://www.alachisoft.com/support.html) to select a support resource appropriate for your issue.
- To request an additional feature or report a documentation discrepancy, contact [support@alachisoft.com](mailto:support@alachisoft.com).

## Copyrights

Copyright © 2026 Alachisoft. All rights reserved.
