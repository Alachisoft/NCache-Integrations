# NCache Output Cache Provider for ASP.NET (System.Web)

**NCache implementation of `System.Web.Caching.OutputCacheProvider`**, letting classic ASP.NET applications (Web Forms / MVC on .NET Framework) store page and action output in a distributed NCache cluster instead of each web server's local memory.


## Package Versions

| Package | Version |
|---|---|
| AspNet.OutputCache.NCache.Opensource | 5.3.6.1 |
| Alachisoft.NCache.SDK | >= 5.3.6.2 |
| Newtonsoft.Json | >= 13.0.1 (used internally for serialization) |

## Installation

Install the package matching your NCache edition:

```powershell
Install-Package AspNet.OutputCache.NCache.Opensource
```

## Prerequisites

Before using this package, ensure you have:

1. **NCache Server** - a running NCache cluster (Enterprise or Professional edition)
2. **A Cache** - created on the cluster, matching the `cacheName` you configure below
3. **A classic ASP.NET application** - Web Forms or MVC on .NET Framework

## Overview

`NOutputCacheProvider` plugs into ASP.NET's output cache pipeline and forwards `Get`/`Add`/`Set`/`Remove` calls to an NCache `ICache` instance, so cached page/action output is shared across every server behind the cluster rather than living in a single process.

**Key benefits:**

- Drop-in replacement for the default in-memory ASP.NET output cache - no changes to pages or controllers
- Output is shared across all web servers in a load-balanced deployment
- Non-primitive objects don't need to be marked `[Serializable]`: a reflection-based serializer (`InternalClassSerializer`) walks the object graph - including private fields, arrays, lists, and nested objects - into JSON, with circular-reference protection, instead of relying on `BinaryFormatter`
- Per-provider toggles for exception propagation and NCache logging

## Configuration

The provider reads its settings from the attributes on its `<add>` entry in `web.config`:

| Attribute | Required | Default | Description |
|---|---|---|---|
| `cacheName` | Yes | - | The NCache cache to connect to; startup fails with a `ConfigurationErrorsException` if missing |
| `description` | No | `"NCache Output Cache Provider"` | Provider description |
| `exceptionsEnabled` | No | `true` | When `true`, errors from cache operations are rethrown to the caller after being logged; when `false`, they're swallowed |
| `enableLogs` | No | `false` | Enables NCache logging at `info` level |
| `enableDetailLogs` | No | `false` | Enables NCache logging at `all` level (implies `enableLogs`) |
| `writeExceptionsToEventLog` | No | `false` | Parsed at startup; in the version reviewed here it isn't yet wired to an actual Event Log write, so don't rely on it for that purpose |

## Usage

Once registered as the default output cache provider, use the standard ASP.NET caching surface - no NCache-specific code is needed in your pages or controllers:

```aspx
<%@ OutputCache Duration="60" VaryByParam="none" %>
```

```csharp
[OutputCache(Duration = 60, VaryByParam = "none")]
public ActionResult Index() { ... }
```

`Duration` is in seconds; ASP.NET calls into `NOutputCacheProvider.Add`/`Set` to populate the cache and `Get` to serve subsequent requests, with `utcExpiry` translated into an absolute NCache expiration.

## Sample

Demonstrates configuring **ASP.NET Output Cache** to use **NCache** as the distributed backing store. Cached HTTP responses are stored in NCache, allowing multiple application servers to share the same output cache for improved scalability and consistent cache behavior across a web farm.

Follow the following instructions to run sample.

```bash
dotnet restore
dotnet run
```

Once the application is running, open the following endpoint in your browser:

```
https://localhost:44310/
```

Refresh the page multiple times to observe cached responses being served. After the configured cache duration expires, a fresh response is generated and stored back into NCache.

## Resources

- [NCache Documentation](https://www.alachisoft.com/resources/docs/)
- [ASP.NET Output Caching with NCache](https://www.alachisoft.com/resources/docs/ncache/prog-guide/aspnet-output-cache.html)
- [NCache Open Source](https://github.com/Alachisoft/NCache)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)

## License

Copyright © 2026 Alachisoft. All rights reserved.