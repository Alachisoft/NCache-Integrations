# NCache SignalR for ASP.NET

The `AspNet.SignalR.NCache.OpenSource` NuGet package enables classic ASP.NET SignalR applications to use NCache as a distributed backplane.

The integration extends SignalR's `IDependencyResolver` interface through the `UseNCache` extension method. This allows ASP.NET SignalR applications running on multiple web servers to synchronize messages through an NCache cluster.

## Package Versions

| **Package**                        | **Version** |
| ---------------------------------- | ----------- |
| `Alachisoft.NCache.Opensource.SDK` | 5.3.6.2     |
| `Microsoft.AspNet.SignalR.Core`    | 2.4.3       |

## Overview

ASP.NET SignalR enables servers to send real-time updates to connected clients as soon as an event occurs. This removes the need for clients to repeatedly poll the server or refresh a webpage to receive updated information.

In a web farm, SignalR clients may be connected to different application servers. Without a distributed backplane, a message generated on one server may not reach clients connected to another server.

NCache provides a distributed ASP.NET SignalR backplane that synchronizes the participating application servers. Each server registers NCache through the `UseNCache` extension method and uses the same cache name and application-specific event key.

When a client operation occurs, NCache uses custom events and cache item versioning to notify the other application servers. Each server can then broadcast the update to its locally connected SignalR clients.

This allows all clients to receive real-time updates regardless of the application server to which they are connected.

## Key Features

- **Distributed SignalR Backplane:** Synchronizes ASP.NET SignalR messages across multiple application servers.
- **Web Farm Support:** Allows clients connected to different web servers to receive the same updates.
- **Real-Time Updates:** Delivers server-generated updates without requiring client-side polling.
- **Horizontal Scalability:** Supports adding more ASP.NET application servers as application traffic increases.
- **Standard SignalR Integration:** Extends SignalR's existing `IDependencyResolver` interface.
- **Simple Registration:** Registers NCache through the `UseNCache` extension method.
- **Custom Event Synchronization:** Uses NCache events to notify participating application servers.
- **Cache Item Versioning:** Detects updates through the version of the cache item associated with the event key.
- **Configurable Cache Connections:** Supports server addresses, load balancing, retries, timeouts, and client logging.
- **Configuration-Based Deployment:** Reads cache connection settings from the application's *Web.config* file.

## What Is Installed

Installing `AspNet.SignalR.NCache.OpenSource` adds the assemblies and dependencies required to register NCache as the ASP.NET SignalR backplane.

The package includes:
- The `UseNCache` extension method for `IDependencyResolver`
- The NCache SignalR message bus implementation
- `NCacheScaleoutConfiguration`
- `SignalRConnectionOptions`
- The NCache .NET client libraries
- The assemblies required to communicate with an NCache cluster

The NCache backplane must be registered separately in the application's OWIN `Startup` class.

## Prerequisites

Before using this package, ensure that you have:

1. **Classic ASP.NET SignalR Application**: An ASP.NET application using ASP.NET SignalR 2.4.0 or later.
2. **Supported .NET Framework Version**: The application must target a .NET Framework version supported by the installed NCache release.
3. **NCache Installation**: NCache must be installed on the cache-server machines.
4. **Running Cache**: A cache, such as `demoCache`, must already be created and running.
5. **Cache Connectivity**: Every ASP.NET application server must be able to communicate with the NCache servers.
6. **Serializable Data**: Data transmitted through the SignalR backplane must be serializable.
7. **Required Namespaces**: Include the following namespaces in the OWIN `Startup` class:

```csharp
using Alachisoft.NCache.AspNet.SignalR;
using Microsoft.AspNet.SignalR;
```

## Installation

Install the Open Source ASP.NET SignalR integration through the NuGet Package Manager Console:

```powershell
Install-Package AspNet.SignalR.NCache.OpenSource
```

You can also install the package through the Visual Studio NuGet Package Manager:

