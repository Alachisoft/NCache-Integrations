# NCache SIGNALR

## Overview

**NCache implementation for ASP.NET Core SignalR**, allowing SignalR applications to use NCache as a distributed backplane for synchronizing messages across multiple application servers.

NCache extends the `ISignalRServerBuilder` interface through the `AddNCache` extension method, enabling SignalR applications to register an NCache backplane using a cache name and an application-specific event key. Once configured, SignalR messages published on one server are propagated through NCache so that all connected application instances can deliver them to their respective clients.

**Key benefits:**

- Distributed backplane for ASP.NET Core SignalR
- Extends `ISignalRServerBuilder` through the `AddNCache` extension method
- Supports horizontal scaling across multiple application servers
- Uses NCache for fast, distributed message propagation
- Supports configurable client connection options and cache security

## Package Versions

| Package | Version |
|---------|---------|
| Alachisoft.NCache.Opensource.SDK | >= 5.3.6.2 |
| AspNetCore.SignalR.NCache.Opensource | Compatible with ASP.NET Core SignalR 1.1.0 |

## Prerequisites

Before using this package, ensure you have:

1. **NCache Server** – a running NCache cluster.
2. **An NCache cache** – created and running on the cluster.
3. **ASP.NET Core SignalR** version **1.1.0**.
4. Include the following namespaces in your application:
   - `Alachisoft.NCache.AspNetCore.SignalR`
   - `Microsoft.AspNetCore.SignalR`
5. Ensure cached objects are serializable.

## Configuration

The SignalR integration is configured through the `AddNCache` extension method and an optional `NCacheConfiguration` object.

Configuration can be supplied either directly in `Startup.cs` or through the application's `appsettings.json`.

Example configuration:

```json
"NCacheConfiguration": {
  "CacheName": "demoCache",
  "EventKey": "chatApplication",
  "ConnectionOptions": {
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
```

`ConnectionOptions` maps directly to NCache client connection settings. Any property not specified uses the defaults defined in `client.ncconf`.

| Property | Description |
|----------|-------------|
| `CacheName` | Name of the NCache cache used as the SignalR backplane. |
| `EventKey` | Application-specific key used to identify SignalR events. |
| `ConnectionOptions` | Optional NCache client connection settings. |
| `ServerList` | List of NCache server nodes. |
| `AppName` | Application name reported to NCache. |
| `ClientBindIP` | Local IP address to bind the client connection. |
| `LogLevel` | NCache client logging level. |
| `UserCredentials` | Credentials used when cache security is enabled. |

## Usage

Register the NCache backplane using one of the provided `AddNCache` overloads.

Supported overloads include:

- `AddNCache(string cacheName, string eventKey)`
- `AddNCache(string cacheName, string eventKey, string userId, string password)`
- `AddNCache(Action<NCacheConfiguration> configure)`

Once registered, SignalR automatically uses NCache as its distributed backplane. Messages published by one application server are propagated through NCache and delivered by every connected SignalR server, enabling consistent real-time communication across a server farm.

## References

Reference documentation is available at:\
https://www.alachisoft.com/resources/docs/ncache/prog-guide/ncache-extension-signalr.html?tabs=net

## Resources

- [NCache Documentation](https://www.alachisoft.com/resources/docs/)
- [NCache SignalR Documentation](https://www.alachisoft.com/resources/docs/ncache/prog-guide/asp-net-core-signalr.html)
- [Playground sample](https://github.com/Alachisoft/NCache-Samples/)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)

## Technical Support

Alachisoft&copy; provides various sources of technical support.

- Please refer to http://www.alachisoft.com/support.html to select a support resource you find suitable for your issue.
- To request additional features in the future, or if you notice any discrepancy regarding this document, please drop an email to [support@alachisoft.com](mailto:support@alachisoft.com).

## Copyrights

Copyright 2026 Alachisoft&copy;