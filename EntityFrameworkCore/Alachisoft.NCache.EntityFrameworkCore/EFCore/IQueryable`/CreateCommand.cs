// Description: Entity Framework Bulk Operations & Utilities (EF Bulk SaveChanges, Insert, Update, Delete, Merge | LINQ Query Cache, Deferred, Filter, IncludeFilter, IncludeOptimize | Audit)
// Website & Documentation: https://github.com/zzzprojects/Entity-Framework-Plus
// Forum & Issues: https://github.com/zzzprojects/EntityFramework-Plus/issues
// License: https://github.com/zzzprojects/EntityFramework-Plus/blob/master/LICENSE
// More projects: http://www.zzzprojects.com/
// Copyright © ZZZ Projects Inc. 2014 - 2016. All rights reserved.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;


namespace Alachisoft.NCache.EntityFrameworkCore
{
    internal static partial class InternalExtensions
    {
        public static IRelationalCommand CreateCommand<T>(this IQueryable<T> source, out RelationalQueryContext queryContext)
        {
            var compilerField = typeof(EntityQueryProvider).GetField("_queryCompiler", BindingFlags.NonPublic | BindingFlags.Instance);
            var compiler = compilerField.GetValue(source.Provider);            
                
            var queryContextFactoryField = compiler.GetType().GetField("_queryContextFactory", BindingFlags.NonPublic | BindingFlags.Instance);
            var queryContextFactory = (IQueryContextFactory)queryContextFactoryField.GetValue(compiler);

            queryContext = (RelationalQueryContext)queryContextFactory.Create();

            var evalutableExpressionFilterField = compiler.GetType().GetField("_evaluatableExpressionFilter", BindingFlags.NonPublic | BindingFlags.Instance);

            var evalutableExpressionFilter = (IEvaluatableExpressionFilter)evalutableExpressionFilterField.GetValue(compiler);///*null*/queryModelGenerator);

            var databaseField = compiler.GetType().GetField("_database", BindingFlags.NonPublic | BindingFlags.Instance);
            var database = (IDatabase)databaseField.GetValue(compiler);

            // REFLECTION: Query.Provider._queryCompiler
            var queryCompilerField = typeof(EntityQueryProvider).GetField("_queryCompiler", BindingFlags.NonPublic | BindingFlags.Instance);
            var queryCompiler = queryCompilerField.GetValue(source.Provider);

            // REFLECTION: Query.Provider._queryCompiler._evaluatableExpressionFilter
            var evaluatableExpressionFilterField = compiler.GetType().GetField("_evaluatableExpressionFilter", BindingFlags.NonPublic | BindingFlags.Instance);
            var evaluatableExpressionFilter = (IEvaluatableExpressionFilter)evaluatableExpressionFilterField.GetValue(compiler);//*null*/queryModelGenerator);

            Expression newQuery;
            IQueryCompilationContextFactory queryCompilationContextFactory;

            var dependenciesProperty = typeof(Database).GetProperty("Dependencies", BindingFlags.NonPublic | BindingFlags.Instance);
            if (dependenciesProperty != null)
            {
                var dependencies = dependenciesProperty.GetValue(database);

                var queryCompilationContextFactoryField = typeof(DbContext).GetTypeFromAssembly_Core("Microsoft.EntityFrameworkCore.Storage.DatabaseDependencies")
                                                                            .GetProperty("QueryCompilationContextFactory", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                queryCompilationContextFactory = (IQueryCompilationContextFactory)queryCompilationContextFactoryField.GetValue(dependencies);
#if NET6_0_OR_GREATER
                var dependenciesProperty2 = queryCompilationContextFactory.GetType().GetField("<Dependencies>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
#else
                var dependenciesProperty2 = typeof(QueryCompilationContextFactory).GetField("_dependencies", BindingFlags.NonPublic | BindingFlags.Instance);
#endif
                var dependencies2 = dependenciesProperty2.GetValue(queryCompilationContextFactory);

                // REFLECTION: Query.Provider._queryCompiler._database._queryCompilationContextFactory.Logger
                var loggerField = typeof(DbContext).GetTypeFromAssembly_Core("Microsoft.EntityFrameworkCore.Query.QueryCompilationContextDependencies")
                                                    .GetProperty("Logger", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var logger = loggerField.GetValue(dependencies2);
                    
                var parameterExtractingExpressionVisitorConstructor = typeof(ParameterExtractingExpressionVisitor).GetConstructors().First(x => x.GetParameters().Length >= 5);
                int paramsCount = parameterExtractingExpressionVisitorConstructor.GetParameters().Length;
                var parameterExtractingExpressionVisitor = default(ParameterExtractingExpressionVisitor);
                if (paramsCount == 7)
                {
                    var contextType = compiler.GetType().GetField("_contextType", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(compiler);
                    var model = compiler.GetType().GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(compiler);
                    parameterExtractingExpressionVisitor = (ParameterExtractingExpressionVisitor)parameterExtractingExpressionVisitorConstructor.Invoke(new object[] { evaluatableExpressionFilter, queryContext, contextType, model, logger, false, false });

                }

                    
                // CREATE new query from query visitor
                newQuery = parameterExtractingExpressionVisitor.ExtractParameters(source.Expression);
            }
            else
            {
                // REFLECTION: Query.Provider._queryCompiler._database._queryCompilationContextFactory
                var queryCompilationContextFactoryField = typeof(Database).GetField("_queryCompilationContextFactory", BindingFlags.NonPublic | BindingFlags.Instance);
                queryCompilationContextFactory = (IQueryCompilationContextFactory)queryCompilationContextFactoryField.GetValue(database);

                // REFLECTION: Query.Provider._queryCompiler._database._queryCompilationContextFactory.Logger
                var loggerField = queryCompilationContextFactory.GetType().GetProperty("Logger", BindingFlags.NonPublic | BindingFlags.Instance);
                var logger = loggerField.GetValue(queryCompilationContextFactory);

                // CREATE new query from query visitor
                var extractParametersMethods = typeof(ParameterExtractingExpressionVisitor).GetMethod("ExtractParameters", BindingFlags.Public | BindingFlags.Static);
                newQuery = (Expression)extractParametersMethods.Invoke(null, new object[] { source.Expression, queryContext, evaluatableExpressionFilter, logger });
            }
            
            var enumerator = source.Provider.Execute<IEnumerable<T>>(newQuery).GetEnumerator();
            var enumeratorType = enumerator.GetType();
            var relationalCommandCachefield = enumeratorType.GetField("_relationalCommandCache", BindingFlags.NonPublic | BindingFlags.Instance);
            var relationalCommandCache = relationalCommandCachefield.GetValue(enumerator);
            var selectExpressionField = relationalCommandCache.GetType().GetField("_selectExpression", BindingFlags.NonPublic | BindingFlags.Instance);
            var selectExpression = (Microsoft.EntityFrameworkCore.Query.SqlExpressions.SelectExpression)selectExpressionField.GetValue(relationalCommandCache);
            var factoryField = relationalCommandCache.GetType().GetField("_querySqlGeneratorFactory", BindingFlags.NonPublic | BindingFlags.Instance);
            var factory = (IQuerySqlGeneratorFactory)factoryField.GetValue(relationalCommandCache);

            var sqlGenerator = factory.Create();
            var command = sqlGenerator.GetCommand(selectExpression);
           
            return command;
           
        }

        public static void CreateCommand(this IQueryable source, out RelationalQueryContext queryContext)
        {
            var compilerField = typeof(EntityQueryProvider).GetField("_queryCompiler", BindingFlags.NonPublic | BindingFlags.Instance);
            var compiler = compilerField.GetValue(source.Provider);

            var nodeTypeProviderField = compiler.GetType().GetProperty("NodeTypeProvider", BindingFlags.NonPublic | BindingFlags.Instance);
            
            var queryContextFactoryField = compiler.GetType().GetField("_queryContextFactory", BindingFlags.NonPublic | BindingFlags.Instance);
            var queryContextFactory = (IQueryContextFactory)queryContextFactoryField.GetValue(compiler);

            queryContext = (RelationalQueryContext)queryContextFactory.Create();                 
                      
        }
    }
}