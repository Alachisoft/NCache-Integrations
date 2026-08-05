# NCache ASP.NET Core Authentication TicketStore

The `NCache.OSS.AspNetCore.Authentication.TicketStore` NuGet package enables ASP.NET Core applications to use NCache as a distributed implementation of `ITicketStore` for Cookie Authentication.

The integration stores complete `AuthenticationTicket` objects in an NCache cluster instead of sending the full authentication state to the browser. The client receives only a lightweight session key, while the corresponding authentication ticket remains available to all application instances connected to the same distributed cache.

## Package Versions

| **Package**                                        | **Version** |
| -------------------------------------------------- | ----------- |
| `NCache.OSS.AspNetCore.Authentication.TicketStore` | >= 5.3.6.2  |
| `Alachisoft.NCache.Opensource.SDK`                 | >= 5.3.6.2  |
| `Microsoft.AspNetCore.Authentication.Cookies`      | >= 2.3.10   |
| `Microsoft.Extensions.Configuration.Binder`        | >= 10.0.8   |

## Overview

ASP.NET Core Cookie Authentication normally serializes the authenticated user's identity, including claims, roles, authentication metadata, and other ticket information, into an `AuthenticationTicket`. This ticket is protected and returned to the browser as part of the authentication cookie.

As the user's identity becomes more complex, the authentication cookie can become large. Large cookies increase HTTP request overhead and can eventually exceed server or proxy header-size limits.

ASP.NET Core provides the `ITicketStore` interface to externalize authentication state from the client. Instead of storing the complete `AuthenticationTicket` inside the cookie, the application stores the ticket in a server-side repository and sends only a unique reference key to the browser.

NCache provides a distributed `ITicketStore` backend. Authentication tickets are stored in an NCache cluster and can therefore be accessed by any application instance in a load-balanced deployment.

This allows users to remain authenticated even when subsequent requests are routed to different application servers.

## Key Features

- **Distributed Authentication Ticket Storage:** Stores ASP.NET Core `AuthenticationTicket` objects in an NCache cluster.
- **Cookie Minimization:** Keeps only a lightweight session key in the client cookie instead of the complete authentication ticket.
- **Web Farm Support:** Allows authenticated users to move between application servers without losing their authentication session.
- **No Sticky Sessions Required:** Any application instance can retrieve the ticket from the shared NCache cluster.
- **High Availability:** Authentication tickets remain available through NCache clustered cache topologies.
- **Horizontal Scalability:** Supports adding cache servers as authentication traffic and concurrent session counts increase.
- **Server-Side Identity Storage:** Keeps user claims and authentication metadata in the server-side cache instead of transmitting them on every request.
- **Sliding Expiration Support:** Synchronizes renewed authentication tickets with the configured cookie expiration behavior.
- **Session Invalidation:** Allows an authentication session to be terminated by removing its ticket from NCache.
- **Native Ticket Serialization:** Uses ASP.NET Core's native ticket serialization mechanism for storing `AuthenticationTicket` data.
- **Configuration-Based Registration:** Supports provider configuration through *appsettings.json*.
- **Programmatic Registration:** Supports inline configuration through an action delegate in *Program.cs*.

## What Is Installed

Installing `NCache.OSS.AspNetCore.Authentication.TicketStore` adds the components required to store ASP.NET Core Cookie Authentication tickets in NCache.

The package provides:

- The NCache `ITicketStore` implementation
- The `AddNCacheTicketStore` extension method
- `NCacheOptions` for TicketStore configuration
- NCache server connectivity support
- NCache client libraries required to communicate with the cache cluster

The TicketStore provider must be registered in the application's service collection before Cookie Authentication uses it.

## Prerequisites

Before using this package, ensure that you have:

1. **ASP.NET Core Application:** An application configured to use ASP.NET Core Cookie Authentication.
2. **NCache OSS 5.3.6.2 or Later:** `ITicketStore` support is available in NCache OpenSource 5.3.6.2 and later.
3. **NCache Installation:** NCache must be installed on the cache-server machines.
4. **Running Cache:** A cache, such as `demoCache`, must already be created and running.
5. **Cache Connectivity:** Every application instance must be able to communicate with the NCache servers.
6. **Required Namespaces:** Include the following namespaces in *Program.cs*:

