using System;
using System.Collections.Generic;
using System.Text;
using System.Collections;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace Alachisoft.NCache.EntityFrameworkCore
{
    internal class NCacheEnumerable<T> : IEnumerable<T>
    {
        private IEnumerable<T> _innerEnumerable;
        private NCacheEnumerator<T> _enumeratorWrapper;

        public NCacheEnumerable(string key, IQueryable<T> query, IEnumerable<T> enumerable, CachingOptions options, bool throwError = false)
        {
            _innerEnumerable = enumerable;
            _enumeratorWrapper = new NCacheEnumerator<T>(key, query, _innerEnumerable, options, throwError);
        }

        public IEnumerator<T> GetEnumerator()
        {
            return _enumeratorWrapper;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _enumeratorWrapper;
        }
    }

    internal class NCacheEnumerator<T> : IEnumerator<T>
    {
        private IEnumerable<T> _innerEnumerable;
        private IEnumerator<T> _innerEnumerator;
        private string _queryKey;
        private Dictionary<string, T> _dataEnumerated;
        private CachingOptions _options; 
        private IQueryable<T> _query;
        private bool _isSeperateStorageEligible = false;

        private DbContext _currentContext;
        private bool _throwError = false;

        public NCacheEnumerator(string key, IQueryable<T> query, IEnumerable<T> enumerable, CachingOptions options, bool throwError = false)
        {
            _throwError = throwError;
            _queryKey = key;
            _query = query;
            _innerEnumerable = enumerable;
            _options = options; 
            _currentContext = query.GetDbContext();

            _innerEnumerator = _innerEnumerable.GetEnumerator();
            _dataEnumerated = new Dictionary<string, T>();

            if (_options.StoreAs == StoreAs.SeparateEntities)
            {

                _isSeperateStorageEligible = QueryHelper.IsSeperateStorageEligible(query, options);
            }
        }

        public T Current => _innerEnumerator.Current;

        object IEnumerator.Current => _innerEnumerator.Current;

        public void Dispose()
        {
            _innerEnumerator.Dispose();
        }

        public bool MoveNext()
        {
            bool result = _innerEnumerator.MoveNext();

            // If enumeration is complete
            if (result == false)
            {

                if (_options.StoreAs == StoreAs.SeparateEntities && _isSeperateStorageEligible)
                {
                    QueryCacheManager.Cache.Set<T>(_queryKey, _dataEnumerated, _options, StoreAs.SeparateEntities);
                }
                else
                {
                    QueryCacheManager.Cache.Set<T>(_queryKey, _dataEnumerated, _options, StoreAs.Collection);
                }
            }
            // If enumeration is being done
            else
            {
                // If StoreAs SeperateEntites
                if (_options.StoreAs == StoreAs.SeparateEntities && _isSeperateStorageEligible)
                {
                    // Note that this case will only occur when query will return single item even if the type is IEnumerable<T>
                    // and that the T is a solid entity this extracting the pkValues in the constructor and using them here
                    string entityKey = GetEntityCacheKey(Current);
                    _dataEnumerated.Add(entityKey, Current);
                }
                // If StoreAs Collection || Seperate eligibility fails store as collection
                else
                {
                    _dataEnumerated.Add(_dataEnumerated.Count.ToString(), Current);
                }
            }
            return result;
        }

        public void Reset()
        {
            _innerEnumerator.Reset();
        }

        public string GetEntityCacheKey(object entity)
        {
            NCacheWrapper nCacheW = QueryCacheManager.Cache as NCacheWrapper;
            if (nCacheW != null)
            {
                StringBuilder keyBuilder = new StringBuilder();
                keyBuilder.Append(nCacheW.DefaultKeyGen.GetKey(_currentContext, entity));
                return keyBuilder.ToString();
            }
            // Handle other cases if needed
            throw new Exception("Cache is not NCache.");
        }
    }
}
