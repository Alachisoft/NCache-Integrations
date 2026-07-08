# NCache Data Protection Sample

Demonstrates configuring ASP.NET Core's **Data Protection API** to store its
key ring in **NCache** instead of the local file system. This is the
standard approach when an app runs across multiple servers/instances (web
farm, containers, Kubernetes pods, Azure App Service scale-out, etc.),
because every instance needs access to the same keys to decrypt cookies,
antiforgery tokens, or anything else protected on another instance.

## Prerequisites

1. A running NCache cluster/cache — see
   [NCache docs](https://www.alachisoft.com/resources/docs/) for setup.
2. A cache named `demoCache` created and registered in the client's
   `client.ncconf` (or update `appsettings.json` to match an existing cache).
3. .NET 6 SDK +.

## What's inside

| File | Purpose |
|---|---|
| `Program.cs` | Registers Data Protection and calls `.PersistKeysToNCache(cacheName, keyName)` so keys are read from / written to NCache. |
| `appsettings.json` | Holds the NCache cache name, application id, and the key used to store the key ring. |
| `ProtectionController.cs` | Minimal API demonstrating `IDataProtector.Protect` / `Unprotect`. |
| `NCacheDataProtectionSample.csproj` | References `Alachisoft.NCache.SDK` and `Alachisoft.NCache.AspNetCore.DataProtectionProvider`. |

## Running it

```bash
dotnet restore
dotnet run
```

Then run application on 2 different servers.

```
dotnet run --launch-profile Server1
dotnet run --launch-profile Server2
```

Open both websites

```
http://localhost:5101/
http://localhost:5102/
```

Now try to encrypt a text from Server1 (localhost:5101), and decrypt protected payload from Server2 (localhost:5102).

## Why NCache instead of the local file system?

By default, Data Protection keys are written to disk (or the registry on
Windows). That works fine for a single server, but breaks as soon as you
scale out: server B can't decrypt something server A encrypted, because it
has a different local key ring. Pointing Data Protection at a shared,
distributed cache like NCache solves this — every instance in the cluster
reads and writes the same key ring, so protected payloads (auth cookies,
CSRF tokens, encrypted query strings, etc.) work no matter which server
handles the request.

## Notes

- `SetApplicationName(...)` should be identical across all instances of the
  same app so they all derive the same key ring; different apps should use
  different names to isolate their keys.
- Keys are automatically encrypted at rest by Data Protection before being
  stored, and NCache further protects them via your cluster's own security
  configuration (TLS, authentication, etc., depending on your NCache edition).
- In production, also configure key rotation and (optionally)
  `.ProtectKeysWithCertificate(...)` for an extra layer of at-rest
  protection on top of NCache storage.
