using Alachisoft.NCache.EntityFrameworkCore;
using Alachisoft.NCache.Runtime.Caching;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace NCache.EntityFrameworkCore.Tests;

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class TestContext : DbContext
{
    private readonly string _dbFile;

    public TestContext(string dbFile) => _dbFile = dbFile;

    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        NCacheConfiguration.Configure("defaultCache", DependencyType.Other, errorEnabled: true);
        optionsBuilder.UseSqlite($"Data Source={_dbFile}");
    }
}

public class QueryCachingTests : IDisposable
{
    private readonly string _dbFile = Path.Combine(Path.GetTempPath(), $"efcore-ncache-{Guid.NewGuid():N}.db");

    public QueryCachingTests()
    {
        using var context = new TestContext(_dbFile);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbFile); } catch (IOException) { }
    }

    private int AddCustomer(string name)
    {
        using var context = new TestContext(_dbFile);
        var customer = new Customer { Name = name };
        context.Customers.Add(customer);
        context.SaveChanges();
        return customer.Id;
    }

    private void DeleteCustomer(int id)
    {
        using var context = new TestContext(_dbFile);
        context.Customers.Where(c => c.Id == id).ExecuteDelete();
    }

    // The cache key is built from the expression text, so the value must be a constant rather than a captured variable.
    private static IQueryable<Customer> Named(TestContext context, string name)
    {
        var customer = Expression.Parameter(typeof(Customer), "c");
        var predicate = Expression.Lambda<Func<Customer, bool>>(
            Expression.Equal(Expression.Property(customer, nameof(Customer.Name)), Expression.Constant(name)),
            customer);

        return context.Customers.Where(predicate);
    }

    private static CachingOptions Options() => new() { StoreAs = StoreAs.Collection };

    [Fact]
    public void FromCache_ReturnsDatabaseResults()
    {
        var name = $"cust-{Guid.NewGuid():N}";
        AddCustomer(name);

        using var context = new TestContext(_dbFile);
        var result = Named(context, name).FromCache(Options()).ToList();

        Assert.Single(result);
        Assert.Equal(name, result[0].Name);
    }

    [Fact]
    public void FromCache_SecondQuery_IsServedFromCache()
    {
        var name = $"cust-{Guid.NewGuid():N}";
        var id = AddCustomer(name);

        using (var context = new TestContext(_dbFile))
            Named(context, name).FromCache(Options()).ToList();

        // The row is gone from the database, so a hit can only come from NCache.
        DeleteCustomer(id);

        using (var context = new TestContext(_dbFile))
        {
            var cached = Named(context, name).FromCache(Options()).ToList();
            Assert.Single(cached);
        }
    }

    [Fact]
    public void RemovedCacheKey_ForcesReloadFromDatabase()
    {
        var name = $"cust-{Guid.NewGuid():N}";
        var id = AddCustomer(name);

        using (var context = new TestContext(_dbFile))
        {
            Named(context, name).FromCache(out string cacheKey, Options()).ToList();
            context.GetCache().Remove(cacheKey);
        }

        DeleteCustomer(id);

        using (var context = new TestContext(_dbFile))
            Assert.Empty(Named(context, name).FromCache(Options()).ToList());
    }

    [Fact]
    public async Task FromCacheAsync_ReturnsDatabaseResults()
    {
        var name = $"cust-{Guid.NewGuid():N}";
        AddCustomer(name);

        using var context = new TestContext(_dbFile);
        var result = await Named(context, name).FromCacheAsync(Options());

        Assert.Single(result);
    }
}