```csharp
using NCache.OSS.AspNetCore.Authentication.TicketStore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
```

## Installation

Install the NCache Open Source TicketStore integration through the NuGet Package Manager Console:

```powershell
Install-Package NCache.OSS.AspNetCore.Authentication.TicketStore
```

You can also install the package through the .NET CLI:

```bash
dotnet add package NCache.OSS.AspNetCore.Authentication.TicketStore
```

Alternatively, install it through the Visual Studio NuGet Package Manager:

1. Right-click the ASP.NET Core project in **Solution Explorer**.
2. Select **Manage NuGet Packages**.
3. Search for `NCache.OSS.AspNetCore.Authentication.TicketStore`.
4. Select the package.
5. Select **Install**.

## Configure TicketStore through appsettings.json

Using *appsettings.json* is the recommended approach for production environments because cache settings can be changed without modifying application code.

Add an `NCacheTicketStore` section to *appsettings.json*:

```json
{
  "NCacheTicketStore": {
    "CacheName": "demoCache",
    "ServerList": [
      {
        "Ip": "20.200.20.29",
        "Port": 9800
      }
    ]
  }
}
```

Register the configuration section in *Program.cs*:

```csharp
builder.Services.AddNCacheTicketStore(
    builder.Configuration.GetSection("NCacheTicketStore"));
```

The cache specified by `CacheName` must already exist and be running.

You can add multiple entries to `ServerList` when the application can connect to more than one NCache server.

## Configure TicketStore through Program.cs

The TicketStore provider can also be configured programmatically through an action delegate:

```csharp
builder.Services.AddNCacheTicketStore(options =>
{
    options.CacheName = "demoCache";

    options.ServerList.Add(
        new NCacheOptions.ServerConfig
        {
            Ip = "20.200.20.29",
            Port = 9800
        });
});
```

This approach is useful when TicketStore settings need to be configured directly during application startup.

## How TicketStore Works

The NCache TicketStore integration maps the ASP.NET Core authentication lifecycle to distributed NCache operations.

### Sign-In

When a user signs in:

1. The application calls `HttpContext.SignInAsync()`.
2. ASP.NET Core Cookie Authentication creates an `AuthenticationTicket`.
3. Instead of placing the complete ticket in the browser cookie, the middleware calls `ITicketStore.StoreAsync()`.
4. The TicketStore creates a unique session key.
5. The authentication ticket is serialized using the ASP.NET Core `TicketSerializer`.
6. The serialized ticket is stored in NCache.
7. Only the session key is returned to the browser inside the authentication cookie.

This keeps the complete user identity and claims data in the server-side distributed cache.

### Subsequent Requests

For later requests:

1. The browser sends the lightweight session-key cookie.
2. Cookie Authentication extracts the session key.
3. The middleware calls `ITicketStore.RetrieveAsync(key)`.
4. NCache retrieves the corresponding serialized authentication ticket.
5. The ticket is deserialized back into an `AuthenticationTicket`.
6. ASP.NET Core restores the associated `ClaimsPrincipal` to `HttpContext.User`.
7. The request continues as an authenticated request.

If the ticket is no longer present in NCache because it expired or was removed, the TicketStore returns no ticket and the user must authenticate again.

### Sliding Expiration

When sliding expiration is enabled:

1. Requests continue to use the authentication ticket stored in NCache.
2. ASP.NET Core determines when the ticket's sliding-expiration threshold has been reached.
3. Cookie Authentication invokes `ITicketStore.RenewAsync()`.
4. NCache updates the stored authentication ticket.
5. The corresponding expiration period is renewed in the cache.

This allows active authentication sessions to remain valid while inactive sessions expire.

### Sign-Out

When a user signs out:

1. The application calls `HttpContext.SignOutAsync()`.
2. Cookie Authentication invokes `ITicketStore.RemoveAsync(key)`.
3. NCache removes the corresponding authentication ticket.
4. The authentication session is invalidated across every application instance using the same distributed cache.

## Session Invalidation

Because authentication tickets are stored centrally in NCache, removing a ticket invalidates the user's authenticated session across the complete web farm.

