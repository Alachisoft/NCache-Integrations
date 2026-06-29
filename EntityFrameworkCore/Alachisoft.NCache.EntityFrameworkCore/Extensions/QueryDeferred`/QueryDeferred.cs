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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Remotion.Linq;

namespace Alachisoft.NCache.EntityFrameworkCore
{
    /// <summary>A class to store immediate LINQ IQueryable query and expression deferred.</summary>
    /// <typeparam name="TResult">Type of the result of the query deferred.</typeparam>
    public class QueryDeferred<TResult>
    {
        /// <summary>Constructor.</summary>
        /// <param name="query">The deferred query.</param>
        /// <param name="expression">The deferred expression.</param>
        internal QueryDeferred(IQueryable query, Expression expression)
        {
            Expression = expression;
            Query = new EntityQueryable<TResult>((IAsyncQueryProvider)query.Provider, expression);
        }

        /// <summary>Gets or sets the deferred expression.</summary>
        /// <value>The deferred expression.</value>
        internal Expression Expression { get; set; }

        /// <summary>Gets or sets the deferred query.</summary>
        /// <value>The deferred query.</value>
        internal IQueryable<TResult> Query { get; set; }

        /// <summary>Execute the deferred expression and return the result.</summary>
        /// <returns>The result of the deferred expression executed.</returns>
        internal TResult Execute()
        {
            return Query.Provider.Execute<TResult>(Expression);
        }
    }
}