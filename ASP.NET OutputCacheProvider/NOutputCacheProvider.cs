using System;
using System.Collections.Specialized;
using System.Configuration; 
using System.Web; 
using Alachisoft.NCache.Common.Logger; 
using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Exceptions;
using Alachisoft.NCache.Runtime.Caching;
using Alachisoft.NCache.Common.FeatureUsageData;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Alachisoft.NCache.OutputCacheProvider
    {
    public class NOutputCacheProvider : System.Web.Caching.OutputCacheProvider
    { 
         private Alachisoft.NCache.Client.ICache _cache; 

        private string _cacheId;
        private bool _detailedLogs = false;
        private bool _exceptionsEnabled, _isCacheInitialized = false;
        private bool _logs, _writeExceptionsToEventLog = false;
        private static ILogger _ncacheLog; 


        static NOutputCacheProvider()
        {
            _ncacheLog = null; 
        } 

        private static ILogger NCacheLog
        {
            get
            {
                return _ncacheLog;
            }
        }   

        public override void Initialize(string name, NameValueCollection config)
        {
            string attribute = string.Empty;
            bool flag = false;
            _exceptionsEnabled = true;
            _isCacheInitialized = false;

            try
            {
                if (config == null)
                    throw new ArgumentNullException("Configuration settings are missing");

                if (string.IsNullOrEmpty(config["cacheName"]))
                    throw new ConfigurationErrorsException("The 'cacheName' attribute cannot be null or empty string"); 
                if (string.IsNullOrEmpty(config["description"]))
                    config["description"] = "NCache Output Cache Provider"; 


                base.Initialize(name, config);

                string[] configSettings = new string[] { "exceptionsEnabled", "writeExceptionsToEventLog", "enableLogs", "enableDetailLogs" };

                for (int i = 0; i < configSettings.Length; i++)
                {
                    attribute = config[configSettings[i]];
                    if (attribute != null)
                    {
                        if ((!(attribute.ToLower().Equals("true"))) && (!(attribute.ToLower().Equals("false"))))
                        {
                            throw new ConfigurationErrorsException("The '" + configSettings[i] + "' attribute must be one of the following values: true, false.");
                        }
                        flag = Convert.ToBoolean(attribute);
                        switch (i)
                        {
                            case 0:
                                _exceptionsEnabled = flag;
                                break;

                            case 1:
                                _writeExceptionsToEventLog = flag;
                                break;

                            case 2:
                                _logs = flag;
                                break;

                            case 3:
                                _detailedLogs = flag;
                                _logs = flag;
                                break;
                        }
                    }
                }
                _cacheId = config["cacheName"];
                InitializeCache(_cacheId);  
            }
            catch (Exception exception)
            {
                this.RaiseException(exception);
            }
        }

        private void InitializeCache(string cacheId)
        {
            try
            {
                if (_cache == null)
                {
                    if ((_logs || _detailedLogs) && (_ncacheLog == null))
                    {
                        _ncacheLog = new NCacheLogger();
                        _ncacheLog.Initialize(LoggerNames.OutputCache, _cacheId);
                            
                        if (_detailedLogs)
                            NCacheLog.SetLevel("all");
                        else if (_logs)
                            NCacheLog.SetLevel("info");
                    } 
					CacheConnectionOptions cacheConnectionOptions = new CacheConnectionOptions();
                    cacheConnectionOptions.AppName = FeatureUsageCollector.FeatureTag + FeatureEnum.outputcache_provider;
                    _cache = NCache.Client.CacheManager.GetCache(cacheId,cacheConnectionOptions); 

                    _isCacheInitialized = true;

                    if (_logs)
                        NCacheLog.Info("NOuputCacheProvider initialized");
                }
            }
            catch (Exception exception)
            {
                _cache = null;
                RaiseException(exception);
            }
        }

        public override object Get(string key)
        {
            object value = null;
            try
            {
                if (!_isCacheInitialized)
                {
                    InitializeCache(_cacheId);
                }
                value = _cache.Get<object>(key);

                value = DeserializeIfSerialized(value);
            }
            catch (Exception exception)
            {
                RaiseException(exception);
            }
            return value;
        }

        public override object Add(string key, object entry, DateTime utcExpiry)
        {
            object value = null;
            try
            {
                if (!_isCacheInitialized)
                {
                    InitializeCache(_cacheId);
                }
                value = _cache.Get<object>(key);
                if (value == null)
                {
                    CacheItem item;
                    item = CreateCacheItem(key, entry, utcExpiry);
                    _cache.Add(key, item);
                    return entry;
                }
                else
                {
                    value = DeserializeIfSerialized(value);
                }
            }
            catch (OperationFailedException op)
            {
                string message = op.Message;
                if (message != null && !(message.ToLower().Contains("key already exists")))
                {
                    value = Get(key);
                }

            }
            catch (Exception exception)
            {
                this.RaiseException(exception);
            }
            return value;
        }

        public override void Set(string key, object entry, DateTime utcExpiry)
        {

            try
            {              
                if (!_isCacheInitialized)
                {
                    InitializeCache(_cacheId);
                } 
                CacheItem item;
                item = CreateCacheItem(key, entry, utcExpiry);                
                _cache.Insert(key, item);
            }
            catch (Exception exception)
            {
                RaiseException(exception);
            }
        }

        internal CacheItem CreateCacheItem(string key, object entry, DateTime utcExpiry)
        {
            object cacheValue = entry;

            if (entry != null && !InternalClassSerializer.IsSimpleType(entry.GetType()))
            {
                var jsonObject = InternalClassSerializer.SerializeFields(entry);
                if (jsonObject != null)
                {
                    cacheValue = jsonObject.ToString(Formatting.None);
                }
            }
            CacheItem item = new CacheItem(cacheValue);
            HttpContext context = HttpContext.Current;
            item.Priority = Alachisoft.NCache.Runtime.CacheItemPriority.Default;
            if (utcExpiry.Equals(DateTime.MaxValue))
                item.Expiration = new Expiration(ExpirationType.None);
            else
            {
                utcExpiry = utcExpiry.ToUniversalTime();
                item.Expiration = new Expiration(ExpirationType.Absolute, utcExpiry - DateTime.UtcNow);
            }

            return item;
        }

        public override void Remove(string key)
        {
            try
            {
                if (!_isCacheInitialized)
                {
                    InitializeCache(_cacheId);
                }

                _cache.Remove(key);
            }
            catch (Exception exception)
            {
                RaiseException(exception);
            }
        }

        private void RaiseException(Exception exc)
        {
            if (NCacheLog != null)
            {
                NCacheLog.Error(exc.ToString());
            }
            if (_exceptionsEnabled)
            {
                throw exc;
            }
        }

        ~NOutputCacheProvider()
        {
            if (_cache != null)
            {
                _cache.Dispose();
                _cache = null;
            }  
        }

        private object DeserializeIfSerialized(object cachedValue)
        {
            if (cachedValue is string jsonString && jsonString.StartsWith("{\"Type\":"))
            {
                try
                {
                    var serialized = JObject.Parse(jsonString);
                    return InternalClassSerializer.Deserialize(serialized);
                }
                catch (Exception ex)
                {
                    if (NCacheLog != null)
                    {
                        NCacheLog.Error("Failed to deserialize output cache entry: " + ex.ToString());
                    }
                    return cachedValue;
                }
            }
            return cachedValue;
        }
    }
}