1. Right-click the ASP.NET project in **Solution Explorer**.
2. Select **Manage NuGet Packages**.
3. Search for `AspNet.SignalR.NCache.OpenSource`.
4. Select the package.
5. Select **Install**.

## Configure the Cache Connection

The SignalR integration reads cache connection settings from a custom `ConnectionOptions` section in the application's *Web.config* file.

Register the section handler under the `<configSections>` element:

```xml
<configuration>
  <configSections>
    <section name="ConnectionOptions"
             type="Alachisoft.NCache.AspNet.SignalR.SignalRConnectionOptions, Alachisoft.NCache.SignalR" />
  </configSections>
</configuration>
```

The `<configSections>` element must appear before the other configuration sections inside `<configuration>`.

Add the `ConnectionOptions` section and specify one or more NCache servers:

```xml
<configuration>
  <configSections>
    <section name="ConnectionOptions"
             type="Alachisoft.NCache.AspNet.SignalR.SignalRConnectionOptions, Alachisoft.NCache.SignalR" />
  </configSections>

  <ConnectionOptions ClientBindIp=""
                     AppName="DemoAppName"
                     EnableClientLogs="false"
                     LogLevel="Info"
                     LoadBalance="true"
                     ConnectionRetries="5"
                     ConnectionTimeout="5"
                     RetryInterval="1"
                     RetryConnectionDelay="0"
                     ClientRequestTimeOut="90">
    <Server name="20.200.20.11" port="9800" />
  </ConnectionOptions>
</configuration>
```

You can add multiple `<Server>` elements when the application can connect to more than one NCache server.

Connection properties that are not specified use the applicable defaults from the NCache client configuration.

## Configure the Cache Name and Event Key

Add the cache name and application event key to the `<appSettings>` section of *Web.config*:

```xml
<appSettings>
  <add key="cache" value="demoCache" />
  <add key="eventKey" value="chatApplication" />
</appSettings>
```

The configuration settings have the following purposes:

| **Setting** | **Description**|
| ----------- | --------------------- |
| `cache`     | Specifies the name of the running NCache cache used by the SignalR backplane.                                                                       |
| `eventKey`  | Specifies the unique application key used to synchronize updates through NCache. All instances of the same application must use the same event key. |

Use a different event key for unrelated SignalR applications that share the same cache.

## Register NCache as the SignalR Backplane

Register NCache in the OWIN `Startup` class before calling `app.MapSignalR()`.

The `UseNCache` extension provides the following overload:

```csharp
public static IDependencyResolver UseNCache(
    this IDependencyResolver resolver,
    string cacheName,
    string eventKey,
    string userID = null,
    string password = null)
```

Register the backplane as follows:

```csharp
using System.Configuration;
using Alachisoft.NCache.AspNet.SignalR;
using Microsoft.AspNet.SignalR;
using Microsoft.Owin;
using Owin;

[assembly: OwinStartup(typeof(MyApp.Startup))]

namespace MyApp
{
    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            string cacheName =
                ConfigurationManager.AppSettings["cache"];

            string eventKey =
                ConfigurationManager.AppSettings["eventKey"];

            GlobalHost.DependencyResolver.UseNCache(
                cacheName,
                eventKey);

            app.MapSignalR();
        }
    }
}
```

The application reads the cache name and event key from *Web.config*. `UseNCache` then registers NCache against SignalR's global dependency resolver before the SignalR hubs are mapped.


## Use SignalR

After registering NCache, continue using the standard ASP.NET SignalR APIs. No NCache-specific code is required in the SignalR hub.

```csharp
using Microsoft.AspNet.SignalR;

public class ChatHub : Hub
{
    public void Send(string userName, string message)
    {
        Clients.All.receiveMessage(userName, message);
    }
}
```

When the hub broadcasts a message, the NCache backplane synchronizes the participating application servers. Each server can then deliver the message to its locally connected SignalR clients.

## How the SignalR Backplane Works

When a SignalR operation occurs:

