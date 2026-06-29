// Description: Entity Framework Bulk Operations & Utilities (EF Bulk SaveChanges, Insert, Update, Delete, Merge | LINQ Query Cache, Deferred, Filter, IncludeFilter, IncludeOptimize | Audit)
// Website & Documentation: https://github.com/zzzprojects/Entity-Framework-Plus
// Forum & Issues: https://github.com/zzzprojects/EntityFramework-Plus/issues
// License: https://github.com/zzzprojects/EntityFramework-Plus/blob/master/LICENSE
// More projects: http://www.zzzprojects.com/
// Copyright © ZZZ Projects Inc. 2014 - 2016. All rights reserved.

using System;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using System.Collections.Generic;

namespace Alachisoft.NCache.EntityFrameworkCore
{
    internal static partial class InternalExtensions
    {
        public static DbContext GetDbContext<T>(this IQueryable<T> source)
        {
            var stateManager = source.GetStateManager();
            return stateManager.Context;
        }

        public static IStateManager GetStateManager<T>(this IQueryable<T> source)
        {
            var compilerField = typeof(EntityQueryProvider).GetField("_queryCompiler", BindingFlags.NonPublic | BindingFlags.Instance);
            var compiler = (QueryCompiler)compilerField.GetValue(source.Provider);

            var queryContextFactoryField = compiler.GetType().GetField("_queryContextFactory", BindingFlags.NonPublic | BindingFlags.Instance);
            dynamic queryContextFactory = queryContextFactoryField.GetValue(compiler);

            return GetStateManagerInternal(source, queryContextFactory);
        }

        /// <summary>An IQueryable extension method that gets database context from the query.</summary>
        /// <param name="query">The query to act on.</param>
        /// <returns>The database context from the query.</returns>
        public static DbContext GetDbContext(this IQueryable query)
        {
            var compilerField = typeof(EntityQueryProvider).GetField("_queryCompiler", BindingFlags.NonPublic | BindingFlags.Instance);
            var compiler = (QueryCompiler)compilerField.GetValue(query.Provider);

            var queryContextFactoryField = compiler.GetType().GetField("_queryContextFactory", BindingFlags.NonPublic | BindingFlags.Instance);
            dynamic queryContextFactory = queryContextFactoryField.GetValue(compiler);

            object stateManagerDynamic;

            var dependenciesProperty = typeof(RelationalQueryContextFactory).GetProperty("Dependencies", BindingFlags.NonPublic | BindingFlags.Instance);
           
            var dependencies = dependenciesProperty.GetValue(queryContextFactory);

            var stateManagerField = typeof(DbContext).GetTypeFromAssembly_Core("Microsoft.EntityFrameworkCore.Query.QueryContextDependencies").GetProperty("StateManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            stateManagerDynamic = stateManagerField.GetValue(dependencies);            

            IStateManager stateManager = stateManagerDynamic as IStateManager;

            if (stateManager == null)
            {
                Lazy<IStateManager> lazyStateManager = stateManagerDynamic as Lazy<IStateManager>;
                if (lazyStateManager != null)
                {
                    stateManager = lazyStateManager.Value;
                }
            }

            if (stateManager == null)
            {
                stateManager = ((dynamic)stateManagerDynamic).Value;
            }

            return stateManager.Context;
        }

        private static IStateManager GetStateManagerInternal<T>(IQueryable<T> source, object queryContextFactory)
        {
            object stateManagerDynamic;

            
            List<FieldInfo> list = queryContextFactory.GetType().GetRuntimeFields().ToList();

            FieldInfo dependenciesField = null;
#if NET6_0_OR_GREATER
            foreach (FieldInfo property in list)
            {
                if (property.Name.Contains("Dependencies"))
                {
                    dependenciesField = property;
                    break;
                }
            }
#else
            foreach (FieldInfo property in list)
            {
                if (property.Name.Equals("_dependencies"))
                {
                    dependenciesField = property;
                    break;
                }
            }             
#endif  
            var dependencies = dependenciesField.GetValue(queryContextFactory);

            var stateManagerField = typeof(DbContext).GetTypeFromAssembly_Core("Microsoft.EntityFrameworkCore.Query.QueryContextDependencies").GetProperty("StateManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            stateManagerDynamic = stateManagerField.GetValue(dependencies);
           
            IStateManager stateManager = stateManagerDynamic as IStateManager;

            if (stateManager == null)
            {
                Lazy<IStateManager> lazyStateManager = stateManagerDynamic as Lazy<IStateManager>;
                if (lazyStateManager != null)
                {
                    stateManager = lazyStateManager.Value;
                }
            }

            if (stateManager == null)
            {
                stateManager = ((dynamic)stateManagerDynamic).Value;
            }
            return stateManager;
        }
    }
}