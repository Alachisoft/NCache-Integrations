# NCache as Key Storage Provider for ASP.NET Core Data Protection

## Introduction

ASP.NET Core Data Protection is a security system that protects sensitive information through encryption. It manages encryption keys whose storage location can vary, Like file system, Azure Storage, or a custom repository. The `NCache.OSS.AspNetCore.DataProtection` package lets you use NCache as that custom key storage provider, so encryption keys are stored in the cache and shared across multiple instances of a web application running in a web farm.

 
## Package Versions

| Package                             | Version        |
| ----------------------------------- | -------------- |
| Target Framework                    | netstandard2.0 |
| Alachisoft.NCache.Opensource.SDK    | 5.3.6.2        |
| Microsoft.AspNetCore.DataProtection | 8.0.0          |


## Prerequisites

- NCache OSS v5.3.6.2 server running, with your target cache in **Started** state.
- An ASP.NET Core application with Data Protection configured.

## Usage

### Step 1: Configure your NCache connection

Three configuration files are copied to your output directory on build: `client.ncconf`, and  `config.ncconf`.

**`client.ncconf`** points the client at your NCache server. Set your server IP and make sure `cache id` matches the cache name you pass in code:

```xml
<cache id="myCache" enable-client-logs="False" log-level="error">
    <server name="YOUR_SERVER_IP"/>
</cache>
```

**`config.ncconf`**  holds cache side settings and ships with sensible defaults. Adjust only if your cache configuration requires it.

### Step 2: Register NCache as the key storage provider

In your `Program.cs`, chain `PersistKeysToNCache` onto `AddDataProtection`:

```csharp
builder.Services.AddDataProtection()
    .PersistKeysToNCache("myCache", "DataProtectionKeys");
```

- **First argument (`cacheName`)** the name of the cache where encryption keys will be stored. Must match the `cache id` in `client.ncconf`.
- **Second argument (`cacheTag`)** an arbitrary string used internally to group and retrieve all keys together. Every server instance in your web farm must use the same value.

Keys are stored as Base64 encoded XML elements and expire automatically based on the expiration time embedded in each key by the Data Protection framework. Revocation entries are stored without expiration.

## How It Works

`PersistKeysToNCache` registers an `NCacheXmlRepository` as the `IXmlRepository` for the Data Protection key management system. On startup, the repository connects to the specified cache. When the framework needs to store a new key, it calls `StoreElement`, which encodes the key XML to Base64 and inserts it into the cache under the provided tag. When the framework needs all current keys (e.g., to decrypt data), it calls `GetAllElements`, which retrieves every item grouped under that tag and deserializes them back to XML.

Tag based grouping is implemented using a lock protected list stored under the tag key itself, ensuring safe concurrent writes across multiple app instances.

## Sample

Demonstrates configuring ASP.NET Core's **Data Protection API** to store its
key ring in **NCache** instead of the local file system. This is the
standard approach when an app runs across multiple servers/instances (web
farm, containers, Kubernetes pods, Azure App Service scale-out, etc.),
because every instance needs access to the same keys to decrypt cookies,
antiforgery tokens, or anything else protected on another instance.

Follow the follwing instructions to run sample.

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

## Additional Resources

#### Documentation
http://www.alachisoft.com/resources/docs/#ncache

#### Programmer's Guide
http://www.alachisoft.com/resources/docs/ncache/prog-guide/

#### ASP.NET Core Data Protection
https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/introduction

#### Key Storage Providers
https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-storage-providers

## Technical Support

Alachisoft � provides various sources of technical support.
- Refer to http://www.alachisoft.com/support.html to select a support resource suited to your issue.
- To request additional features, or report a discrepancy in this document, email [support@alachisoft.com](mailto:support@alachisoft.com).

## License

Copyright � 2005-2026 Alachisoft. All rights reserved.