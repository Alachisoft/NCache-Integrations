using NCache.OSS.CacheManager.Core;
using CacheManager;
using CacheManager.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using Serilog;

namespace CacheManagerMultiLayerDemo
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var services = new ServiceCollection();

            services.AddLogging(cfg =>
            {
                cfg.AddConsole();
                cfg.SetMinimumLevel(LogLevel.Information);
            });

            var provider = services.BuildServiceProvider();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

            var options = new NCacheOptions
            {
                CacheName = "demoCache",
                ServerList = new List<NCacheOptions.ServerConfig>
                        {
                            new NCacheOptions.ServerConfig
                            {
                                Ip = "127.0.0.1"
                            }
                        }
            }; 

            var cache = CacheFactory.Build<string>("myCache", settings =>
            {

                settings.WithDictionaryHandle();

                settings.WithHandle(
                    typeof(NCacheCacheHandle<>),
                    "ncache_handle_name",
                    true,
                    options);

                settings.WithBackplane(
                    typeof(NCacheCacheBackplane),
                    "ncache_config_key",
                    "example_topic_name",
                    options);

            },
            loggerFactory);

            // PUT
            CacheItem<string> item = new CacheItem<string>("product:1", "Gaming Laptop", ExpirationMode.Default, TimeSpan.FromSeconds(5));
            cache.Put(item);

            // GET
            var value = cache.Get("product:1");

            Console.WriteLine($"Value: {value}");

            // EXISTS
            Console.WriteLine($"product:1 exits: {cache.Exists("product:1")}");

            // REMOVE
            cache.Remove("product:1");

            // REGION PUT
            cache.Put(
                new CacheItem<string>(
                    "customer:1",
                    "customers",
                    "John Doe"));

            cache.Put(
                new CacheItem<string>(
                    "customer:2",
                    "customers",
                    "John Doe"));

            cache.Put(
                new CacheItem<string>(
                    "customer:3",
                    "customers",
                    "John Doe"));

            // REGION GET
            var customer = cache.Get<string>(
                "customer:1",
                "customers");

            Console.WriteLine($"Customer: {customer}");

            // REGION EXISTS
            bool exists = cache.Exists("customer:1", "customers");

            Console.WriteLine($"Exists: {exists}");

            // REGION REMOVE
            cache.ClearRegion(
                "customers");
            Console.WriteLine("Removed customer:1 with region customers");


            Console.ReadLine();
        }
    }
}