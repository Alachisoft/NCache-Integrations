using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace NCache.OSS.Dapper
{
    public static class CacheKeyGenerator
    {
        /// <summary>
        /// Generates a cache key for the given database, SQL text and parameters.
        /// </summary>
        /// <param name="databaseName">Name of the database the query targets, used to isolate keys per database instance.</param>
        /// <param name="sql">The SQL text being executed.</param>
        /// <param name="param">The query parameters, if any.</param>
        /// <param name="resultType">
        /// The CLR type the result is being materialized into. This MUST be included in the key:
        /// otherwise identical SQL/params requested as two different result shapes (e.g. a full
        /// entity vs. a projection DTO) would collide on the same cache entry and return the
        /// wrong-shaped object.
        /// </param>
        public static string GenerateCacheKey(string databaseName, string sql, object? param, Type resultType)
        {
            if (resultType == null) throw new ArgumentNullException(nameof(resultType));

            var sb = new StringBuilder();

            // Isolate keys per database instance
            sb.Append(GetSha256Hash(databaseName ?? string.Empty));
            sb.Append(":");

            // Isolate keys per requested result type
            sb.Append(GetSha256Hash(resultType.AssemblyQualifiedName ?? resultType.FullName ?? resultType.Name));
            sb.Append(":");

            // Normalize SQL text to avoid duplicate entries due to capitalization or spacing differences
            sb.Append(NormalizeSql(sql));
            sb.Append(":");

            // Canonicalize and serialize parameters
            if (param != null)
            {
                sb.Append(SerializeParams(param));
            }

            // Output SHA256 of combined representation for compact key size
            return "NCache.OSS.Dapper:" + GetSha256Hash(sb.ToString());
        }

        private static string NormalizeSql(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return string.Empty;

            var sb = new StringBuilder();
            bool inWhitespace = false;
            foreach (char c in sql)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!inWhitespace)
                    {
                        sb.Append(' ');
                        inWhitespace = true;
                    }
                }
                else
                {
                    sb.Append(char.ToLowerInvariant(c));
                    inWhitespace = false;
                }
            }
            return sb.ToString().Trim();
        }

        private static string SerializeParams(object param)
        {
            if (param is DynamicParameters dynamicParams)
            {
                var lookup = (SqlMapper.IParameterLookup)dynamicParams;
                var ordered = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                foreach (var name in dynamicParams.ParameterNames)
                {
                    ordered[name] = lookup[name];
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(
                    ordered,
                    new Newtonsoft.Json.JsonSerializerSettings
                    {
                        Formatting = Newtonsoft.Json.Formatting.None,
                        NullValueHandling = Newtonsoft.Json.NullValueHandling.Include
                    });
            }

            return Newtonsoft.Json.JsonConvert.SerializeObject(
                param,
                new Newtonsoft.Json.JsonSerializerSettings
                {
                    Formatting = Newtonsoft.Json.Formatting.None,
                    NullValueHandling = Newtonsoft.Json.NullValueHandling.Include
                });
        }

        private static string GetSha256Hash(string input)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
                return BytesToHex(bytes);
            }
        }

        private static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }
    }
}