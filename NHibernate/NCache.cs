// ===============================================================================
// Alachisoft (R) NCache Integrations
// NCache Provider for NHibernate
// ===============================================================================
// Copyright © Alachisoft.  All rights reserved.
// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY
// OF ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT
// LIMITED TO THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
// FITNESS FOR A PARTICULAR PURPOSE.
// ===============================================================================

using Alachisoft.NCache.Client.Extension;
using Alachisoft.NCache.Integrations.NHibernate.Cache.Configuration;
using Alachisoft.NCache.Runtime.Caching;
using Newtonsoft.Json.Linq;
using NHibernate;
using NHibernate.Cache;
using NHibernate.Mapping;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Alachisoft.NCache.Integrations.NHibernate.Cache
{
    class NCache : CacheBase
    {
        private static readonly INHibernateLogger _logger = NHibernateLogger.For(typeof(Alachisoft.NCache.Integrations.NHibernate.Cache.NCacheProvider));
        private static Dictionary<string, CacheHandler> _caches = new Dictionary<string, CacheHandler>();
        private CacheHandler _cacheHandler = null;
        private readonly RegionConfiguration _regionConfig = null;
        private string _regionName = null;

        /// <summary>
        /// Initializes new cache region.
        /// </summary>
        /// <param name="regionName">Name of region.</param>
        /// <param name="properties"></param>
        public NCache(string regionName, IDictionary<string, string> properties)
        {
            try
            {
                if (_logger.IsDebugEnabled())
                {
                    _logger.Debug(String.Format("Initializing NCache with region : {0}", regionName));
                }

                _regionName = regionName;
                _regionConfig = ConfigurationManager.Instance.GetRegionConfiguration(regionName);

                lock (_caches)
                {
                    if (_caches.ContainsKey(_regionConfig.CacheName))
                    {
                        _cacheHandler = _caches[_regionConfig.CacheName];
                        _cacheHandler.IncrementRefCount();
                    }
                    else
                    {
                        _cacheHandler = new CacheHandler(_regionConfig.CacheName, ConfigurationManager.Instance.ExceptionEnabled);
                        _caches.Add(_regionConfig.CacheName, _cacheHandler);
                    }
                }
            }
            catch (Exception e)
            {
                if (_logger.IsErrorEnabled())
                {
                    _logger.Error("Failed to initialize NCache. " + e.Message);
                }
                throw new CacheException("Failed to initialize NCache. " + e.Message, e);
            }
        }


        #region ICache Members
        /// <summary>
        /// Clear cache.
        /// </summary>
        public override void Clear()
        {
            try
            {
                if (_logger.IsDebugEnabled())
                {
                    _logger.Debug(String.Format("Clearing Region Cache : {0}", _regionName));
                }

                _cacheHandler.Cache.Clear();

            }
            catch (Exception e)
            {
                if (_logger.IsErrorEnabled())
                {
                    _logger.Error("Clear operaion failed." + e.Message);
                }
                throw new CacheException("Clear operaion failed." + e.Message, e);
            }
        }

        /// <summary>
        /// Disposes cache.
        /// </summary>
        public override void Destroy()
        {
            try
            {
                lock (_caches)
                {
                    if (_cacheHandler != null)
                    {
                        if (_logger.IsDebugEnabled())
                        {
                            _logger.Debug(String.Format("Destroying Region Cache : {0}", _regionName));
                        }
                        if (_cacheHandler.DecrementRefCount() == 0)
                        {
                            _caches.Remove(_regionConfig.CacheName);
                            _cacheHandler.DisposeCache();
                        }
                        _cacheHandler = null;
                    }
                }
            }
            catch (Exception e)
            {
                if (_logger.IsErrorEnabled())
                {
                    _logger.Error("Destroy operation failed." + e.Message);
                }
                throw new CacheException("Destroy operation failed." + e.Message, e);
            }
        }

        /// <summary>
        /// Get object from cache
        /// </summary>
        /// <param name="key">key of the object.</param>
        /// <returns></returns>
        public override object Get(object key)
        {
            try
            {
                if (key == null)
                    return null;

                string cacheKey = ConfigurationManager.Instance.GetCacheKey(key);
                if (_logger.IsDebugEnabled())
                {
                    _logger.Debug(String.Format("Fetching object from the cache with key = {0}", cacheKey));
                }

                var objectFromCache = _cacheHandler.Cache.Get<object>(cacheKey);
                
                if (objectFromCache != null && objectFromCache is JArray)
                {
                    objectFromCache = ((JArray)objectFromCache).ToObject<List<object>>();
                }

                return objectFromCache;
            }
            catch (Exception e)
            {
                if (_logger.IsErrorEnabled())
                {
                    _logger.Error("Get operation failed. " + e.Message);
                }
                throw new CacheException("Get operation failed. " + e.Message, e);
            }
        }

        /// <summary>
        /// Lock the item with the key provided
        /// </summary>
        /// <param name="key">The Key of the Item in the Cache to lock.</param>
        /// <exception cref="CacheException"></exception>
        public override object Lock(object key)
        {
            if (key == null)
                throw new ArgumentNullException("key", "null key not allowed");

            string cacheKey = ConfigurationManager.Instance.GetCacheKey(key);
            LockToken lockToken = new LockToken();

            try
            {
                if (!LockingExtension.LockKey(_cacheHandler.Cache, cacheKey, out lockToken))
                {
                    throw new CacheException("Unable to acquire lock on the key provided.");
                }
            }
            catch (Exception e)
            {
                throw new CacheException(e.Message);
            }


            return lockToken;

        }

        public override long NextTimestamp()
        {
            return Timestamper.Next();
        }

        /// <summary>
        /// Insert an object in cahce with key specified.
        /// </summary>
        /// <param name="key">key of the object.</param>
        /// <param name="value">Object to be inserted in cache.</param>
        public override void Put(object key, object value)
        {
            try
            {
                if (key == null)
                    throw new ArgumentNullException("key", "null key not allowed");

                if (value == null)
                    throw new ArgumentNullException("value", "null value not allowed");

                string cacheKey = ConfigurationManager.Instance.GetCacheKey(key);

                Alachisoft.NCache.Client.CacheItem item = new Alachisoft.NCache.Client.CacheItem(value);
                item.Priority = _regionConfig.CacheItemPriority;

                if (_regionConfig.ExpirationType.ToLower() == "sliding")
                    item.Expiration = new Expiration(ExpirationType.Sliding, new TimeSpan(0, 0, _regionConfig.ExpirationPeriod));
                else if (_regionConfig.ExpirationType.ToLower() == "absolute")
                    item.Expiration = new Expiration(ExpirationType.Absolute, new TimeSpan(0, 0, _regionConfig.ExpirationPeriod));


                if (_logger.IsDebugEnabled())
                {
                    _logger.Debug(String.Format("Inserting: key={0}&value={1}", key, value.ToString()));
                }
                _cacheHandler.Cache.Insert(cacheKey, item);
            }
            catch (Exception e)
            {
                if (_logger.IsErrorEnabled())
                {
                    _logger.Error("Put operation failed. " + e.Message);
                }
                throw new CacheException("Put operation failed. " + e.Message, e);
            }
        }

        /// <summary>
        /// RegionName associated with current cache.
        /// </summary>
        public override string RegionName
        {
            get { return _regionName; }
        }

        /// <summary>
        /// Remove an object from cache.
        /// </summary>
        /// <param name="key">Key of the object.</param>
        public override void Remove(object key)
        {
            try
            {
                string cacheKey = ConfigurationManager.Instance.GetCacheKey(key);
                //              if (_regionConfig.UseAsync)
                //             {

                if (_logger.IsDebugEnabled())
                {
                    _logger.Debug("Removing item with key: " + cacheKey);
                }
                _cacheHandler.Cache.Remove(cacheKey);
                //             }
            }
            catch (Exception e)
            {
                if (_logger.IsErrorEnabled())
                {
                    _logger.Error("Remove operation failed. " + e.Message);
                }
                throw new CacheException("Remove operation failed. " + e.Message, e);
            }
        }

        public override int Timeout
        {
            get { return Timestamper.OneMs * 60000; }
        }

        /// <summary>
        /// Unlock the item with the key provided
        /// </summary>
        /// <param name="key">The Key of the Item in the Cache to lock.</param>
        /// <exception cref="CacheException"></exception>
        public override void Unlock(object key, object lockValue)
        {
            if (key == null)
                throw new ArgumentNullException("key", "null key not allowed");

            string cacheKey = ConfigurationManager.Instance.GetCacheKey(key);

            try
            {
                LockingExtension.UnlockKey(_cacheHandler.Cache, cacheKey, null);
            }
            catch (Exception e)
            {
                throw new CacheException(e.Message);
            }
        }

        public override Task<object> GetAsync(object key, CancellationToken cancellationToken)
        {
            if (key == null)
                throw new ArgumentNullException("key", "null key not allowed");

            TaskFactory<object> factory = new TaskFactory<object>(cancellationToken);

            Task<object> task = Task.Run(() => {
                return Get(key);
            }, cancellationToken);

            return task;
        }

        public override Task PutAsync(object key, object value, CancellationToken cancellationToken)
        {
            if (key == null)
                throw new ArgumentNullException("key", "null key not allowed");

            if (value == null)
                throw new ArgumentNullException("value", "null value not allowed");

            Task task = Task.Run(() => {
                Put(key, value);
            }, cancellationToken);

            return task;
        }

        public override Task RemoveAsync(object key, CancellationToken cancellationToken)
        {
            if (key == null)
                throw new ArgumentNullException("key", "null key not allowed");

            Task task = Task.Run(() => {
                Remove(key);
            });

            return task;
        }

        public override Task ClearAsync(CancellationToken cancellationToken)
        {
            Task task = Task.Run(() => {
                Clear();
            });

            return task;
        }

        public override Task<object> LockAsync(object key, CancellationToken cancellationToken)
        {
            if (key == null)
                throw new ArgumentNullException("key", "null key not allowed");

            return Task.Run(() => {
                return Lock(key);
            });

        }

        public Task UnlockAsync(object key, CancellationToken cancellationToken)
        {
            if (key == null)
                throw new ArgumentNullException("key", "null key not allowed");

            Task task = Task.Run(() => {
                Unlock(key, null);
            });

            return task;
        }

        /// <summary>
        /// Fetches objects from cache.
        /// </summary>
        /// <param name="keys">Array of keys to fetch objects from cache.</param>
        /// <returns>Array of objects fetched from cache.</returns>
        public override object[] GetMany(object[] keys)
        {
            try
            {
                if (keys == null)
                    throw new ArgumentNullException(nameof(keys));
                string[] cacheKeys = new string[keys.Length];
                for (int i = 0; i < keys.Length; i++)
                {
                    cacheKeys[i] = ConfigurationManager.Instance.GetCacheKey(keys[i]);
                }
                var cacheItems = _cacheHandler.Cache.GetBulk<object>(cacheKeys);
                object[] results = new object[cacheKeys.Length];
                for (int i = 0; i < cacheKeys.Length; i++)
                {
                    if (_logger.IsDebugEnabled())
                    {
                        _logger.Debug(String.Format("Fetching object from the cache with key = {0}", cacheKeys[i]));
                    }
                    if (cacheItems.TryGetValue(cacheKeys[i], out object item))
                    {
                        if (item != null && item is JArray)
                        {
                            item = ((JArray)item).ToObject<List<object>>();
                        }

                        results[i] = item;
                    }
                    else
                    {
                        results[i] = null;
                    }
                }
                return results;
            }
            catch (Exception e)
            {
                if (_logger.IsErrorEnabled())
                {
                    _logger.Error("GetMany operation failed. " + e.Message);
                }
                throw new CacheException("GetMany operation failed. " + e.Message, e);
            }
        }

        /// <summary>
        /// Inserts multiple objects into cache.
        /// </summary>
        /// <param name="keys">Array of keys.</param>
        /// <param name="values">Array of objects to insert into cache.</param>
        public override void PutMany(object[] keys, object[] values)
        {
            try
            {
                if (keys == null)
                    throw new ArgumentNullException(nameof(keys));
                if (values == null)
                    throw new ArgumentNullException(nameof(values));
                if (keys.Length != values.Length)
                    throw new ArgumentException(
                        $"{nameof(keys)} and {nameof(values)} must have the same length. Found {keys.Length} and " +
                        $"{values.Length} respectively");

                IDictionary<string, Alachisoft.NCache.Client.CacheItem> items_dict = new Dictionary<string, Alachisoft.NCache.Client.CacheItem>();

                for (int i = 0; i < keys.Length; i++)
                {
                    if (keys[i] == null)
                        throw new ArgumentNullException("key", "null key not allowed");

                    if (values[i] == null)
                        throw new ArgumentNullException("value", "null value not allowed");

                    string cacheKey = ConfigurationManager.Instance.GetCacheKey(keys[i]);

                    Alachisoft.NCache.Client.CacheItem item = GetCacheItem(cacheKey, values[i]);

                    if (_logger.IsDebugEnabled())
                    {
                        _logger.Debug(String.Format("Inserting: key={0}&value={1}", cacheKey, values[i].ToString()));
                    }
                    //_cacheHandler.Cache.Insert(cacheKey, item);
                    items_dict.Add(cacheKey, item);
                }

                _cacheHandler.Cache.InsertBulk(items_dict);

            }
            catch (Exception e)
            {
                if (_logger.IsErrorEnabled())
                {
                    _logger.Error("PutMany operation failed. " + e.Message);
                }
                throw new CacheException("PutMany operation failed. " + e.Message, e);
            }
        }

        /// <summary>
        /// Method to fetch multiple objects from cache asynchronously.
        /// </summary>
        /// <param name="keys"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>Instance of task.</returns>
        public override Task<object[]> GetManyAsync(object[] keys, CancellationToken cancellationToken)
        {
            if (keys == null)
                throw new ArgumentNullException(nameof(keys), "keys cannot be null");
            Task<object[]> task = Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return GetMany(keys);
            }, cancellationToken);
            return task;
        }

        /// <summary>
        /// Method to insert multiple objects into cache asynchronously.
        /// </summary>
        /// <param name="keys"></param>
        /// <param name="values"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>Instance of task.</returns>

        public override Task PutManyAsync(object[] keys, object[] values, CancellationToken cancellationToken)
        {
            if (keys == null)
                throw new ArgumentNullException(nameof(keys), "keys cannot be null");
            if (values == null)
                throw new ArgumentNullException(nameof(values), "values cannot be null");
            Task task = Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                PutMany(keys, values);
            }, cancellationToken);
            return task;
        }

        #endregion

        /// <summary>
        /// Method to wrap object into cacheitem, set priority, tag, dependency and expiration.
        /// </summary>
        /// <param name="key">Key of object.</param>
        /// <param name="obj">Object itself.</param>
        /// <returns>Instance of cacheitem.</returns>
        private Alachisoft.NCache.Client.CacheItem GetCacheItem(string key, object obj)
        {
            Alachisoft.NCache.Client.CacheItem cacheItem = new Alachisoft.NCache.Client.CacheItem(obj);
            cacheItem.Priority = _regionConfig.CacheItemPriority;

            if (_regionConfig.ExpirationType.ToLower() == "sliding")
                cacheItem.Expiration = new Expiration(ExpirationType.Sliding, new TimeSpan(0, 0, _regionConfig.ExpirationPeriod));
            else if (_regionConfig.ExpirationType.ToLower() == "absolute")
                cacheItem.Expiration = new Expiration(ExpirationType.Absolute, new TimeSpan(0, 0, _regionConfig.ExpirationPeriod));

            return cacheItem;
        }
    }
}
