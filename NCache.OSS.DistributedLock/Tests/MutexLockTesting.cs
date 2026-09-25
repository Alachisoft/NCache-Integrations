using Alachisoft.NCache.Client;
using NCache.DistributedLock.Providers;
using Medallion.Threading;

namespace IDistribtuedLockTest
{
    /// <summary>
    /// Functional / integration smoke-test for the basic distributed mutex lock
    /// (NCacheDistributedLock). These tests require a live NCache instance.
    /// </summary>
    internal class MutexLockTesting
    {
        // ── Entry point kept for backward compatibility ───────────────────────

        public static void Runner()
        {
            ICache cache = CacheManager.GetCache("democache");

            NCacheDistributedSynchronizationProvider provider = new(cache);

            IDistributedLock distLock = provider.CreateLock("criticalsection1");

            var task1 = Task.Run(() =>
            {
                try
                {
                    using (var handle = distLock.Acquire(TimeSpan.FromSeconds(7)))
                    {
                        Console.WriteLine("Lock 1 Acquired");
                        Thread.Sleep(5000);
                    }
                    Console.WriteLine("Lock 1 Released");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    Console.WriteLine("First Failed");
                }
            });

            var task2 = Task.Run(() =>
            {
                try
                {
                    using (var handle = distLock.Acquire(TimeSpan.FromSeconds(7)))
                    {
                        Console.WriteLine("Lock 2 Acquired");
                        Thread.Sleep(5000);
                    }
                    Console.WriteLine("Lock 2 Released");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    Console.WriteLine("Second Failed");
                }
            });

            Task.WhenAll(task1, task2).Wait();

            Console.ReadKey();
        }

        // ── Test suite ────────────────────────────────────────────────────────

