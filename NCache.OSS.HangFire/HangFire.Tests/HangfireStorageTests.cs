using Hangfire;
using Hangfire.Common;
using NCache.OSS.Hangfire;
using System.Collections.Concurrent;

namespace NCache.Hangfire.Tests;

public static class TestJobs
{
    public const string Queue = "ncache_tests";

    public static readonly ConcurrentDictionary<string, bool> Completed = new();

    [Queue(Queue)]
    public static void Succeed(string token) => Completed[token] = true;

    [Queue(Queue)]
    [AutomaticRetry(Attempts = 0)]
    public static void Fail() => throw new InvalidOperationException("Intentional failure");
}

public class HangfireStorageTests : IDisposable
{
    private readonly NCacheStorage _storage;
    private readonly BackgroundJobServer _server;
    private readonly BackgroundJobClient _client;

    public HangfireStorageTests()
    {
        _storage = new NCacheStorage("defaultCache", new NCacheStorageOptions
        {
            QueuePollInterval = TimeSpan.FromSeconds(1)
        });

        _client = new BackgroundJobClient(_storage);
        _server = new BackgroundJobServer(
            new BackgroundJobServerOptions { WorkerCount = 2, Queues = new[] { TestJobs.Queue } },
            _storage);
    }

    public void Dispose() => _server.Dispose();

    private string? WaitForState(string jobId, string state)
    {
        var api = _storage.GetMonitoringApi();
        for (var i = 0; i < 40; i++)
        {
            var current = api.JobDetails(jobId)?.History.FirstOrDefault()?.StateName;
            if (current == state) return current;
            Thread.Sleep(500);
        }
        return api.JobDetails(jobId)?.History.FirstOrDefault()?.StateName;
    }

    [Fact]
    public void EnqueuedJob_RunsAndSucceeds()
    {
        var token = Guid.NewGuid().ToString("N");

        var jobId = _client.Enqueue(() => TestJobs.Succeed(token));

        Assert.Equal("Succeeded", WaitForState(jobId, "Succeeded"));
        Assert.True(TestJobs.Completed.ContainsKey(token));
    }

    [Fact]
    public void ThrowingJob_EndsInFailedState()
    {
        var jobId = _client.Enqueue(() => TestJobs.Fail());

        Assert.Equal("Failed", WaitForState(jobId, "Failed"));
    }

    [Fact]
    public void ScheduledJob_RunsAfterDelay()
    {
        var token = Guid.NewGuid().ToString("N");

        var jobId = _client.Schedule(() => TestJobs.Succeed(token), TimeSpan.FromSeconds(2));

        Assert.Equal("Succeeded", WaitForState(jobId, "Succeeded"));
        Assert.True(TestJobs.Completed.ContainsKey(token));
    }

    [Fact]
    public void DeletedJob_MovesToDeletedState()
    {
        var jobId = _client.Schedule(() => TestJobs.Succeed("never"), TimeSpan.FromMinutes(10));

        Assert.True(_client.Delete(jobId));

        Assert.Equal("Deleted", WaitForState(jobId, "Deleted"));
    }
}
