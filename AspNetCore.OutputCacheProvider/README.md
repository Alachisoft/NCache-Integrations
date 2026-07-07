# NCache Output Cache Provider for ASP.NET Core

## Introduction
This provider lets ASP.NET Core's built in output caching feature use NCache as its backing store, via extension methods on `IServiceCollection`. It replaces the default in memory output cache store, so cached responses can be shared across multiple server instances instead of being held per process.
 
## Package Versions

| Package                            | Version                        |
| ---------------------------------- | ------------------------------ |
| Target Framework                   | .NET 8                         |
| Microsoft.AspNetCore.OutputCaching | .NET 7+ (feature availability) |
| Alachisoft.NCache.Opensource.SDK   | 5.3.6.2                        |


## Limitation
Tag based eviction is not supported, calling `EvictByTagAsync` (e.g. via `[OutputCache(Tags = ...)]`) throws `NotImplementedException`.

## Prerequisites
- NCache OSS server running, with your target cache in **Started** state.
- An ASP.NET Core (.NET 8) application.

## Installation
Add a reference to `Alachisoft.NCache.OutputCacheProvider`.

## Usage

**Step 1: Enable output caching and register NCache as the store**

Configure inline in code:

```csharp
builder.Services.AddOutputCache();
builder.Services.AddNCacheOutputCacheProvider(options =>
{
    options.CacheName = "myCache";
});
```

...or bind from configuration instead:

```csharp
builder.Services.AddOutputCache();
builder.Services.AddNCacheOutputCacheProvider(builder.Configuration.GetSection("NCacheOutputCache"));
```
```json
{
  "NCacheOutputCache": {
    "CacheName": "myCache"
  }
}
```

`AddOutputCache()` is still required.`AddNCacheOutputCacheProvider()` only swaps the storage backend, it doesn't enable output caching by itself.

**Step 2: Point the client at your server**

Set your NCache server IP in `client.ncconf` (copied to the output directory on build):

```xml
<cache id="myCache" enable-client-logs="False" log-level="error">
    <server name="YOUR_SERVER_IP"/>
</cache>
```

`cache id` must match `CacheName`. A `tls.ncconf` is also included for TLS enabled deployments. A server list can alternatively be set in code via `options.ServerList`, which overrides `client.ncconf`.

**Step 3: Enable the middleware and cache an endpoint**

```csharp
app.UseOutputCache();
```

Then mark endpoints as cacheable using ASP.NET Core's standard `[OutputCache]` attribute or `.CacheOutput()`, this part is unrelated to NCache and works the same as with any output cache store.

## Advanced Configuration
`EnabledLogs` and `EnableDetailLogs` on `NCacheOutputCacheOptions` turn on NCache provider logging (off by default). See the Programmers' Guide below for more.

## Resources

- [NCache Documentation](https://www.alachisoft.com/resources/docs/)
- [NCache ASP.NET Core OutputCache Documentation](https://www.alachisoft.com/resources/docs/ncache/prog-guide/output-cache.html)
- [NCache Open Source](https://github.com/Alachisoft/NCache)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)

#### NCache Doc

## Technical Support
Alachisoft � provides various sources of technical support.
- Refer to http://www.alachisoft.com/support.html to select a support resource suited to your issue.
- To request additional features, or report a discrepancy in this document, email [support@alachisoft.com](mailto:support@alachisoft.com).

## License 

Copyright � 2005-2026 Alachisoft. All rights reserved.