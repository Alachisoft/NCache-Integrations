using Alachisoft.NCache.Client;
using Dapper;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using NCache.OSS.Dapper;

namespace NCacheDapper_TestApp
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            // 1. Configure cache provider (IMPORTANT)
            var cache = CacheManager.GetCache("demoCache", new CacheConnectionOptions
            {
                ServerList = new List<ServerInfo>
                {
                    new ServerInfo("127.0.0.1", 9800)
                }
            });

            DapperNCache.CacheProvider = new NCacheCacheProvider(cache);

            using var conn = DbBootstrap.CreateConnection();

            // =======================================
            // 1. FIRST CALL (DB hit expected)
            // =======================================
            Console.WriteLine("FIRST CALL (DB expected)");
            var users1 = conn.QueryCached<User>(
                "SELECT * FROM Users");
            foreach (var u in users1)
                Console.WriteLine($"{u.Id} {u.Name} {u.Age}");

            // =======================================
            // 2. SECOND CALL (CACHE hit expected)
            // =======================================
            Console.WriteLine("\nSECOND CALL (CACHE expected)");
            var users2 = conn.QueryCached<User>(
                "SELECT * FROM Users");
            Console.WriteLine($"Count: {users2.Count()}");

            // =======================================
            // 3. FORCE UPDATE (DB hit expected)
            // =======================================
            Console.WriteLine("\nFORCE UPDATE (DB expected)");
            var users3 = conn.QueryCached<User>(
                "SELECT * FROM Users",
                options: new CacheOptions
                {
                    ForceUpdate = true
                });
            Console.WriteLine($"Count: {users3.Count()}");

            // =======================================
            // 4. CUSTOM PROVIDER PER CALL
            // =======================================
            Console.WriteLine("\nCUSTOM PROVIDER CALL");
            var cache2 = CacheManager.GetCache("demoLocalCache", new CacheConnectionOptions
            {
                ServerList = new List<ServerInfo>
                {
                    new ServerInfo("127.0.0.1", 9800)
                }
            });
            var customProvider = new NCacheCacheProvider(cache2);

            var users4 = conn.QueryCached<User>(
                "SELECT * FROM Users",
                customProvider);
            Console.WriteLine($"Count: {users4.Count()}");

            // =======================================
            // 5. STAMPEDE TEST (concurrent callers)
            // =======================================
            Console.WriteLine("\nSTAMPEDE TEST — press Enter to start");
            Console.ReadLine(); 

            await RunStampedeTest();

            Console.WriteLine("\nDONE");
        }

        static async Task RunStampedeTest()
        {
            const int concurrentCallers = 50;
            const string stampedeKey = "test-key"; 


            var barrier = new Barrier(concurrentCallers);

            var tasks = Enumerable.Range(0, concurrentCallers).Select(_ => Task.Run(() =>
            {
                using var localConn = DbBootstrap.CreateConnection();
                localConn.Open();

                barrier.SignalAndWait(); 

                return localConn.QueryCached<User>(
                    "SELECT * FROM Users",
                    customCacheKey: stampedeKey
                ).ToList();
            }));

            var sw = Stopwatch.StartNew();
            var results = await Task.WhenAll(tasks);
            sw.Stop();

            Console.WriteLine($"Elapsed: {sw.ElapsedMilliseconds}ms");
            Console.WriteLine($"Concurrent callers: {concurrentCallers}");
            Console.WriteLine("Now check sys.dm_exec_query_stats for the actual DB hit count.");
        }
    }

    public class User
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    public static class DbBootstrap
    {
        public static IDbConnection CreateConnection()
        {
            return new SqlConnection(
                "Server=localhost\\SQLEXPRESS;Database=NCache_Dapper;User Id=sa;Password=4Islamabad;Encrypt=False;");
        }
    }
}