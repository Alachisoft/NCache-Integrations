using Alachisoft.NCache.Runtime.Caching; 
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Generic;
using System.Text;

namespace Alachisoft.NCache.EntityFrameworkCore
{
    [Serializable]
    internal class CacheEntry<T>
    {
        private object _key;
        private T _value;

        private TimeSpan _slidingExpTime;
        private DateTime _absoluteExpTime;
        private ExpirationType _expirationType;
        private string _queryIdentifier;
        private StoreAs _storedAs = StoreAs.Collection;
        private Alachisoft.NCache.Runtime.CacheItemPriority _priority; 
        
        [NonSerialized]
        private Alachisoft.NCache.Client.ICache _cache;

        public object Key => _key;

        public T Value
        {
            get => _value;
            set => _value = value;
        }

        public DateTime AbsoluteExpirationTime
        {
            get => _absoluteExpTime;
            set => _absoluteExpTime = value;
        }

        public TimeSpan SlidingExpirationTime
        {
            get => _slidingExpTime;
            set => _slidingExpTime = value;
        }

        public ExpirationType ExpirationType
        {
            get => _expirationType;
            set => _expirationType = value;
        }

        public string QueryIDentifier
        {
            get => _queryIdentifier;
            set => _queryIdentifier = value;
        }

        public StoreAs StoredAs
        {
            get => _storedAs;
            set => _storedAs = value;
        }

        public Alachisoft.NCache.Runtime.CacheItemPriority Priority
        {
            get => _priority;
            set => _priority = value;
        } 

        public CacheEntry(object key, Alachisoft.NCache.Client.ICache cache)
        {
            _key = key;
            _cache = cache;
        }
    }
}
