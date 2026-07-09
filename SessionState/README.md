# NCache Integration for ASP.NET Session State

**NCache backed session storage for both classic ASP.NET (System.Web) and ASP.NET Core applications**, allowing session state to be stored in a distributed NCache cache instead of the default in process or SQL Server session providers.

## Package Versions

### Classic ASP.NET (.NET Framework): `AspNet.SessionState.NCache.Opensource`

| Package | Version |
|---|---|
| AspNet.SessionState.NCache.Opensource | 5.3.6.1 |
| Alachisoft.NCache.Opensource.SDK | >= 5.3.6.2 |

Targets `net462` and higher.

### ASP.NET Core: `AspNetCore.Session.NCache.Opensource`

| Package | Version |
|---|---|
| AspNetCore.Session.NCache.Opensource | 5.3.6.1 |
| Alachisoft.NCache.Opensource.SDK | >= 5.3.6.2 |
| Microsoft.AspNetCore.Http.Abstractions | >= 2.1.0 |
| Microsoft.Extensions.Caching.Abstractions | >= 2.1.0 |
| Microsoft.Extensions.Configuration | >= 2.1.0 |
| Microsoft.Extensions.DependencyInjection.Abstractions | >= 2.1.0 |
| Microsoft.Extensions.Options.ConfigurationExtensions | >= 2.1.0 |
| System.Configuration.ConfigurationManager | >= 9.0.0 |

Ships both a `net462` build (classic .NET Framework hosting) and a `netstandard2.0` build, so it works with ASP.NET Core on both .NET Framework and modern .NET (tested against `net8.0`).

## Installation

```powershell
# Classic ASP.NET (.NET Framework)
Install-Package AspNet.SessionState.NCache.Opensource -Version 5.3.6.1
dotnet add package AspNet.SessionState.NCache.Opensource --version 5.3.6.1

# ASP.NET Core
Install-Package AspNetCore.Session.NCache.Opensource -Version 5.3.6.1
dotnet add package AspNetCore.Session.NCache.Opensource --version 5.3.6.1
```
## Overview

This integration provides two independent, interoperable session storage modules:

- **`NSessionStoreProvider`** (classic ASP.NET / .NET Framework): a `SessionStateStoreProviderBase` implementation, registered through `Web.config` as a custom session state provider.
- **NCache Session Storage Service** (ASP.NET Core): an `ISession` middleware and store, registered via `AddNCacheSession()` / `UseNCacheSession()`, replacing the default ASP.NET Core in-memory session provider.