1. Each ASP.NET application server is registered with NCache through `UseNCache`.
2. SignalR clients connect to their respective application servers.
3. NCache maintains an item associated with the configured `eventKey`.
4. A client operation updates the version of the corresponding cache item.
5. NCache fires the registered event across the participating application servers.
6. Each server receives the update.
7. The servers broadcast the update to their locally connected SignalR clients.

This allows clients connected to different application servers to receive updates without repeatedly requesting the latest application state.

## Run the Sample

An Open Source ASP.NET SignalR chat sample is available in the NCache Samples repository. Because this integration targets classic ASP.NET and .NET Framework, run the sample through Visual Studio, IIS Express, or IIS.

### Using Visual Studio

1. Clone or download the NCache Samples repository.
2. Open the SignalR sample solution in Visual Studio.
3. Restore the NuGet packages.
4. Make sure that NCache is running.
5. Create and start the cache specified in the sample's *Web.config* file.
6. Verify that the configured NCache server addresses are accessible.
7. Build the solution.
8. Run the application through IIS Express or IIS.
9. Open the application in multiple browser windows to test real-time message delivery.

For a web-farm test, deploy separate instances of the sample on multiple application servers. Configure every instance to use the same cache name and event key.

### Using the Command Line

For environments with NuGet and MSBuild installed, restore and build the solution with:

```powershell
nuget restore
msbuild /p:Configuration=Debug
```

Host the resulting application through IIS or IIS Express. `dotnet run` is not used because the integration targets classic ASP.NET and .NET Framework.

## Validation

Before running the application, verify the following:

- The configured NCache cache exists.
- The cache is running.
- Every application server can connect to the NCache cluster.
- The `ConnectionOptions` section is correctly registered in *Web.config*.
- At least one valid NCache server is configured when server information is not available through the client configuration.
- The `cache` app setting matches the running cache name.
- The `eventKey` app setting is present.
- Every instance of the same SignalR application uses the same event key.
- `UseNCache` is called before `app.MapSignalR()`.
- The application uses ASP.NET SignalR 2.4.0 or later.
- Data transmitted through the backplane is serializable.

If the cache does not exist, is not running, or cannot be reached, NCache cannot initialize the SignalR backplane.

## Best Practices

- Run the NCache backplane in the same data center as the SignalR application.
- Use the same cache name and event key across all instances of the same application.
- Use a unique event key for each unrelated SignalR application.
- Register NCache before calling `app.MapSignalR()`.
- Configure multiple NCache server addresses for distributed deployments.
- Use an application-specific value for `AppName`.
- Keep connection timeout and retry values appropriate for the deployment environment.
- Enable client logs only while diagnosing connection or message propagation issues.
- Ensure that all SignalR message payloads are serializable.
- Test the integration with multiple application instances before deploying it to production.
- Monitor the NCache cluster to ensure sufficient capacity for the expected message traffic.
- Use separate cache configurations for development, testing, and production environments.

## Resources

- [NCache Documentation](https://www.alachisoft.com/resources/docs/)
- [ASP.NET SignalR Backplane](https://www.alachisoft.com/resources/docs/ncache/prog-guide/aspnet-signalr.html)
- [NCache Extension for ASP.NET SignalR](https://www.alachisoft.com/resources/docs/ncache/prog-guide/ncache-extension-signalr.html?tabs=net)
- [ASP.NET SignalR Sample](https://github.com/Alachisoft/NCache-Samples/tree/master/dotnet-framework/SignalRChat/oss)
- [NCache Open Source](https://github.com/Alachisoft/NCache)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)

## Technical Support

Alachisoft provides various technical support resources.

- Visit the [Alachisoft Support Center](https://www.alachisoft.com/support.html) to select a support resource appropriate for your issue.
- To request an additional feature or report a documentation discrepancy, contact [support@alachisoft.com](mailto:support@alachisoft.com).

## Copyrights

Copyright © 2026 Alachisoft. All rights reserved.
