using Alachisoft.NCache.Client;
using NCache.DistributedLock.Locks;
using NCache.DistributedLock.Primitives;
using NCache.DistributedLock.Providers;
using IDistribtuedLockTest;
using Medallion.Threading;
using Microsoft.Win32;

// ════════════════════════════════════════════════════════════════════════════
//  Test Application Entry Point
//
//  Usage:
//    dotnet run              — run all tests (unit + integration)
//    dotnet run -- unit      — run only unit tests (no NCache connection)
//    dotnet run -- legacy    — run the original MutexLockTesting.Runner()
// ════════════════════════════════════════════════════════════════════════════

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

if (mode == "legacy")
{
    MutexLockTesting.Runner();
    return;
}

Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║      DistributedLock.NCache  —  Test Suite               ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝");

// ── Unit tests (always run, no NCache required) ───────────────────────────
//KeyGenerationTesting.RunTests();

if (mode == "unit")
{
    TestRunner.PrintSummary();
    return;
}

// ── Integration tests (require live NCache) ───────────────────────────────
Console.WriteLine();
Console.WriteLine("Connecting to NCache (\"democache\") …");

ICache? cache = null;
try
{
    cache = CacheManager.GetCache("democache");
    Console.WriteLine("Connected.");
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"WARNING: Could not connect to NCache — {ex.Message}");
    Console.WriteLine("Skipping all integration tests.");
    Console.ResetColor();
    TestRunner.PrintSummary();
    return;
}

var storing = new DistributedLockAcquisition(TimeSpan.FromSeconds(10));
cache.Insert("test", storing);
var test = cache.Get<DistributedLockAcquisition>("test");

if(test != null && test.IsEqual(storing))
{
    Console.WriteLine("Test Completes");
}
else
{
    Console.WriteLine("Test Base Fails");
}

MutexLockTesting.RunTests(cache);
SemaphoreLockTesting.RunTests(cache);
ReaderWriterLockTesting.RunTests(cache);

TestRunner.PrintSummary();

if (TestRunner.HasFailures)
    Environment.Exit(1);
