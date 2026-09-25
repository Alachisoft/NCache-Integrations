using Alachisoft.NCache.Client;
using NCache.DistributedLock.Providers;
using Medallion.Threading;

namespace IDistribtuedLockTest
{
    /// <summary>
    /// Integration tests for the distributed semaphore (NCacheDistributedSemaphore).
    /// Validates capacity enforcement, concurrent slot management, release behaviour,
    /// timeout handling, and dangling-lock cleanup.
    /// </summary>
    internal class SemaphoreLockTesting
    {
        public static void RunTests(ICache cache)
        {
            Console.WriteLine();
            Console.WriteLine("══ Semaphore Tests ══════════════════════════════════════════");

            var provider = new NCacheDistributedSynchronizationProvider(cache);

            // ── TC-SM-01 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-SM-01: Acquire up to maxCount slots — all succeed",
                () =>
                {
                    int maxCount = 3;
                    string name = $"sem-tc01-{Guid.NewGuid()}";
                    var sem = provider.CreateSemaphore(name, maxCount);

                    var handles = new List<IDistributedSynchronizationHandle>();
                    try
                    {
                        for (int i = 0; i < maxCount; i++)
                        {
                            var h = sem.Acquire(TimeSpan.FromSeconds(5));
                            TestRunner.AssertNotNull(h, $"Slot {i + 1} of {maxCount} should be acquired");
                            handles.Add(h);
                        }
                    }
                    finally
                    {
                        foreach (var h in handles) h.Dispose();
                    }
                });

            // ── TC-SM-02 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-SM-02: Exceeding maxCount blocks until a slot is freed",
                () =>
                {
                    int maxCount = 2;
                    string name = $"sem-tc02-{Guid.NewGuid()}";
                    var sem = provider.CreateSemaphore(name, maxCount);

                    bool thirdAcquired = false;
                    bool firstTwoStillHeld = false;

                    var slotReady = new CountdownEvent(maxCount);
                    var releaseTrigger = new ManualResetEventSlim(false);

                    // Fill all slots
                    var holders = Enumerable.Range(0, maxCount).Select(_ => Task.Run(() =>
                    {
                        using var h = sem.Acquire(TimeSpan.FromSeconds(10));
                        slotReady.Signal();
                        releaseTrigger.Wait();
                    })).ToList();

                    slotReady.Wait(); // all slots taken

                    // Third acquire waits for a slot
                    var waiter = Task.Run(() =>
                    {
                        using var h = sem.Acquire(TimeSpan.FromSeconds(10));
                        thirdAcquired = true;
                    });

                    Thread.Sleep(500); // waiter should still be blocked
                    firstTwoStillHeld = !thirdAcquired;

                    releaseTrigger.Set(); // release holders
                    Task.WhenAll(holders).Wait();
                    waiter.Wait(TimeSpan.FromSeconds(10));

                    TestRunner.AssertTrue(firstTwoStillHeld,
                        "Third acquire should be blocked while all slots are taken");
                    TestRunner.AssertTrue(thirdAcquired,
                        "Third acquire should succeed once a slot is released");
                });

