using Alachisoft.NCache.Client;
using Alachisoft.NCache.Common.FeatureUsageData;
using Alachisoft.NCache.EntityFrameworkCore.NCache;
using Alachisoft.NCache.Runtime.Caching;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Alachisoft.NCache.EntityFrameworkCore
{
    internal class NCacheWrapper /*: Cache*///, IMemoryCache
    {
        Alachisoft.NCache.Client.ICache _nCache;
        private DefaultKeyGenerator _defaultKeyGen;
        internal Alachisoft.NCache.Client.ICache NCacheInstance => _nCache;
        private DateTime? _retryCacheInitTime;
        bool _cacheInitialized = false;
        private bool _cacheUnavailable;

        public DefaultKeyGenerator DefaultKeyGen => _defaultKeyGen;

        public NCacheWrapper(MemoryCacheOptions options)
        {
            if (!NCacheConfiguration.IsConfigured)
                throw new Exception("NCache initialization configuration is not provided. Please use Alachisoft.NCache.EntityFrameworkCore.NCacheConfiguration.Configure() method to configure NCache before using it.");

            _defaultKeyGen = new DefaultKeyGenerator();
        }

        public bool IsCacheInitialized
        {
            get
            {
                try { TryCacheInitialization(); }
                catch { }
                return _cacheInitialized;
            }
        }

        private void InitializeCache()
        {
            try
            {
                CacheConnectionOptions cacheConnectionOptions = new CacheConnectionOptions();
                string AppName = FeatureUsageCollector.FeatureTag + FeatureEnum.efcore;

                _retryCacheInitTime = DateTime.Now;
                if (NCacheConfiguration.InitParams != null)
                {
                    NCacheConfiguration.InitParams.AppName += AppName;
                    cacheConnectionOptions = NCacheConfiguration.InitParams;
                    _nCache = CacheManager.GetCache(NCacheConfiguration.CacheId, cacheConnectionOptions);
                }

                else
                    _nCache = CacheManager.GetCache(NCacheConfiguration.CacheId);
                _cacheInitialized = true;
            }
            catch (Exception ex)
            {
                _cacheInitialized = false;
                throw;
            }
        }
        private bool RetryCacheInitInterval()
        {
            if (_retryCacheInitTime != null && DateTime.Now.Subtract(_retryCacheInitTime.Value).TotalSeconds < 60)
                return false;

            return true;
        }

        internal void EnsureCacheInitialized()
        {
            if (!_cacheInitialized)
                InitializeCache();
        }

        private void TryCacheInitialization()
        {
            if (!_cacheInitialized && RetryCacheInitInterval())
            {
                InitializeCache();
            }
            else if (_cacheUnavailable)
            {
                if (RetryCacheInitInterval())
                    _cacheUnavailable = false;
            }
        }

        internal CacheEntry<T> CreateEntry<T>(object key)
        {
            Logger.Log(
                "Creating cache entry against key '" + key + "'.",
                Microsoft.Extensions.Logging.LogLevel.Trace
            );
            var cacheEntry = new CacheEntry<T>(key, _nCache);
            return cacheEntry;
        }

        public void Insert(object key, CacheItem value, bool throwError = false)
        {
            Logger.Log(
           "Inserting item '" + value + "' against key '" + key + "'.",
           Microsoft.Extensions.Logging.LogLevel.Trace
       );
            TryCacheInitialization();
            if (_cacheInitialized && !_cacheUnavailable)
            {
                try
                {
                    _nCache.Insert(key.ToString(), value);
                }
                catch (Exception e)
                {
                    Logger.Log(
                     "Failed to Insert item '" + value + "' against key '" + key + "'. " + e.Message,
                     Microsoft.Extensions.Logging.LogLevel.Trace);
                    if (e.Message.Contains("No server is available to process the request"))
                    {
                        _retryCacheInitTime = DateTime.Now;
                        _cacheUnavailable = true;

                        if (throwError)
                            throw;
                    }

                }
            }


        }



        internal void InsertBulk(object[] keys, CacheItem[] values, bool throwError = false)
        {
            Logger.Log(
                    "Inserting items in bulk against respective keys.",
                    Microsoft.Extensions.Logging.LogLevel.Trace
                );
            TryCacheInitialization();
            if (_cacheInitialized && !_cacheUnavailable)
            {
                try
                {
                    Dictionary<string, CacheItem> items = new Dictionary<string, CacheItem>(keys.Length);
                    if (keys.Length > 0)
                    {
                        string[] strKeys = new string[keys.Length];
                        for (int i = 0; i < keys.Length; i++)
                        {
                            strKeys[i] = keys[i].ToString();
                            items.Add(strKeys[i], values[i]);
                        }

                        IDictionary issues = (Dictionary<string, Exception>)_nCache.InsertBulk(items);
                        if (issues.Count > 0)
                        {
                            _nCache.RemoveBulk(strKeys);

                            IDictionaryEnumerator enumerator = issues.GetEnumerator();
                            enumerator.MoveNext();
                            throw (Exception)enumerator.Entry.Value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log(
                    "Failed to Insert items in bulk against respective keys. " + ex.Message,
                    Microsoft.Extensions.Logging.LogLevel.Trace
                );
                    if (ex.Message.Contains("No server is available to process the request"))
                    {
                        _retryCacheInitTime = DateTime.Now;
                        _cacheUnavailable = true;

                        if (throwError)
                            throw;
                    }
                }

            }


        }

        public new void Remove(object key, bool throwError = false)
        {
            Logger.Log(
                    "Removing item against key '" + key + "'",
                    Microsoft.Extensions.Logging.LogLevel.Trace
                );
            TryCacheInitialization();
            if (_cacheInitialized && !_cacheUnavailable)
            {
                try
                {
                    _nCache.Remove(key.ToString());
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("No server is available to process the request"))
                    {
                        _retryCacheInitTime = DateTime.Now;
                        _cacheUnavailable = true;

                        if (throwError)
                            throw;
                    }
                    if (NCacheConfiguration.IsErrorEnabled)
                    {
                        Logger.Log(
                    "Removing item against key '" + key + "'" + " failed. " + ex.Message,
                     Microsoft.Extensions.Logging.LogLevel.Trace);
                        throw ex;
                    }

                }
            }


        }

        public bool TryGetValue<T>(object key, out T value, bool throwError = false)
        {
            Logger.Log(
                     "Trying to get item against key '" + key + "'",
                     Microsoft.Extensions.Logging.LogLevel.Trace
                 );
            TryCacheInitialization();
            if (_cacheInitialized && !_cacheUnavailable)
            {
                try
                {
                    value = _nCache.Get<T>(key.ToString());
                    return value != null;
                }
                catch (Exception ex)
                {
                    Logger.Log(
                     "Trying to get item against key '" + key + "'" + "failed. " + ex.Message,
                     Microsoft.Extensions.Logging.LogLevel.Trace
                    );
                    if (ex.Message.Contains("No server is available to process the request"))
                    {
                        _retryCacheInitTime = DateTime.Now;
                        _cacheUnavailable = true;

                        Logger.Log(
                        "Trying to get item against key '" + key + "' failed" + ex.Message,
                        Microsoft.Extensions.Logging.LogLevel.Error);

                        if (throwError)
                            throw;
                    }

                }

            }
            else if (throwError)
            {
                Logger.Log(
                    "Trying to get item against key '" + key + "'" + "failed. " + "No server is available to process the request",
                    Microsoft.Extensions.Logging.LogLevel.Trace);
                throw new Exception("No server is available to process the request");
            }

            value = default(T);
            return false;

        }

        // storeAs
        // Seperate cases Collection
        public bool GetByKey<T>(string key, out IDictionary value, StoreAs storeAs, bool throwError = false)
        {
            Logger.Log("Trying to get item against Key '" + key + "'",
                                        Microsoft.Extensions.Logging.LogLevel.Trace);

            TryCacheInitialization();
            IDictionary resultSet = new Hashtable();
            if (_cacheInitialized && !_cacheUnavailable)
            {
                try
                {
                    // The query key holds a different shape depending on how the result was stored
                    // (see NCacheWrapperExtensions.Set), and StoreAs is not part of the key, so the
                    // same query run with the other StoreAs reads what the earlier run left. Read it
                    // untyped and branch on what is actually there:
                    //   Collection       -> CacheEntry<IList<T>> holding the whole result
                    //   SeparateEntities -> the keys of the entities, each entity stored under its own key
                    //   single result    -> CacheEntry<T> (SetAsCacheEntry)
                    //
                    // Only a shape that matches the requested StoreAs is a hit. A mismatch means the key was
                    // written by a different query (two queries sharing a QueryIdentifier) or by the same
                    // query in the other mode; returning it would hand back another query's rows, so it is a
                    // miss and the query goes to the database.
                    object firstResult = _nCache.Get<object>(key);

                    if (firstResult is CacheEntry<IList<T>> collection)
                    {
                        if (storeAs == StoreAs.Collection)
                            resultSet.Add(key, collection);
                    }
                    else if (firstResult is CacheEntry<T> entry)
                    {
                        resultSet.Add(key, entry.Value);
                    }
                    else if (firstResult != null && storeAs == StoreAs.SeparateEntities)
                    {
                        string[] entityKeys = firstResult as string[] ?? _nCache.Get<string[]>(key);
                        IDictionary<string, T> entities = _nCache.GetBulk<T>(entityKeys);

                        // An entity that expired or was evicted makes the stored result incomplete;
                        // returning the rest would silently drop rows, so treat it as a miss.
                        if (entities.Count == entityKeys.Length)
                        {
                            var ordered = new System.Collections.Specialized.OrderedDictionary(entityKeys.Length);
                            foreach (string entityKey in entityKeys)
                                ordered.Add(entityKey, entities[entityKey]);
                            resultSet = ordered;
                        }
                    }

                    value = resultSet;
                    return resultSet.Count > 0;
                }
                catch (Exception ex)
                {
                    Logger.Log("Trying to get item against Key '" + key + "' failed." + ex.Message, Microsoft.Extensions.Logging.LogLevel.Trace);
                    if (ex.Message.Contains("No server is available to process the request"))
                    {
                        _retryCacheInitTime = DateTime.Now;
                        _cacheUnavailable = true;
                        Logger.Log("Trying to get item against Key '" + key + "' failed" + ex.Message, Microsoft.Extensions.Logging.LogLevel.Trace);

                        if (throwError)
                            throw;
                    }

                }
            }
            else if (throwError)
            {
                Logger.Log("Trying to get item against Key '" + key + "' failed" + "No server is avialble to process the request", Microsoft.Extensions.Logging.LogLevel.Trace);
                throw new Exception("No server is avialble to process the request");
            }
            value = null;
            return false;
        }

        public void Dispose()
        {
            _nCache = null;
        }

       
    }
}
