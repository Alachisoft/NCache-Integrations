using Alachisoft.NCache.EntityFrameworkCore.NCache;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;

namespace Alachisoft.NCache.EntityFrameworkCore.NCLinq
{
    internal class ExtentsGenerator
    {
        private Dictionary<Expression, string> _extenets;
        private readonly string _extentPrefix = "Extent";
        private int _extentNum;

        public string ExtentPrefix => _extentPrefix;

        internal ExtentsGenerator()
        {
            _extenets = new Dictionary<Expression, string>();
            _extentNum = 0;
        }

        internal ExtentsGenerator(string extentPrefix)
            : this()
        {
            _extentPrefix = extentPrefix;
        }

        internal string GetExtent(Expression node)
        {
            lock (_extenets)
            {
                if (_extenets.ContainsKey(node))
                {
                    return _extenets[node];
                }
                else
                {
                    string extent = GetNextExtent();
                    _extenets.Add(node, extent);
                    return extent;
                }
            }
        }

        internal string GetNextExtent()
        {
            return _extentPrefix + ++_extentNum;
        }
    }
}
