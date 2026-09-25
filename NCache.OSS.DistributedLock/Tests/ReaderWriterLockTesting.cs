using Alachisoft.NCache.Client;
using NCache.DistributedLock.Providers;
using Medallion.Threading;

namespace IDistribtuedLockTest
{
    /// <summary>
    /// Integration tests for the distributed reader-writer lock
    /// (NCacheDistributedReaderWriterLock).
    ///
    /// Reader-writer lock contract:
    ///   • Multiple readers may hold the lock concurrently.
    ///   • A writer gets exclusive access — no readers and no other writers.
    ///   • A waiting writer blocks new readers from acquiring.
    /// </summary>
    internal class ReaderWriterLockTesting
    {
        public static void RunTests(ICache cache)
        {
            Console.WriteLine();
            Console.WriteLine("══ Reader-Writer Lock Tests ═════════════════════════════════");

            var provider = new NCacheDistributedSynchronizationProvider(cache);

            // ── TC-RW-01 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-RW-01: Read lock acquire returns a valid handle",
                () =>
                {
                    var rwl = provider.CreateReaderWriterLock($"rwl-tc01-{Guid.NewGuid()}");
                    using (var handle = rwl.AcquireReadLock(TimeSpan.FromSeconds(5)))
                    {
                        TestRunner.AssertNotNull(handle, "AcquireReadLock should return a non-null handle");
                    }
                });

            // ── TC-RW-02 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-RW-02: Write lock acquire returns a valid handle",
                () =>
                {
                    var rwl = provider.CreateReaderWriterLock($"rwl-tc02-{Guid.NewGuid()}");
                    using var handle = rwl.AcquireWriteLock(TimeSpan.FromSeconds(5));
                    TestRunner.AssertNotNull(handle, "AcquireWriteLock should return a non-null handle");
                });