            // ── TC-SM-03 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-SM-03: Slot is freed on Dispose — subsequent acquire succeeds",
                () =>
                {
                    string name = $"sem-tc03-{Guid.NewGuid()}";
                    var sem = provider.CreateSemaphore(name, 1);

                    var h1 = sem.Acquire(TimeSpan.FromSeconds(5));
                    h1.Dispose();

                    bool acquired = false;
                    var t = Task.Run(() =>
                    {
                        using var h2 = sem.Acquire(TimeSpan.FromSeconds(3));
                        acquired = true;
                    });

                    bool done = t.Wait(TimeSpan.FromSeconds(5));
                    TestRunner.AssertTrue(done, "Second acquire should complete quickly after dispose");
                    TestRunner.AssertTrue(acquired, "Second acquire should succeed");
                });

            // ── TC-SM-04 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-SM-04: Timeout throws when all slots are occupied",
                () =>
                {
                    string name = $"sem-tc04-{Guid.NewGuid()}";
                    var sem = provider.CreateSemaphore(name, 1);

                    var release = new ManualResetEventSlim(false);

                    var holder = Task.Run(() =>
                    {
                        using var h = sem.Acquire(TimeSpan.FromSeconds(15));
                        release.Wait();
                    });

                    Thread.Sleep(200);

                    try
                    {
                        TestRunner.AssertThrows<TimeoutException>(
                            () => sem.Acquire(TimeSpan.FromMilliseconds(500)),
                            "Should throw TimeoutException when no slot available in time");
                    }
                    finally
                    {
                        release.Set();
                        holder.Wait();
                    }
                });

            // ── TC-SM-05 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-SM-05: MaxCount property matches constructor argument",
                () =>
                {
                    string name = $"sem-tc05-{Guid.NewGuid()}";
                    var sem = provider.CreateSemaphore(name, 5);
                    TestRunner.AssertEqual(5, sem.MaxCount,
                        "MaxCount should equal the value passed to the constructor");
                });

            // ── TC-SM-06 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-SM-06: Concurrent acquires do not exceed maxCount simultaneously",
                () =>
                {
                    int maxCount = 3;
                    string name = $"sem-tc06-{Guid.NewGuid()}";
                    var sem = provider.CreateSemaphore(name, maxCount);

                    int current = 0;
                    bool capacityViolated = false;
                    var tasks = new List<Task>();

                    for (int i = 0; i < 6; i++)
                    {
                        tasks.Add(Task.Run(() =>
                        {
                            using var h = sem.Acquire(TimeSpan.FromSeconds(20));
                            int val = Interlocked.Increment(ref current);
                            if (val > maxCount) capacityViolated = true;
                            Thread.Sleep(300);
                            Interlocked.Decrement(ref current);
                        }));
                    }

                    Task.WhenAll(tasks).Wait();

                    TestRunner.AssertFalse(capacityViolated,
                        "Concurrent holders should never exceed maxCount");
                });

            // ── TC-SM-07 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-SM-07: Slots are fully reusable after all are released",
                () =>
                {
                    int maxCount = 2;
                    string name = $"sem-tc07-{Guid.NewGuid()}";
                    var sem = provider.CreateSemaphore(name, maxCount);

                    // Round 1
                    var h1 = sem.Acquire(TimeSpan.FromSeconds(5));
                    var h2 = sem.Acquire(TimeSpan.FromSeconds(5));
                    h1.Dispose();
                    h2.Dispose();

                    // Round 2 — same slots should be available again
                    bool round2OK = false;
                    var h3 = sem.Acquire(TimeSpan.FromSeconds(5));
                    var h4 = sem.Acquire(TimeSpan.FromSeconds(5));
                    round2OK = true;
                    h3.Dispose();
                    h4.Dispose();

                    TestRunner.AssertTrue(round2OK,
                        "All semaphore slots should be reusable after release");
                });

            // ── TC-SM-08 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-SM-08: Two semaphores with different names are independent",
                () =>
                {
                    string nameA = $"sem-tc08-A-{Guid.NewGuid()}";
                    string nameB = $"sem-tc08-B-{Guid.NewGuid()}";

                    var semA = provider.CreateSemaphore(nameA, 1);
                    var semB = provider.CreateSemaphore(nameB, 1);

                    using var hA = semA.Acquire(TimeSpan.FromSeconds(5));
                    using var hB = semB.Acquire(TimeSpan.FromSeconds(5));

                    TestRunner.AssertNotNull(hA, "Semaphore A should be acquirable");
                    TestRunner.AssertNotNull(hB, "Semaphore B should be acquirable independently");
                });

            TestRunner.Run("TC-SM-09: Epired semaphore",
                () =>
                {
                    string name = $"sem-tc08-A-{Guid.NewGuid()}";

                    var sem = provider.CreateSemaphore(name, 1);

                    using var hA = sem.Acquire(TimeSpan.FromSeconds(1));

                    Thread.Sleep(TimeSpan.FromSeconds(30));

                    using var hB = sem.Acquire(TimeSpan.FromSeconds(5));

                    TestRunner.AssertNotNull(hB, "Semaphore B should be acquired");
                });
        }
    }
}
