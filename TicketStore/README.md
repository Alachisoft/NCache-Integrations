# NCache.OSS.TicketStore

**NCache Open Source implementation of ASP.NET Core `ITicketStore`**, providing a distributed backing store for ASP.NET Core Cookie Authentication by storing authentication tickets in NCache.

## Package Versions

| Package | Version |
|---------|---------|
| NCache.OSS.AspNetCore.Authentication.TicketStore | >= 5.3.6.2 |
| Alachisoft.NCache.Opensource.SDK | >= 5.3.6.2 |
| Microsoft.AspNetCore.Authentication.Cookies | >= 2.3.10 |
| Microsoft.Extensions.Configuration.Binder | >= 10.0.8 |

## Installation

Install via NuGet Package Manager:

```bash
dotnet add package NCache.OSS.AspNetCore.Authentication.TicketStore
```

Or via Package Manager Console:

```powershell
Install-Package NCache.OSS.AspNetCore.Authentication.TicketStore
```

## Prerequisites

Before using this package, ensure you have:

1. **NCache Server** – a running NCache cluster.
2. **An NCache cache** – created and running on the cluster.
3. Include the following namespaces in your application:
   - `NCache.OSS.AspNetCore.Authentication.TicketStore`
   - `Microsoft.AspNetCore.Authentication`
   - `Microsoft.AspNetCore.Authentication.Cookies`
   - `System.Security.Claims`

> **Note**
> This feature is supported only in **NCache Open Source 5.3.6.2** and later.

## Overview

`NCache.OSS.TicketStore` provides an implementation of ASP.NET Core's `ITicketStore` interface, allowing authentication tickets to be stored in a distributed NCache cluster instead of inside client cookies.

By externalizing authentication tickets to NCache, authenticated sessions can be shared across multiple application instances in load-balanced environments while reducing cookie size and centralizing user identity storage.

**Key benefits:**

- Distributed `ITicketStore` implementation for ASP.NET Core Cookie Authentication
- Enables authenticated session sharing across multiple application servers
- Stores authentication tickets in NCache instead of client cookies
- Supports configuration through `appsettings.json` or programmatic registration
- Uses the standard ASP.NET Core authentication pipeline with no application code changes

## Configuration

The TicketStore provider can be configured using either:

- `appsettings.json` (recommended)
- An action delegate passed to `AddNCacheTicketStore`

Example configuration:

```json
"NCacheTicketStore": {
  "CacheName": "demoCache",
  "ServerList": [
    {
      "Ip": "20.200.20.29",
      "Port": 9800
    }
  ]
}
```

| Property | Description |
|----------|-------------|
| `CacheName` | Name of the NCache cache used to store authentication tickets. |
| `ServerList` | List of NCache server nodes used for cache connectivity. Each entry contains an IP address and optional port. |

## Usage

Register the TicketStore provider using one of the supported approaches.

Using configuration:

```csharp
builder.Services.AddNCacheTicketStore(
    builder.Configuration.GetSection("NCacheTicketStore"));
```

Or configure it programmatically:

```csharp
builder.Services.AddNCacheTicketStore(options =>
{
    options.CacheName = "demoCache";

    options.ServerList.Add(new NCacheOptions.ServerConfig
    {
        Ip = "20.200.20.29",
        Port = 9800
    });
});
```

Once registered, ASP.NET Core Cookie Authentication uses NCache as the backing store for authentication tickets, enabling distributed authentication across multiple application instances.

## License

Copyright © 2005-2026 Alachisoft. All rights reserved.

## Resources

- [NCache Documentation](https://www.alachisoft.com/resources/docs/ncache/prog-guide/aspnetcore-iticketstore.html)
- [NCache Open Source](https://github.com/Alachisoft/NCache)
- [Microsoft ITicketStore Documentation](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.authentication.cookies.iticketstore)
- [NCache ITicketStore Documentation](https://www.alachisoft.com/resources/docs/ncache/prog-guide/iticket-store-overview.html)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)