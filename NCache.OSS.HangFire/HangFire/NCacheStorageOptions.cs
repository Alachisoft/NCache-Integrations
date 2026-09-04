//NCacheStoarageOptions.cs
using System;

public class NCacheStorageOptions
{
    public string CacheName { get; internal set; }

    public TimeSpan QueuePollInterval { get; set; } = TimeSpan.FromSeconds(60);
     

    public TimeSpan InvisibilityTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan DistributedLockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal string Prefix { get; set; } = "hangfire:ncache:";
    public bool EnablePubSubNotifications { get; set; } = true;
    public int SucceededListSize { get; set; } = 10000;
    public int DeletedListSize { get; set; } = 1000;
    public int FailedListSize { get; set; } = 1000;

    public TimeSpan LockAcquireMaxWait { get; set; } = TimeSpan.FromSeconds(15);
}