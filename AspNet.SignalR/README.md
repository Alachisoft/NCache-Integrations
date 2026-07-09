# NCache SignalR (ASP.NET)

## Overview

**NCache implementation for ASP.NET SignalR**, allowing SignalR applications to use NCache as a distributed backplane for synchronizing messages across multiple application servers.

This package extends SignalR's `IDependencyResolver` with a `UseNCache` extension method that registers an `NCacheMessageBus` as the `IMessageBus` implementation used by SignalR, backed by an NCache pub/sub topic. Once configured, SignalR messages published on one server are propagated through NCache so that all connected application instances can deliver them to their respective clients.

**Key benefits:**

- Distributed backplane for ASP.NET SignalR
- Extends `IDependencyResolver` through the `UseNCache` extension method
- Supports horizontal scaling across multiple application servers
- Uses NCache pub/sub for fast, distributed message propagation
- Supports configurable client connection options and cache security

## Package / Project Info

| Item | Value |
|------|-------|
| Assembly name | `Alachisoft.NCache.SignalR` |
| Root namespace | `Alachisoft.NCache.AspNet.SignalR` |
| Target Framework | .NET Framework 4.6.2 |
| NCache SDK | `Alachisoft.NCache.Opensource.SDK` 5.3.6.2 |
| SignalR dependency | `Microsoft.AspNet.SignalR.Core` 2.4.3 |
| Other dependencies | `Microsoft.Owin` 2.1.0, `Microsoft.Owin.Security` 2.1.0, `Owin` 1.0, `Newtonsoft.Json` 13.0.1, `jQuery` 1.6.4 |

## Prerequisites

Before using this package, ensure you have:

1. A running **NCache Server** cluster.
2. An **NCache cache** created and running on the cluster (matching the `config.ncconf` / `client.ncconf` sample files provided).
3. **ASP.NET SignalR** (`Microsoft.AspNet.SignalR.Core`) version 2.4.3 referenced in your host application.
4. Cached message payloads must be serializable.
5. Include the relevant namespace in your application:
   - `Alachisoft.NCache.AspNet.SignalR`
   - `Microsoft.AspNet.SignalR`

## Configuration

Client connection settings are supplied through a custom `IConfigurationSectionHandler` (`SignalRConnectionOptions`) that reads a `<ConnectionOptions>` section from `Web.config`/`App.config`. Register the section handler and declare the section, then list one or more `<server>` nodes:

```xml
<configuration>
  <configSections>
    <section name="ConnectionOptions"
             type="Alachisoft.NCache.AspNet.SignalR.SignalRConnectionOptions, Alachisoft.NCache.SignalR" />
  </configSections>

  <ConnectionOptions AppName="DemoAppName"
                      LogLevel="Error"
                      LoadBalance="true"
                      ConnectionRetries="5"
                      ConnectionTimeout="5"
                      RetryInterval="1"
                      RetryConnectionDelay="0"
                      ClientRequestTimeOut="90"
                      EnableClientLogs="false">
    <server name="10.0.5.1" port="9800" />
  </ConnectionOptions>
</configuration>
```

At runtime, `NCacheProvider.ConnectAsync` reads this section via `WebConfigurationManager.GetSection("ConnectionOptions")` and passes it to `CacheManager.GetCache`. Any attribute not specified falls back to the defaults defined in `client.ncconf`.

| Attribute | Description |
|-----------|-------------|
| `AppName` | Application name reported to NCache. |
| `ClientBindIP` | Local IP address to bind the client connection. |
| `LoadBalance` | Enables client-side load balancing across servers. |
| `ConnectionRetries` | Number of connection retry attempts. |
| `ConnectionTimeout` | Timeout (seconds) for establishing a connection. |
| `RetryInterval` | Interval (seconds) between retries. |
| `RetryConnectionDelay` | Delay (seconds) before reconnecting after a drop. |
| `ClientRequestTimeOut` | Timeout (seconds) for individual client requests. |
| `EnableClientLogs` | Enables NCache client-side logging. |
| `LogLevel` | NCache client logging level (e.g. `Error`, `Info`, `Debug`). |
| `<server name="" port="" />` | One or more NCache server nodes to connect to. |

## Usage

Register the NCache backplane against SignalR's dependency resolver, typically in your OWIN `Startup` class:

```csharp
using Microsoft.AspNet.SignalR;
using Microsoft.Owin;
using Alachisoft.NCache.AspNet.SignalR;
using Owin;

[assembly: OwinStartup(typeof(MyApp.Startup))]
namespace MyApp
{
    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            GlobalHost.DependencyResolver.UseNCache("demoCache", "chatApplication");

            app.MapSignalR();
        }
    }
}
```

Supported overloads on `IDependencyResolver`:

- `UseNCache(string cacheName, string eventKey)`
- `UseNCache(NCacheScaleoutConfiguration configuration)`

Once registered, SignalR uses the `NCacheMessageBus` as its `IMessageBus`, publishing and subscribing to an NCache topic keyed by `eventKey` so messages fan out to every connected server.

## References

Reference documentation is available at:\
https://www.alachisoft.com/resources/docs/ncache/prog-guide/ncache-extension-signalr.html?tabs=net

## Resources

- [NCache Documentation](https://www.alachisoft.com/resources/docs/)
- [NCache SignalR Documentation](https://www.alachisoft.com/resources/docs/ncache/prog-guide/aspnet-signalr.html)
- [Playground sample](https://github.com/Alachisoft/NCache-Samples/)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)

## Technical Support

Alachisoft&copy; provides various sources of technical support.

- Please refer to http://www.alachisoft.com/support.html to select a support resource you find suitable for your issue.
- To request additional features in the future, or if you notice any discrepancy regarding this document, please drop an email to [support@alachisoft.com](mailto:support@alachisoft.com).

## Copyrights

Copyright 2026 Alachisoft&copy;
