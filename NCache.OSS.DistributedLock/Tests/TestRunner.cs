using System;
using System.Collections.Generic;

namespace IDistribtuedLockTest
{
    /// <summary>
    /// Lightweight test runner — no external test framework required.
    /// Collects pass / fail / skip results and prints a summary.
    /// </summary>
    internal static class TestRunner
    {
        private static int _passed;
        private static int _failed;
        private static int _skipped;
        private static readonly List<string> _failures = new();

        // ── Assertion helpers ────────────────────────────────────────────────

        public static void AssertTrue(bool condition, string message)
        {
            if (!condition)
                throw new AssertionException($"Expected TRUE — {message}");
        }

        public static void AssertFalse(bool condition, string message)
        {
            if (condition)
                throw new AssertionException($"Expected FALSE — {message}");
        }

        public static void AssertNull(object? obj, string message)
        {
            if (obj != null)
                throw new AssertionException($"Expected NULL — {message}");
        }

        public static void AssertNotNull(object? obj, string message)
        {
            if (obj == null)
                throw new AssertionException($"Expected NOT NULL — {message}");
        }

        public static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertionException($"Expected [{expected}] but got [{actual}] — {message}");
        }

        public static void AssertThrows<TEx>(Action action, string message) where TEx : Exception
        {
            try
            {
                action();
                throw new AssertionException($"Expected {typeof(TEx).Name} to be thrown — {message}");
            }
            catch (TEx) { /* expected */ }
            catch (AssertionException) { throw; }
            catch (Exception ex)
            {
                throw new AssertionException(
                    $"Expected {typeof(TEx).Name} but got {ex.GetType().Name} — {message}");
            }
        }

        // ── Test registration ────────────────────────────────────────────────

        /// <summary>Run a named test case, catching and recording any failure.</summary>
        public static void Run(string name, Action test)
        {
            Console.Write($"  [{name}] ... ");
            try
            {
                test();
                _passed++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS");
            }
            catch (SkipException ex)
            {
                _skipped++;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"SKIP  ({ex.Message})");
            }
            catch (Exception ex)
            {
                _failed++;
                _failures.Add($"  • {name}: {ex.Message}");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL  — {ex.Message}");
            }
            finally
            {
                Console.ResetColor();
            }
        }

        /// <summary>Skip the current test with a reason.</summary>
        public static void Skip(string reason) => throw new SkipException(reason);

        // ── Summary ──────────────────────────────────────────────────────────

        public static void PrintSummary()
        {
            int total = _passed + _failed + _skipped;
            Console.WriteLine();
            Console.WriteLine(new string('═', 60));
            Console.WriteLine($"  Results: {total} tests  |  " +
                              $"Passed: {_passed}  |  Failed: {_failed}  |  Skipped: {_skipped}");
            if (_failures.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine();
                Console.WriteLine("  FAILURES:");
                foreach (var f in _failures)
                    Console.WriteLine(f);
                Console.ResetColor();
            }
            Console.WriteLine(new string('═', 60));
        }

        public static bool HasFailures => _failed > 0;

        // ── Internal exception types ─────────────────────────────────────────

        internal class AssertionException : Exception
        {
            public AssertionException(string msg) : base(msg) { }
        }

        internal class SkipException : Exception
        {
            public SkipException(string msg) : base(msg) { }
        }
    }
}
