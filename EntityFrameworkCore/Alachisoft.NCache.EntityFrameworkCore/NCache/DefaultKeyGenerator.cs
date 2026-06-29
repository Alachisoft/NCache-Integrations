using System;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Text;

namespace Alachisoft.NCache.EntityFrameworkCore
{
    /// <summary>
    /// Abstract class that defines the method required to generate keys.
    /// </summary>
    public abstract class KeyGenerator
    {
        /// <summary>
        /// Generates the cache key for the entity specified.
        /// </summary>
        /// <param name="context">The context which will be used to generate the key.</param>
        /// <param name="entity">The entity whos key is to be generated.</param>
        /// <returns>Returns the cache key.</returns>
        public abstract string GetKey(DbContext context, object entity);
    }

    /// <summary>
    /// Default implementation provided for the cache key generation.
    /// </summary>
    public class DefaultKeyGenerator : KeyGenerator
    {
        private static Dictionary<Type, KeyGenerator> _registeredGenerators;
        private static readonly object _lockObject;
        private const string seperator = ":";

        static DefaultKeyGenerator()
        {
            _lockObject = new object();
            _registeredGenerators = new Dictionary<Type, KeyGenerator>();
        }
        #region GenerateKeyFromQuery
        public static KeyInfo ParseKeyInfo(string key)
        {
            return new KeyInfo(GetFqnFromCachekey(key), GetClassNameFromCachekey(key), GetPrimaryKeysFromCacheKey(key));
        }
        public static string GenerateQuery(KeyInfo _keyinfo)
        {
            string space = " ";

            StringBuilder query = new StringBuilder("select * from" + space);
            query.Append(_keyinfo.ClassName + space);
            query.Append("where" + space);
            foreach (DictionaryEntry property in _keyinfo.PKPairs)
            {
                query.Append(property.Key);
                query.Append("=");
                query.Append(property.Value);
                query.Append(space);

            }

            return query.ToString();
        }
        private static string GetSeparateEntityKey(string key)
        {
            if (key.ToLower().StartsWith("seperateentity"))
                return key.Substring(15);
            throw new Exception("is not separate Entity");
        }

        private static string GetFqnFromCachekey(string key)
        {
            key = GetSeparateEntityKey(key);
            int index = key.IndexOf(":");
            if (index >= 0)
                return key.Substring(0, index);
            else
                throw new Exception("Key is not valid");
        }
        private static string GetClassNameFromCachekey(string key)
        {
            key = GetSeparateEntityKey(key);
            int index = key.IndexOf(":");
            if (index >= 0)
            {
                string nKey = key.Substring(0, index);
                return nKey.Substring(nKey.LastIndexOf(".") + 1);
            }
            else
                throw new Exception("Key is not valid");

        }

        private static IDictionary GetPrimaryKeysFromCacheKey(string key)
        {
            key = GetSeparateEntityKey(key);

            IDictionary pkPairs = new Dictionary<string, string>();
            string[] parts = key.Split(':');

            int startingPoint = parts[0].Length + 1;
            bool isFqn = true;
            foreach (var part in parts)
            {
                if (isFqn)
                {
                    isFqn = false;
                    continue;
                }
                string pk = key.Substring(startingPoint, part.IndexOf("="));
                string value = part.Substring(part.IndexOf("=") + 1);

                pkPairs.Add(pk, value);
                startingPoint = (startingPoint + 1) + (part.Length);
            }
            return pkPairs;


        }

        #endregion

        /// <summary>
        /// Generates the cache key for the entity specified.
        /// </summary>
        /// <param name="context">The context which will be used to generate the key.</param>
        /// <param name="entity">The entity whos key is to be generated.</param>
        /// <returns>Returns the cache key.</returns>
        public override string GetKey(DbContext context, object entity)
        {
            HashSet<object> visitedEntities = new HashSet<object>();
            return GetKeyInternal(context, entity, visitedEntities);
        }

        private string GetKeyInternal(DbContext context, object entity, HashSet<object> visitedEntities)
        {
            // No need to check if object entity is of DbContext Entity, Already done while registering

            // Check for custom implementation
            lock (_registeredGenerators)
            {
                KeyGenerator keyGen;
                if (_registeredGenerators.TryGetValue(entity.GetType(), out keyGen))
                {
                    return keyGen.GetKey(context, entity);
                }
            }

            // Default Implementation
            string key = "SeperateEntity_";
            IEntityType eType = context.Model.FindEntityType(entity.GetType());

            if (eType == null)
                throw new Exception("Entity type and context do not match");
            else
            {
                // If entity is visited already return empty string
                if (visitedEntities.Contains(entity))
                    return string.Empty;

                // Else visit entity
                key += GetEntityTypeName(eType);

                IKey pKey = eType.FindPrimaryKey();

                foreach (var property in pKey.Properties)
                {
                    object value = entity.GetType().GetProperty(property.Name).GetValue(entity);
                    key += seperator + property.Name + "=" + value.ToString();
                }
                // Add entity to visited list
                visitedEntities.Add(entity);

                IEnumerator<INavigation> navigations = eType.GetNavigations().GetEnumerator();
                while (navigations.MoveNext())
                {
                    string dependentEntityName = navigations.Current.Name;
                    var dependentEntityValue = entity.GetType().GetProperty(dependentEntityName).GetValue(entity);

                    if (dependentEntityValue == null)
                        return key;
                    else
                    {
                        Type dependentEntityType = dependentEntityValue.GetType();
                        if (dependentEntityType.IsGenericType && dependentEntityValue is IEnumerable)
                        {
                            var dependentEntities = ((IEnumerable)dependentEntityValue);
                            var enumerator = dependentEntities.GetEnumerator();
                            while (enumerator.MoveNext())
                            {
                                string internalKey = GetKeyInternal(context, enumerator.Current, visitedEntities);
                                if (!string.IsNullOrEmpty(internalKey))
                                    key += seperator + internalKey;
                            }
                        }
                        else
                        {
                            string internalKey = GetKeyInternal(context, dependentEntityValue, visitedEntities);
                            if (!string.IsNullOrEmpty(internalKey))
                                key += seperator + internalKey;
                        }
                    }
                }
            }
            return key;
        }

        private string GetEntityTypeName(IEntityType entityType)
        {
            string name = default(string);

            PropertyInfo nameProperty = entityType.GetType().GetRuntimeProperty("Name");

            if (nameProperty != null)
            {
                name = nameProperty.GetValue(entityType).ToString();
            }
            return name;
        }
    }
}