        public static void RunTests(ICache cache)
        {
            Console.WriteLine();
            Console.WriteLine("══ Mutex (Distributed Lock) Tests ══════════════════════════");

            var provider = new NCacheDistributedSynchronizationProvider(cache);

            TestRunner.Run("TC-ML-01: Lock acquired returns non-null handle",
                () =>
                {
                    var lck = provider.CreateLock($"mutex-tc01-{Guid.NewGuid()}");
                    using var handle = lck.Acquire(TimeSpan.FromSeconds(5));
                    TestRunner.AssertNotNull(handle, "Acquire should return a valid handle");
                });

            TestRunner.Run("TC-ML-02: Lock is exclusive — second acquire blocks until first is released",
                () =>
                {
                    string lockName = $"mutex-tc02-{Guid.NewGuid()}";
                    var lck = provider.CreateLock(lockName);

                    bool task2Ran = false;
                    bool task1StillHeld = false;

                    // Task 1 holds the lock for 3 s
                    var t1 = Task.Run(() =>
                    {
                        using var handle = lck.Acquire(TimeSpan.FromSeconds(10));
                        Thread.Sleep(3000);
                        // At this point task 2 should NOT have run yet
                        task1StillHeld = !task2Ran;
                    });

                    Thread.Sleep(200); // ensure T1 gets the lock first

                    // Task 2 waits up to 7 s
                    var t2 = Task.Run(() =>
                    {
                        using var handle = lck.Acquire(TimeSpan.FromSeconds(7));
                        task2Ran = true;
                    });

                    Task.WhenAll(t1, t2).Wait();

                    TestRunner.AssertTrue(task2Ran, "Task 2 should eventually acquire the lock");
                    TestRunner.AssertTrue(task1StillHeld,
                        "Task 2 should not run while Task 1 holds the lock");
                });

            TestRunner.Run("TC-ML-03: Lock is released when handle is disposed",
                () =>
                {
                    string lockName = $"mutex-tc03-{Guid.NewGuid()}";
                    var lck = provider.CreateLock(lockName);

                    // Acquire and immediately dispose
                    var handle = lck.Acquire(TimeSpan.FromSeconds(5));
                    handle.Dispose();

                    // A second acquire should succeed without waiting
                    bool secondAcquired = false;
                    var t = Task.Run(() =>
                    {
                        using var h2 = lck.Acquire(TimeSpan.FromSeconds(3));

                        secondAcquired = true;
                    });

                    bool completedInTime = t.Wait(TimeSpan.FromSeconds(5));
                    TestRunner.AssertTrue(completedInTime, "Second acquire should complete quickly");
                    TestRunner.AssertTrue(secondAcquired, "Second handle should be non-null");
                });

            TestRunner.Run("TC-ML-04: Acquire throws TimeoutException when lock unavailable",
                () =>
                {
                    string lockName = $"mutex-tc04-{Guid.NewGuid()}";
                    var lck = provider.CreateLock(lockName);

                    // Hold the lock for 10 s on a background thread
                    var holderReady = new ManualResetEventSlim(false);
                    var releaseHolder = new ManualResetEventSlim(false);

                    var holder = Task.Run(() =>
                    {
                        using var h = lck.Acquire(TimeSpan.FromSeconds(15));
                        holderReady.Set();
                        releaseHolder.Wait();
                    });

                    holderReady.Wait();

                    try
                    {
                        // Try with a very short timeout — should time out
                        TestRunner.AssertThrows<TimeoutException>(
                            () => lck.Acquire(TimeSpan.FromMilliseconds(500)),
                            "Should throw TimeoutException when lock cannot be acquired in time");
                    }
                    finally
                    {
                        releaseHolder.Set();
                        holder.Wait();
                    }
                });

            TestRunner.Run("TC-ML-05: Multiple independent locks do not interfere",
                () =>
                {
                    var lockA = provider.CreateLock($"mutex-tc05-A-{Guid.NewGuid()}");
                    var lockB = provider.CreateLock($"mutex-tc05-B-{Guid.NewGuid()}");

                    bool bothAcquired = false;

                    using var hA = lockA.Acquire(TimeSpan.FromSeconds(5));
                    using var hB = lockB.Acquire(TimeSpan.FromSeconds(5));
                    bothAcquired = true;

                    TestRunner.AssertTrue(bothAcquired, "Two different locks should be acquirable simultaneously");
                });

            TestRunner.Run("TC-ML-06: Re-acquire after release succeeds",
                () =>
                {
                    string lockName = $"mutex-tc06-{Guid.NewGuid()}";
                    var lck = provider.CreateLock(lockName);

                    for (int i = 0; i < 3; i++)
                    {
                        using var handle = lck.Acquire(TimeSpan.FromSeconds(5));
                        TestRunner.AssertNotNull(handle, $"Acquire attempt #{i + 1} should succeed");
                    }
                });

            TestRunner.Run("TC-ML-07: Two tasks race — exactly one acquires at a time",
                () =>
                {
                    string lockName = $"mutex-tc07-{Guid.NewGuid()}";
                    var lck = provider.CreateLock(lockName);

                    int concurrentHolders = 0;
                    bool exclusivityViolated = false;
                    var exceptions = new List<Exception>();
                    var tasks = new List<Task>();

                    for (int i = 0; i < 4; i++)
                    {
                        tasks.Add(Task.Run(() =>
                        {
                            try
                            {
                                using var handle = lck.Acquire(TimeSpan.FromSeconds(15));
                                int current = Interlocked.Increment(ref concurrentHolders);
                                if (current > 1) exclusivityViolated = true;
                                Thread.Sleep(300);
                                Interlocked.Decrement(ref concurrentHolders);
                            }
                            catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
                        }));
                    }

                    Task.WhenAll(tasks).Wait();

                    TestRunner.AssertFalse(exclusivityViolated,
                        "At no point should more than one thread hold the mutex");
                    TestRunner.AssertEqual(0, exceptions.Count,
                        "No unexpected exceptions should occur");
                });
        }
    }
}
