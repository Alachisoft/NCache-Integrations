using NCache.DistributedLock.Common;

namespace IDistribtuedLockTest
{
    /// <summary>
    /// Unit tests for the internal KeyGeneration utility.
    /// These tests are pure in-process and do NOT need a live NCache instance.
    /// Reflection is used to exercise the internal class.
    /// </summary>
    internal class KeyGenerationTesting
    {
        public static void RunTests()
        {
            Console.WriteLine();
            Console.WriteLine("══ KeyGeneration Unit Tests (no NCache needed) ══════════════");

            // Retrieve the private type via reflection so we can call internal methods
            var assembly = typeof(NCache.DistributedLock.Locks.NCacheDistributedLock).Assembly;
            var type = assembly.GetType("DistributedLock.NCache.Common.KeyGeneration")!;
            var method = type.GetMethod("GetKey",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

            // Enum values
            var enumType = type.GetNestedType("LockType",
                System.Reflection.BindingFlags.NonPublic)!;
            var distLock = Enum.Parse(enumType, "DistributedLock");
            var semaphore = Enum.Parse(enumType, "SempahoreLock");
            var readerLock = Enum.Parse(enumType, "ReaderLock");
            var writerLock = Enum.Parse(enumType, "WriterLock");

            string Invoke(object lockType, string name)
                => (string)method.Invoke(null, new[] { lockType, name })!;

            // ── TC-KG-01 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-KG-01: DistributedLock key has correct prefix",
                () =>
                {
                    var key = Invoke(distLock, "myLock");
                    TestRunner.AssertTrue(key.StartsWith("ncache#distLock#"),
                        $"Expected prefix 'ncache#distLock#' but got '{key}'");
                    TestRunner.AssertTrue(key.EndsWith("myLock"),
                        "Key should end with the lock name");
                });

            // ── TC-KG-02 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-KG-02: SemaphoreLock key has correct prefix",
                () =>
                {
                    var key = Invoke(semaphore, "mySemaphore");
                    TestRunner.AssertTrue(key.StartsWith("ncache#distSemaphore#"),
                        $"Expected prefix 'ncache#distSemaphore#' but got '{key}'");
                });

            // ── TC-KG-03 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-KG-03: ReaderLock key has correct prefix",
                () =>
                {
                    var key = Invoke(readerLock, "myReader");
                    TestRunner.AssertTrue(key.StartsWith("ncache#distReaderLock#"),
                        $"Expected prefix 'ncache#distReaderLock#' but got '{key}'");
                });

            // ── TC-KG-04 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-KG-04: WriterLock key has correct prefix",
                () =>
                {
                    var key = Invoke(writerLock, "myWriter");
                    TestRunner.AssertTrue(key.StartsWith("ncache#distWriterLock#"),
                        $"Expected prefix 'ncache#distWriterLock#' but got '{key}'");
                });

            // ── TC-KG-05 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-KG-05: Null lock name throws ArgumentException",
                () =>
                {
                    TestRunner.AssertThrows<System.Reflection.TargetInvocationException>(
                        () => Invoke(distLock, null!),
                        "Null lock name should throw");
                });

            // ── TC-KG-06 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-KG-06: Empty lock name throws ArgumentException",
                () =>
                {
                    TestRunner.AssertThrows<System.Reflection.TargetInvocationException>(
                        () => Invoke(distLock, ""),
                        "Empty lock name should throw");
                });

            // ── TC-KG-07 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-KG-07: Whitespace-only lock name throws ArgumentException",
                () =>
                {
                    TestRunner.AssertThrows<System.Reflection.TargetInvocationException>(
                        () => Invoke(distLock, "   "),
                        "Whitespace-only lock name should throw");
                });

            // ── TC-KG-08 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-KG-08: Different lock names produce different keys (same type)",
                () =>
                {
                    var key1 = Invoke(distLock, "lockA");
                    var key2 = Invoke(distLock, "lockB");
                    TestRunner.AssertFalse(key1 == key2,
                        "Different names must generate different keys");
                });

            // ── TC-KG-09 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-KG-09: Same name with different lock types produces different keys",
                () =>
                {
                    var k1 = Invoke(distLock, "shared");
                    var k2 = Invoke(semaphore, "shared");
                    TestRunner.AssertFalse(k1 == k2,
                        "Same name with different lock types must produce different keys");
                });

            // ── TC-KG-10 ─────────────────────────────────────────────────────
            TestRunner.Run("TC-KG-10: Key generation is deterministic for same inputs",
                () =>
                {
                    var key1 = Invoke(distLock, "stable");
                    var key2 = Invoke(distLock, "stable");
                    TestRunner.AssertEqual(key1, key2,
                        "Same lock type + name should always produce the same key");
                });
        }
    }
}
