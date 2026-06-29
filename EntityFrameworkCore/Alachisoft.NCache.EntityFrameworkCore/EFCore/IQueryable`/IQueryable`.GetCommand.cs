// Description: Entity Framework Bulk Operations & Utilities (EF Bulk SaveChanges, Insert, Update, Delete, Merge | LINQ Query Cache, Deferred, Filter, IncludeFilter, IncludeOptimize | Audit)
// Website & Documentation: https://github.com/zzzprojects/Entity-Framework-Plus
// Forum & Issues: https://github.com/zzzprojects/EntityFramework-Plus/issues
// License: https://github.com/zzzprojects/EntityFramework-Plus/blob/master/LICENSE
// More projects: http://www.zzzprojects.com/
// Copyright © ZZZ Projects Inc. 2014 - 2016. All rights reserved.

using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace Alachisoft.NCache.EntityFrameworkCore
{
    internal static partial class InternalExtensions
    {
        public static IRelationalCommand GetDbCommand<T>(this IQueryable<T> query)
        {
            
            // REFLECTION: Query._context
            var contextField = query.GetType().GetField("_context", BindingFlags.NonPublic | BindingFlags.Instance);
            var context = (DbContext)contextField.GetValue(query);

            // REFLECTION: Query._context.StateManager
            var stateManagerProperty = typeof(DbContext).GetProperty("StateManager", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateManager = (StateManager)stateManagerProperty.GetValue(context);

            // REFLECTION: Query._context.StateManager._concurrencyDetector
            var concurrencyDetectorField = typeof(StateManager).GetField("_concurrencyDetector", BindingFlags.NonPublic | BindingFlags.Instance);
            var concurrencyDetector = (IConcurrencyDetector)concurrencyDetectorField.GetValue(stateManager);

            // REFLECTION: Query.Provider._queryCompiler
            var queryCompilerField = typeof(EntityQueryProvider).GetField("_queryCompiler", BindingFlags.NonPublic | BindingFlags.Instance);
            var queryCompiler = queryCompilerField.GetValue(query.Provider);            

            // REFLECTION: Query.Provider._queryCompiler._database
            var databaseField = queryCompiler.GetType().GetField("_database", BindingFlags.NonPublic | BindingFlags.Instance);
            var database = (IDatabase)databaseField.GetValue(queryCompiler);

            // REFLECTION: Query.Provider._queryCompiler._evaluatableExpressionFilter
            var evaluatableExpressionFilterField = queryCompiler.GetType().GetField("_evaluatableExpressionFilter", BindingFlags.NonPublic | BindingFlags.Static);
            var evaluatableExpressionFilter = (IEvaluatableExpressionFilter)evaluatableExpressionFilterField.GetValue(null);

            // REFLECTION: Query.Provider._queryCompiler._queryContextFactory
            var queryContextFactoryField = queryCompiler.GetType().GetField("_queryContextFactory", BindingFlags.NonPublic | BindingFlags.Instance);
            var queryContextFactory = (IQueryContextFactory)queryContextFactoryField.GetValue(queryCompiler);

            // REFLECTION: Query.Provider._queryCompiler._queryContextFactory.CreateQueryBuffer
            var createQueryBufferDelegateMethod = (typeof(IQueryContextFactory)).GetMethod("CreateQueryBuffer", BindingFlags.NonPublic | BindingFlags.Instance);

            // REFLECTION: Query.Provider._queryCompiler._queryContextFactory._connection
            var relationalDependencyFiled = queryContextFactory.GetType().GetField("_relationalDependencies", BindingFlags.NonPublic | BindingFlags.Instance);
            var relationalDependency = (RelationalQueryContextDependencies)relationalDependencyFiled.GetValue(queryContextFactory);

            // REFLECTION: Query.Provider._queryCompiler._queryContextFactory._connection
            var connectionField = relationalDependency.GetType().GetField("RelationalConnection", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            var connection = (IRelationalConnection)connectionField.GetValue(relationalDependency);

            // REFLECTION: Query.Provider._queryCompiler._database._queryCompilationContextFactory
            object logger;

            var dependenciesProperty = typeof(Database).GetProperty("Dependencies", BindingFlags.NonPublic | BindingFlags.Instance);
            IQueryCompilationContextFactory queryCompilationContextFactory;
            
            
            var dependencies = dependenciesProperty.GetValue(database);

            var queryCompilationContextFactoryField = typeof(DbContext).GetTypeFromAssembly_Core("Microsoft.EntityFrameworkCore.Storage.DatabaseDependencies")
                                                                        .GetProperty("QueryCompilationContextFactory", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                
            queryCompilationContextFactory = (IQueryCompilationContextFactory)queryCompilationContextFactoryField.GetValue(dependencies);

            var dependenciesProperty2 = typeof(QueryCompilationContextFactory).GetProperty("Dependencies", BindingFlags.NonPublic | BindingFlags.Instance);
            var dependencies2 = dependenciesProperty2.GetValue(queryCompilationContextFactory);

            // REFLECTION: Query.Provider._queryCompiler._database._queryCompilationContextFactory.Logger
            var loggerField =  typeof(DbContext).GetTypeFromAssembly_Core("Microsoft.EntityFrameworkCore.Query.Internal.QueryCompilationContextDependencies")
                                                .GetProperty("Logger", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            // (IInterceptingLogger<LoggerCategory.Query>)
            logger = loggerField.GetValue(dependencies2);
            
            

            // CREATE query context
            RelationalQueryContext queryContext;
            {
                var relationalQueryContextType = typeof(RelationalQueryContext);
                var relationalQueryContextConstructor = relationalQueryContextType.GetConstructors()[0];
                queryContext = (RelationalQueryContext) relationalQueryContextConstructor.Invoke(new object[] { connection, relationalDependency });
                
            }

            
            Expression newQuery;

            
            var parameterExtractingExpressionVisitorConstructor = typeof(ParameterExtractingExpressionVisitor).GetConstructors().First(x => x.GetParameters().Length == 5);

            var parameterExtractingExpressionVisitor = (ParameterExtractingExpressionVisitor)parameterExtractingExpressionVisitorConstructor.Invoke(new object[] {evaluatableExpressionFilter, queryContext, logger, false, false} );
            
            // CREATE new query from query visitor
            newQuery = parameterExtractingExpressionVisitor.ExtractParameters(query.Expression);
           
            var enumerator = query.Provider.Execute(newQuery);
            var enumeratorType = enumerator.GetType();
            var relationalCommandCachefield = enumeratorType.GetField("_relationalCommandCache", BindingFlags.NonPublic | BindingFlags.Instance);
            var relationalCommandCache = relationalCommandCachefield.GetValue(enumerator);
#if NET8_0_OR_GREATER
            var selectExpressionField = relationalCommandCache.GetType().GetField("_queryExpression", BindingFlags.NonPublic | BindingFlags.Instance);
#else
            var selectExpressionField = relationalCommandCache.GetType().GetField("_selectExpression", BindingFlags.NonPublic | BindingFlags.Instance);
#endif
            var selectExpression = (Microsoft.EntityFrameworkCore.Query.SqlExpressions.SelectExpression)selectExpressionField.GetValue(relationalCommandCache);
            var factoryField = relationalCommandCache.GetType().GetField("_querySqlGeneratorFactory", BindingFlags.NonPublic | BindingFlags.Instance);
            var factory = (IQuerySqlGeneratorFactory)factoryField.GetValue(relationalCommandCache);

            var sqlGenerator = factory.Create();
            var command = sqlGenerator.GetCommand(selectExpression);
            return command;
        }
    }
}