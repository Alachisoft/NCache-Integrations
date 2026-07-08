using Alachisoft.NCache.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// Read NCache settings from appsettings.json
var cacheName = builder.Configuration["NCacheSettings:CacheName"] ?? "demoCache";
var appId = builder.Configuration["NCacheSettings:ApplicationId"] ?? "NCacheDataProtectionSample";
var keyName = builder.Configuration["NCacheSettings:KeyName"] ?? "DataProtection-Keys";

// -----------------------------------------------------------------------
// Register ASP.NET Core Data Protection and point it at NCache instead of
// the default local file system. This is required whenever the app runs
// on more than one server/instance (web farm, containers, k8s pods, etc.)
// so that every instance can decrypt cookies/tokens protected by any other
// instance.
// -----------------------------------------------------------------------
builder.Services.AddDataProtection()
    .SetApplicationName(appId)
    .PersistKeysToNCache(cacheName, keyName);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

var instanceName = Environment.GetEnvironmentVariable("INSTANCE_NAME") ?? "Server";
app.Logger.LogInformation("Starting instance: {InstanceName}", instanceName);

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthorization();
app.MapControllers();

app.Run();