This can be useful for scenarios such as:

- User logout
- Administrative account lockout
- Forced session termination
- Password reset workflows
- Security-related session revocation

Once the ticket is removed, subsequent requests using the old session key cannot restore the associated authentication identity.

## Run the Sample

The sample demonstrates configuring ASP.NET Core Cookie Authentication to use NCache as the distributed `ITicketStore`.

### Using Visual Studio

1. Open the TicketStore sample in Visual Studio.
2. Restore the NuGet packages.
3. Make sure that NCache OSS 5.3.6.2 or later is running.
4. Create and start the cache configured by the sample.
5. Verify that the application can connect to the configured NCache server.
6. Build the application.
7. Run the sample.
8. Open the sample endpoints in the browser.

For example:

```text
https://localhost:55211/login
https://localhost:55211/
https://localhost:55211/ping
```

Try the following scenarios:

- Visit `/login` to authenticate the user and store the authentication ticket in NCache.
- Navigate to `/` to verify that the authenticated identity is restored from NCache.
- Call `/ping` repeatedly to verify that the authentication session remains available.
- Enable sliding expiration and continue calling `/ping` to observe the ticket being renewed before expiration.
- Sign the user out or remove the corresponding ticket to verify that the authentication session is invalidated.

### Using the Command Line

Restore the application dependencies:

```bash
dotnet restore
```

Run the application:

```bash
dotnet run
```

Open the application URL displayed by the application and execute the available authentication scenarios.

## Validation

Before running the application, verify the following:

- NCache OSS 5.3.6.2 or later is being used.
- `NCache.OSS.AspNetCore.Authentication.TicketStore` is installed.
- The configured NCache cache exists.
- The cache is running.
- Every application instance can connect to the NCache cluster.
- `NCache.OSS.AspNetCore.Authentication.TicketStore` is included where the provider is configured.
- `Microsoft.AspNetCore.Authentication.Cookies` is available.
- `CacheName` matches an existing and running NCache cache.
- Each configured server contains a valid IP address.
- Each configured port is between `1` and `65535`.
- `AddNCacheTicketStore` is registered before authentication services begin using the TicketStore.
- Cookie Authentication is configured for the application.
- Every application instance that must share authentication state connects to the same NCache cache.

If the configured cache does not exist, is not running, or cannot be reached, authentication tickets cannot be stored or retrieved through NCache.

## Best Practices

- Use the same NCache cache across all application instances that need to share authenticated sessions.
- Use *appsettings.json* for production environments where cache connectivity settings can differ between deployments.
- Configure multiple NCache server addresses when appropriate for the deployment.
- Keep the NCache cluster in the same environment or data center as the application to minimize authentication latency.
- Enable sliding expiration only when active authentication sessions should be automatically extended.
- Set cookie and ticket expiration values according to the application's authentication policy.
- Use centralized ticket removal when users need to be signed out across the entire web farm.
- Use NCache clustered topologies that provide the required availability for authentication state.
- Test authentication failover across multiple application instances before deploying to production.
- Monitor the NCache cluster to ensure sufficient capacity for the expected number of active authentication sessions.
- Use separate caches or environments for development, testing, and production deployments.

## Resources

- [NCache ASP.NET Core ITicketStore](https://www.alachisoft.com/resources/docs/ncache/prog-guide/aspnetcore-iticketstore.html)
- [NCache ITicketStore Overview](https://www.alachisoft.com/resources/docs/ncache/prog-guide/iticket-store-overview.html)
- [NCache.OSS.AspNetCore.Authentication.TicketStore](https://www.nuget.org/packages/NCache.OSS.AspNetCore.Authentication.TicketStore)
- [Microsoft ITicketStore Documentation](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.authentication.cookies.iticketstore)
- [NCache Open Source](https://github.com/Alachisoft/NCache)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)

## Technical Support

Alachisoft provides various technical support resources.

- Visit the [Alachisoft Support Center](https://www.alachisoft.com/support.html) to select a support resource appropriate for your issue.
- To request an additional feature or report a documentation discrepancy, contact [support@alachisoft.com](mailto:support@alachisoft.com).

## Copyrights

Copyright © 2005–2026 Alachisoft. All rights reserved.