Because both modules read and write sessions against the same kind of NCache cache, they can point at a single shared cache to keep an ASP.NET Framework app and an ASP.NET Core app in sync on the same session which is useful during a Framework to Core migration, or for running both stacks side by side. See [Sample Projects](#sample-projects) below.

**Key features:**

- Out of process, distributed session storage, meaning it survives sessions survive app pool/process recycles and outlive any single server
- Exclusive session locking, mirroring conventional ASP.NET session-locking behavior
- Choice of NCache Compact (binary) serialization or JSON serialization
- Configurable operation retries if the connection to the cache drops mid request
- Session data is stored with the tag `NC_ASP.NET_session_data` and can be queried in bulk via `cache.SearchService.GetByTag(new Tag("NC_ASP.NET_session_data"))`

> **Limitations**
>
> - Some advanced functionality may be limited by the NCache Open Source edition compared to Enterprise.
> - Location Affinity is currently available in both modules; however, it has been marked as deprecated. For new implementations requiring multi region session management, the Multi Region Session Provider is the recommended approach.

## Classic ASP.NET (.NET Framework)

### Configuration

Edit `Web.config` and set `<sessionState>` to use the custom provider:

```xml
<system.web>
  <sessionState cookieless="false" regenerateExpiredSessionId="true" mode="Custom" customProvider="NCacheSessionProvider" timeout="20">
    <providers>
      <add name="NCacheSessionProvider"
           type="Alachisoft.NCache.Web.SessionState.NSessionStoreProvider"
           cacheName="demoCache"
           sessionAppId="MyApp"
           writeExceptionsToEventLog="false"
           enableLogs="false"
           useJsonSerialization="false" />
    </providers>
  </sessionState>
</system.web>
```

`timeout="20"` on `<sessionState>` sets the session idle timeout in minutes and is picked up automatically by the provider. There's no separate NCache specific timeout attribute to set.

#### Provider attributes

| Attribute | Required | Description |
|---|---|---|
| `cacheName` | Yes | Name of the NCache cache used for session storage. |
| `sessionAppId` | No | Identifier that keeps session IDs unique when multiple applications share the same cache. |
| `enableSessionLocking` | No | Exclusively locks a session item while a request is using it. Default `false`. |
| `sessionLockingRetry` | No | Retries to acquire the lock before returning an empty session, when `enableSessionLocking` is `true`. `-1` = wait indefinitely. Default `-1`. |
| `emptySessionWhenLocked` | No | Return an empty session instead of waiting when the session is locked. Default `false`. |
| `useJsonSerialization` | No | Serialize session items as JSON instead of NCache's compact binary format. Default `false`. |
| `enableLogs` / `enableDetailLogs` | No | Enable NCache session-provider logging / detailed logging. Default `false`. |
| `writeExceptionsToEventLog` | No | Write provider exceptions to the Windows Event Log. Default `false`. |
| `exceptionsEnabled` | No | Propagate cache exceptions to the calling page instead of swallowing them. Default `false`. |
| `operationRetry` / `operationRetryInterval` | No | Retry count / interval (ms) for a cache operation if the connection drops mid-operation. Default `0`. |

### Usage

No further code changes are required. Once the provider is registered in `Web.config`, `Session["key"] = value;` and `var value = Session["key"];` work exactly as they do with any other ASP.NET session-state provider.

## ASP.NET Core

### Configuration

Add an `NCacheSettings` section to `appsettings.json`:

```json
{
  "NCacheSettings": {
    "CacheName": "demoCache",
    "SessionAppId": "MyApp",
    "UseJsonSerialization": true,
    "EnableLogs": false,
    "WriteExceptionsToEventLog": false,
    "RequestTimeout": 120,
    "SessionOptions": {
      "CookieName": ".NCache.AspNetCore.Session",
      "CookiePath": "/",
      "CookieHttpOnly": true,
      "IdleTimeout": 20,
      "CookieSecure": "None"
    }
  }
}
```

### Usage

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// Register NCache as the session provider, reading settings from appsettings.json
builder.Services.AddNCacheSession(builder.Configuration.GetSection("NCacheSettings"));

// ...or configure it directly in code instead:
// builder.Services.AddNCacheSession(options => { options.CacheName = "demoCache"; });

var app = builder.Build();

app.UseRouting();

// Must be registered before any middleware/endpoint that reads session data
app.UseNCacheSession();

app.UseAuthorization();
app.Run();
```

If `services.AddSession()` or `app.UseSession()` are already present, remove them as they will interfere with NCache Session Services.

Use the NCache extension methods to store and retrieve typed objects in session:

```csharp
HttpContext.Session.Set("cartId", cart);
HttpContext.Session.TryGetValue("cartId", out var cart);
```

#### Configuration options (`NCacheSessionConfiguration`)

| Property | Required | Description |
|---|---|---|
| `CacheName` | Yes | Name of the NCache cache used for session storage. |
| `SessionAppId` | No | Identifier that keeps session IDs unique when multiple applications share the same cache. |
| `UseJsonSerialization` | No | Serialize session items as JSON instead of NCache's compact binary format. Default `false`. |
| `RequestTimeout` | No | Time after which a new request forcefully releases a lock held by an older, unfinished request. Default `120`. |
| `ReadOnlyFlag` | No | `HttpContext.Items` key an application can set to `true` to mark the current request's session access as read-only (changes won't be committed). Default `.NCache.AspNetCore.IsReadOnly`. |
| `EnableLogs` / `EnableDetailLogs` | No | Enable NCache session-provider logging / detailed logging. Default `false`. |
| `WriteExceptionsToEventLog` | No | Write provider exceptions to the Windows Event Log. Default `false`. |
| `ExceptionsEnabled` | No | Propagate cache exceptions instead of swallowing them. Default `false`. |
| `OperationRetry` / `OperationRetryInterval` | No | Retry count / interval for a cache operation if the connection drops mid-operation. Default `0`. |
| `SessionOptions.CookieName` / `CookieDomain` / `CookiePath` / `CookieHttpOnly` / `CookieSecure` | No | Standard session cookie options. |
| `SessionOptions.IdleTimeout` | No | Session idle timeout, in minutes. |
| `EnableLocationAffinity` / `AffinityMapping` | No | Enables multi cache Location Affinity (see Limitations above). `AffinityMapping` is an array of `{ CacheName, CachePrefix }`; `CachePrefix` must be at least 4 characters. |

## Sample Projects

Working, end-to-end samples are available in the [NCache-Samples](https://github.com/Alachisoft/NCache-Samples) repository:

- [.NET Framework: Session Sharing (OSS)](https://github.com/Alachisoft/NCache-Samples/tree/master/dotnet-framework/SessionSharing/oss)
- [.NET / ASP.NET Core Session Sharing (OSS)](https://github.com/Alachisoft/NCache-Samples/tree/master/dotnet/SessionSharing/oss)

For More about samples and how to use the do read the ReadMe of the samples applications .


## Best Practices

- Create the NCache cache before starting your application.
- Mark custom session objects `[Serializable]`, or turn on JSON serialization if you'd rather not modify your types.
- Use a dedicated NCache cluster/cache per environment.
- Keep `sessionAppId` / `SessionAppId` set whenever multiple applications share the same cache, so session IDs don't collide.

## Resources

- [ASP.NET Session-State Provider](https://www.alachisoft.com/resources/docs/ncache/prog-guide/aspnet-session-state-provider.html)
- [ASP.NET Core Session Provider](https://www.alachisoft.com/resources/docs/ncache/prog-guide/ncache-aspnet-core-session-provider.html)
- [Session Sharing between ASP.NET and ASP.NET Core](https://www.alachisoft.com/resources/docs/ncache-5-2/prog-guide/aspnet-session-sharing.html)
- [NuGet Package AspNet.SessionState.NCache.Opensource](https://www.nuget.org/packages/AspNet.SessionState.NCache.Opensource)
- [NuGet Package AspNetCore.Session.NCache.Opensource](https://www.nuget.org/packages/AspNetCore.Session.NCache.Opensource)
- [NCache Open Source](https://github.com/Alachisoft/NCache)

## License

Copyright © 2026 Alachisoft. All rights reserved.

