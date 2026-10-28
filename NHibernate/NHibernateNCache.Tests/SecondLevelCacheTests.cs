using NHibernate;
using NHibernate.Cfg;
using NHibernate.Dialect;
using NHibernate.Driver;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using NHibernate.Tool.hbm2ddl;
using System;
using System.IO;
using Xunit;

namespace NCache.NHibernate.Tests
{
    public class Customer
    {
        public virtual int Id { get; set; }
        public virtual string Name { get; set; }
    }

    public class CustomerMap : ClassMapping<Customer>
    {
        public CustomerMap()
        {
            Table("Customers");
            Cache(c => { c.Usage(CacheUsage.ReadWrite); c.Region("default"); });
            Id(x => x.Id, m => m.Generator(Generators.Assigned));
            Property(x => x.Name);
        }
    }

    public class SecondLevelCacheTests : IDisposable
    {
        private readonly string _dbFile = Path.Combine(Path.GetTempPath(), "nh-ncache-" + Guid.NewGuid().ToString("N") + ".db");
        private readonly ISessionFactory _factory;

        public SecondLevelCacheTests()
        {
            var cfg = new Configuration();
            cfg.DataBaseIntegration(db =>
            {
                db.Dialect<SQLiteDialect>();
                db.Driver<SQLite20Driver>();
                db.ConnectionString = "Data Source=" + _dbFile + ";Version=3;";
            });
            cfg.SetProperty("cache.provider_class",
                "Alachisoft.NCache.Integrations.NHibernate.Cache.NCacheProvider, Alachisoft.NCache.Integrations.NHibernate.Cache");
            cfg.SetProperty("cache.use_second_level_cache", "true");
            cfg.SetProperty("generate_statistics", "true");

            var mapper = new ModelMapper();
            mapper.AddMapping<CustomerMap>();
            cfg.AddMapping(mapper.CompileMappingForAllExplicitlyAddedEntities());

            new SchemaExport(cfg).Create(false, true);
            _factory = cfg.BuildSessionFactory();
        }

        public void Dispose()
        {
            _factory.Dispose();
            try { File.Delete(_dbFile); } catch (IOException) { }
        }

        private static int NewId() => Math.Abs(Guid.NewGuid().GetHashCode());

        [Fact]
        public void EvictedEntity_IsReloadedFromDatabaseAndCachedAgain()
        {
            var id = NewId();
            using (var session = _factory.OpenSession())
            using (var tx = session.BeginTransaction())
            {
                session.Save(new Customer { Id = id, Name = "John" });
                tx.Commit();
            }
            _factory.Evict(typeof(Customer), id);
            _factory.Statistics.Clear();

            using (var session = _factory.OpenSession())
                Assert.Equal("John", session.Get<Customer>(id).Name);

            Assert.True(_factory.Statistics.SecondLevelCacheMissCount >= 1);
            Assert.True(_factory.Statistics.SecondLevelCachePutCount >= 1);
        }

        [Fact]
        public void Entity_IsServedFromSecondLevelCache_InANewSession()
        {
            var id = NewId();
            using (var session = _factory.OpenSession())
            using (var tx = session.BeginTransaction())
            {
                session.Save(new Customer { Id = id, Name = "Jane" });
                tx.Commit();
            }
            using (var session = _factory.OpenSession())
                session.Get<Customer>(id);
            _factory.Statistics.Clear();

            using (var session = _factory.OpenSession())
                Assert.Equal("Jane", session.Get<Customer>(id).Name);

            Assert.True(_factory.Statistics.SecondLevelCacheHitCount >= 1);
        }

        [Fact]
        public void UpdatedEntity_IsRefreshedInCache()
        {
            var id = NewId();
            using (var session = _factory.OpenSession())
            using (var tx = session.BeginTransaction())
            {
                session.Save(new Customer { Id = id, Name = "Before" });
                tx.Commit();
            }
            using (var session = _factory.OpenSession())
                session.Get<Customer>(id);

            using (var session = _factory.OpenSession())
            using (var tx = session.BeginTransaction())
            {
                session.Get<Customer>(id).Name = "After";
                tx.Commit();
            }

            using (var session = _factory.OpenSession())
                Assert.Equal("After", session.Get<Customer>(id).Name);
        }
    }
}
