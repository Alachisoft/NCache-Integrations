using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace Alachisoft.NCache.EntityFrameworkCore
{
    public class KeyInfo
    {
        private string _fqn = "";
        private string _className = "";
        private IDictionary _pkPairs =null;

        public string Fqn { get { return _fqn; } }
        public string ClassName { get { return _className; } }
        public IDictionary PKPairs { get { return _pkPairs; } }


        internal KeyInfo(string fqn,string className,IDictionary pkPairs)
        {
            _fqn = fqn;
            _className = className;
            _pkPairs = pkPairs;
        }
    }
}
