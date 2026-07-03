using Alachisoft.NCache.Client;
using Alachisoft.NCache.Common.Logger;
using Alachisoft.NCache.Common.Util;
using Alachisoft.NCache.Runtime.Caching;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace NCache.OSS.AspNetCore.OutputCaching;

internal class NCacheOutputCacheStore : IOutputCacheStore
{
    private readonly ICache _cache;
    private readonly NCacheOutputCacheOptions _options;
    private readonly ILogger _ncacheLogger;

    public NCacheOutputCacheStore(NCacheOutputCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.isValid(out var configError))
        {
            throw new InvalidOperationException(configError);
        }

        _options = options;
        _ncacheLogger = new NCacheLogger();

        try
        {
            if (_options.EnabledLogs)
            {
                _ncacheLogger.Initialize(LoggerNames.OutputCache, _options.CacheName);

                if (_options.EnableDetailLogs == true)
                    _ncacheLogger.SetLevel("all");
                else
                    _ncacheLogger.SetLevel("info");

                _ncacheLogger.Debug("Detailed logging enabled");

                _ncacheLogger.Info("Initializing NCacheOutputCacheProvider");
            }

            _cache = CacheManager.GetCache(_options.CacheName, _options.getCacheConnectionOptions());

            if (_options.EnabledLogs)
                _ncacheLogger.Info("NCacheOutputCacheProvider initialized");
        }
        catch (Exception ex)
        {
            if (_options.EnabledLogs)
                _ncacheLogger.Error($"Error: {ex}");
            throw;
        }
    }

    public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
    {
        throw new NotImplementedException(Constants.TagUnsupportedExceptionMessage);
    }

    public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));

        if (_options.EnabledLogs)
            _ncacheLogger.Debug($"GetAsync called. Key: {key}");

        return new ValueTask<byte[]?>(Task<byte[]?>.Run(() =>
        {
            try
            {
                var result = _cache.Get<byte[]?>(key);

                if (_options.EnabledLogs)
                {
                    if (result == null)
                        _ncacheLogger.Debug($"Cache MISS for key: {key}");
                    else
                        _ncacheLogger.Debug($"Cache HIT for key: {key}");
                }
                
                return result;
            }
            catch (Exception ex)
            {
                if (_options.EnabledLogs)
                    _ncacheLogger.Error($"Error: {ex}");
                throw;
            }
        }, cancellationToken));
    }

    public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));
        if (value == null) throw new ArgumentNullException(nameof(value));

        if (_options.EnabledLogs)
            _ncacheLogger.Debug($"SetAsync called. Key: {key}, Expiry: {validFor}");

        return new ValueTask(Task.Run(() =>
        {
            try
            {
                CacheItem cItem = new CacheItem(value);
                Expiration expiration = new Expiration(ExpirationType.Absolute, validFor);
                cItem.Expiration = expiration;

                _cache.Insert(key, cItem);
            }
            catch (Exception ex)
            {
                if (_options.EnabledLogs)
                    _ncacheLogger.Error($"Error: {ex}");
                throw;
            }
        }, cancellationToken));
    }
}
