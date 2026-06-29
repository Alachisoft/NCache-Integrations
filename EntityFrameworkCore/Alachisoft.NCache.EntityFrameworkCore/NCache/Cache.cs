using System;
using Microsoft.EntityFrameworkCore;
using Alachisoft.NCache.Runtime.Caching;
using Microsoft.EntityFrameworkCore.Metadata;
using Alachisoft.NCache.EntityFrameworkCore.NCache;

namespace Alachisoft.NCache.EntityFrameworkCore
{
    /// <summary>
    /// Class that provides methods to perfomr cache level operations.
    /// Each instance of this class is binded with a database context object.
    /// </summary>
    public class Cache
    {
        internal DbContext CurrentContext { get; set; }

        internal Cache() { }

        /*
        /// <summary>
        /// Returns the key generated for the specified entity.
        /// </summary>
        /// <param name="entity">The entity whos key is to be created.</param>
        /// <returns>The key of the entity specified.</returns>
        public string GetKey(object entity)
        {
            NCacheWrapper nCacheWrapper = QueryCacheManager.Cache;
            return nCacheWrapper.DefaultKeyGen.GetKey(CurrentContext, entity);
        }
        */

        /// <summary>
        /// Inserts instance of an entity in the cache. Any exisiting entity will be overwritten in the cache.
        /// Entity should be a part of the database context or else the method will throw an exception.
        /// </summary>
        /// <param name="entity">Instance of the entity to be inserted.</param>
        /// <param name="cacheKey">cache key that was used to insert the entity.</param>
        /// <param name="options">Caching options to be used while storing the entity. Note that some of the options
        /// might be overridden such as StoreAs option will always be <see cref="StoreAs.SeparateEntities"/>.</param>
        public void Insert(object entity, out string cacheKey, CachingOptions options)
        {
            Logger.Log(
                "Inserting entity '" + entity + "' with options " + options.ToLog() + "",
                Microsoft.Extensions.Logging.LogLevel.Trace
            );

            if (IsValidEntity(entity))
            {
                QueryCacheManager.Cache.EnsureCacheInitialized();

                // Generate key using the key generator from NCacheWrapper
                cacheKey = QueryCacheManager.Cache.DefaultKeyGen.GetKey(CurrentContext, entity);

                // Items are stored as separate entities in this API because only separate APIs can make it 
                // to this section of code. List or other similar data structures will fail on IsValidEntity
                // so only individual entities will make it to here.
                //
                 QueryCacheManager.Cache.Set(cacheKey, entity,options,StoreAs.SeparateEntities);

                return;
            }
            else
            {
                throw new Exception("Entity type and context do not match");
            }
        }

        /// <summary>
        /// Removes the entity from the cache. Doesnt throw any exception if the entity does not exist.
        /// However entity should be a part of the database context or else the method will throw an exception.
        /// </summary>
        /// <param name="entity">Entity that will be used to generate the Key for the cache item.</param>
        public void Remove(object entity)
        {
            Logger.Log("Removing entity '" + entity + "'", Microsoft.Extensions.Logging.LogLevel.Trace);

            if (IsValidEntity(entity))
            {
                NCacheWrapper nCacheWrapper = QueryCacheManager.Cache;
                nCacheWrapper.Remove(
                    nCacheWrapper.DefaultKeyGen.GetKey(CurrentContext, entity)
                );
            }
            else
            {
                throw new Exception("Entity type and context do not match");
            }
        }

        /// <summary>
        ///  Removes the item from the cache. Doesnt throw any exception if the item does not exist.
        /// </summary>
        /// <param name="cacheKey">The key that will be used to remove the item from the cache.</param>
        public void Remove(string cacheKey)
        {
            QueryCacheManager.Cache.Remove(cacheKey);
        } 

        private bool IsValidEntity(object entity)
        {
            IEntityType entityType = CurrentContext.Model.FindEntityType(entity.GetType());
            return entityType != null;
        }
    }
}