            // ── TC-RW-03 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-RW-03: Multiple concurrent readers can hold the lock simultaneously",
                () =>
                {
                    string name = $"rwl-tc03-{Guid.NewGuid()}";
                    var rwl = provider.CreateReaderWriterLock(name);

                    int concurrentReaders = 0;
                    int maxObserved = 0;
                    var allReady = new CountdownEvent(3);
                    var release = new ManualResetEventSlim(false);

                    var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
                    {
                        using var h = rwl.AcquireReadLock(TimeSpan.FromSeconds(10));
                        int val = Interlocked.Increment(ref concurrentReaders);
                        // Track peak concurrent readers
                        int observed;
                        do { observed = maxObserved; }
                        while (observed < val && Interlocked.CompareExchange(ref maxObserved, val, observed) != observed);

                        allReady.Signal();
                        release.Wait();
                        Interlocked.Decrement(ref concurrentReaders);
                    })).ToList();

                    allReady.Wait(TimeSpan.FromSeconds(10));
                    release.Set();
                    Task.WhenAll(readers).Wait();

                    TestRunner.AssertEqual(3, maxObserved,
                        "All 3 readers should hold the lock at the same time");
                });

            // ── TC-RW-04 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-RW-04: Writer is exclusive — blocks while readers hold the lock",
                () =>
                {
                    string name = $"rwl-tc04-{Guid.NewGuid()}";
                    var rwl = provider.CreateReaderWriterLock(name);

                    bool writerRan = false;
                    bool readerStillHeld = false;

                    var readerReady = new ManualResetEventSlim(false);
                    var releaseReader = new ManualResetEventSlim(false);

                    var readerTask = Task.Run(() =>
                    {
                        using var h = rwl.AcquireReadLock(TimeSpan.FromSeconds(10));
                        readerReady.Set();
                        releaseReader.Wait();
                    });

                    readerReady.Wait();

                    var writerTask = Task.Run(() =>
                    {
                        using var h = rwl.AcquireWriteLock(TimeSpan.FromSeconds(10));
                        writerRan = true;
                    });

                    Thread.Sleep(500);
                    readerStillHeld = !writerRan;
                    releaseReader.Set();

                    Task.WhenAll(readerTask, writerTask).Wait();

                    TestRunner.AssertTrue(readerStillHeld,
                        "Writer should not acquire the lock while a reader holds it");
                    TestRunner.AssertTrue(writerRan,
                        "Writer should acquire after all readers release");
                });

            // ── TC-RW-05 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-RW-05: New readers are blocked while a writer is active",
                () =>
                {
                    string name = $"rwl-tc05-{Guid.NewGuid()}";
                    var rwl = provider.CreateReaderWriterLock(name);

                    bool readerAcquiredWhileWriterHeld = false;

                    var writerReady = new ManualResetEventSlim(false);
                    var releaseWriter = new ManualResetEventSlim(false);

                    var writerTask = Task.Run(() =>
                    {
                        using var h = rwl.AcquireWriteLock(TimeSpan.FromSeconds(10));
                        writerReady.Set();
                        releaseWriter.Wait();
                       h.Dispose();
                    });

                    writerReady.Wait();

                    var readerTask = Task.Run(() =>
                    {
                        using var h = rwl.AcquireReadLock(TimeSpan.FromSeconds(10));
                        readerAcquiredWhileWriterHeld = true;
                    });

                    Thread.Sleep(500);
                    bool readerBlockedCorrectly = !readerAcquiredWhileWriterHeld;
                    releaseWriter.Set();

                    Task.WhenAll(writerTask, readerTask).Wait();

                    TestRunner.AssertTrue(readerBlockedCorrectly,
                        "Reader should not acquire the lock while a writer is active");
                    TestRunner.AssertTrue(readerAcquiredWhileWriterHeld,
                        "Reader should succeed once the writer releases");
                });

            // ── TC-RW-06 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-RW-06: Two writers are mutually exclusive",
                () =>
                {
                    string name = $"rwl-tc06-{Guid.NewGuid()}";
                    var rwl = provider.CreateReaderWriterLock(name);

                    int concurrentWriters = 0;
                    bool exclusivityViolated = false;

                    var w1Ready = new ManualResetEventSlim(false);
                    var releaseW1 = new ManualResetEventSlim(false);

                    var w1 = Task.Run(() =>
                    {
                        using var h = rwl.AcquireWriteLock(TimeSpan.FromSeconds(10));
                        Interlocked.Increment(ref concurrentWriters);
                        w1Ready.Set();
                        releaseW1.Wait();
                        Interlocked.Decrement(ref concurrentWriters);
                    });

                    w1Ready.Wait();

                    var w2 = Task.Run(() =>
                    {
                        using var h = rwl.AcquireWriteLock(TimeSpan.FromSeconds(10));
                        int val = Interlocked.Increment(ref concurrentWriters);
                        if (val > 1) exclusivityViolated = true;
                        Interlocked.Decrement(ref concurrentWriters);
                    });

                    Thread.Sleep(500);
                    releaseW1.Set();

                    Task.WhenAll(w1, w2).Wait();

                    TestRunner.AssertFalse(exclusivityViolated,
                        "Two writers must never hold the lock simultaneously");
                });

            // ── TC-RW-07 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-RW-07: Read lock is released on Dispose — subsequent read lock succeeds",
                () =>
                {
                    string name = $"rwl-tc07-{Guid.NewGuid()}";
                    var rwl = provider.CreateReaderWriterLock(name);

                    var h1 = rwl.AcquireReadLock(TimeSpan.FromSeconds(5));
                    h1.Dispose();

                    bool acquired = false;
                    var t = Task.Run(() =>
                    {
                        using var h2 = rwl.AcquireReadLock(TimeSpan.FromSeconds(5));
                        acquired = true;
                    });

                    bool done = t.Wait(TimeSpan.FromSeconds(6));
                    TestRunner.AssertTrue(done, "Read acquire should complete quickly after Dispose");
                    TestRunner.AssertTrue(acquired, "Second read acquire should succeed");
                });

            // ── TC-RW-08 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-RW-08: Write lock acquire throws TimeoutException when readers block",
                () =>
                {
                    string name = $"rwl-tc08-{Guid.NewGuid()}";
                    var rwl = provider.CreateReaderWriterLock(name);

                    var release = new ManualResetEventSlim(false);

                    var reader = Task.Run(() =>
                    {
                        using var h = rwl.AcquireReadLock(TimeSpan.FromSeconds(15));
                        release.Wait();
                    });

                    Thread.Sleep(200);

                    try
                    {
                        TestRunner.AssertThrows<TimeoutException>(
                            () => rwl.AcquireWriteLock(TimeSpan.FromMilliseconds(500)),
                            "Write lock should time out when a reader is active");
                    }
                    finally
                    {
                        release.Set();
                        reader.Wait();
                    }
                });

            // ── TC-RW-09 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-RW-09: Two independent named locks do not interfere",
                () =>
                {
                    var rwlA = provider.CreateReaderWriterLock($"rwl-tc09-A-{Guid.NewGuid()}");
                    var rwlB = provider.CreateReaderWriterLock($"rwl-tc09-B-{Guid.NewGuid()}");

                    using var hA = rwlA.AcquireWriteLock(TimeSpan.FromSeconds(5));
                    using var hB = rwlB.AcquireWriteLock(TimeSpan.FromSeconds(5));

                    TestRunner.AssertNotNull(hA, "Lock A write handle should be valid");
                    TestRunner.AssertNotNull(hB, "Lock B write handle should be valid independently");
                });
        }
    }
}
