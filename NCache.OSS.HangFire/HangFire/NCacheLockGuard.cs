//NCacheLockGuard.cs
using Alachisoft.NCache.Client;
using Alachisoft.NCache.Client.Extension;
using Alachisoft.NCache.Common.Interop;
using Alachisoft.NCache.Runtime.Exceptions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
namespace NCache.OSS.Hangfire
{
    internal static class NCacheLockGuard
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
           new ConcurrentDictionary<string, SemaphoreSlim>();

        public static SemaphoreSlim For(string key) =>
            _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
         
        internal static void EnsureKeyExists<T>(ICache cache, string key, Func<T> factory)
        {
            var gate = For(key);
            gate.Wait();
            try
            {
                if (cache.Contains(key)) return;
                try
                {
                    cache.Add(key, new CacheItem(factory()));
                }
                catch (OperationFailedException)
                {
                    // lost the race to a concurrent process — fine, key exists now
                }
            }
            finally
            {
                gate.Release();
            }
        }

        internal static void Execute(ICache cache, string key, TimeSpan lockDuration, TimeSpan maxWait, Action action)
        {
            var gate = For(key);
            gate.Wait();
            try
            {
                var token = Acquire(cache, key, lockDuration, maxWait);
                try
                {
                    action();
                }
                finally
                {
                    cache.UnlockKey(key, token);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        internal static bool TryExecute(ICache cache, string key, TimeSpan lockDuration, TimeSpan maxWait, Action action)
        {
            try
            {
                Execute(cache, key, lockDuration, maxWait, action);
                return true;
            }
            catch (Alachisoft.NCache.Runtime.Exceptions.TimeoutException)
            {
                return false;
            }
        }


        internal static LockToken Acquire(ICache cache, string key, TimeSpan lockDuration, TimeSpan maxWait)
        {
            var stopwatch = Stopwatch.StartNew();
            var random = new Random(Guid.NewGuid().GetHashCode());
            string lockKey = NCacheKeys.WrapperLock(key);

            while (true)
            {
                if (!cache.Contains(lockKey))   // cheap check — skip the throw when we can already tell it's held
                {
                    try
                    {
                        if (cache.LockKey(key, out var token, lockDuration))
                            return token;
                    }
                    catch (KeyNotFoundException)
                    {
                        // unchanged — see original comment
                    }
                }

                if (stopwatch.Elapsed >= maxWait)
                    throw new Alachisoft.NCache.Runtime.Exceptions.TimeoutException(
                        $"Could not acquire lock on '{key}' within {maxWait}.");

                Thread.Sleep(TimeSpan.FromMilliseconds(20 + random.Next(50)));
            }
        }
    }
}