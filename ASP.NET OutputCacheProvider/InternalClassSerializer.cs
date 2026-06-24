using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Alachisoft.NCache.OutputCacheProvider
{
    public static class InternalClassSerializer
    {
        public static JObject SerializeFields(object obj)
        {
            return SerializeFieldsInternal(obj, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        private static JObject SerializeFieldsInternal(object obj, HashSet<object> visited)
        {
            if (obj == null)
                return null;

            var result = new JObject();
            Type type = obj.GetType();
            result["Type"] = type.AssemblyQualifiedName;

            if (IsSimpleType(type))
            {
                result["Value"] = new JValue(obj);
            }
            else if (type.IsArray)
            {
                var array = (Array)obj;
                var jArray = new JArray();
                for (int i = 0; i < array.Length; i++)
                {
                    object element = array.GetValue(i);
                    jArray.Add(element == null ? null : SerializeFieldsInternal(element, visited));
                }
                result["Value"] = jArray;
            }
            else if (typeof(IList).IsAssignableFrom(type))
            {
                var list = (IList)obj;
                var jArray = new JArray();
                foreach (var element in list)
                {
                    jArray.Add(element == null ? null : SerializeFieldsInternal(element, visited));
                }
                result["Value"] = jArray;
            }
            else
            {
                if (!visited.Add(obj))
                {
                    result["Value"] = "[Circular Reference]";
                    return result;
                }

                var fieldsObject = new JObject();
                FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                foreach (FieldInfo field in fields)
                {
                    object fieldValue = field.GetValue(obj);
                    if (fieldValue == null)
                    {
                        var nullField = new JObject();
                        nullField["Type"] = field.FieldType.AssemblyQualifiedName;
                        nullField["Value"] = null;
                        fieldsObject[field.Name] = nullField;
                    }
                    else
                    {
                        fieldsObject[field.Name] = SerializeFieldsInternal(fieldValue, visited);
                    }
                }

                visited.Remove(obj);
                result["Value"] = fieldsObject;
            }

            return result;
        }

        public static object Deserialize(JObject serialized)
        {
            if (serialized == null)
                return null;

            return DeserializeInternal(serialized, new Dictionary<string, object>());
        }

        private static object DeserializeInternal(JToken token, Dictionary<string, object> visited)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            var obj = token as JObject;
            if (obj == null)
                return null;

            string typeName = obj["Type"]?.ToString();
            if (string.IsNullOrEmpty(typeName))
                return null;

            Type type = Type.GetType(typeName);
            if (type == null)
            {
                throw new InvalidOperationException($"Cannot resolve type: {typeName}");
            }

            JToken valueToken = obj["Value"];
            if (valueToken == null || valueToken.Type == JTokenType.Null)
                return null;

            if (IsSimpleType(type))
            {
                return ((JValue)valueToken).Value;
            }

            if (valueToken.Type == JTokenType.String && valueToken.ToString() == "[Circular Reference]")
            {
                return null;
            }

            if (type.IsArray)
            {
                var jArray = valueToken as JArray;
                if (jArray != null)
                {
                    Type elementType = type.GetElementType();
                    var array = Array.CreateInstance(elementType, jArray.Count);
                    for (int i = 0; i < jArray.Count; i++)
                    {
                        object elementValue = DeserializeInternal(jArray[i], visited);
                        if (elementValue != null && !elementType.IsInstanceOfType(elementValue))
                        {
                            elementValue = ConvertValue(elementValue, elementType);
                        }
                        array.SetValue(elementValue, i);
                    }
                    return array;
                }
                return null;
            }

            if (typeof(IList).IsAssignableFrom(type))
            {
                var jArray = valueToken as JArray;
                if (jArray != null)
                {
                    IList listInstance = (IList)Activator.CreateInstance(type);
                    if (type.IsGenericType)
                    {
                        Type elementType = type.GetGenericArguments()[0];
                        for (int i = 0; i < jArray.Count; i++)
                        {
                            object elementValue = DeserializeInternal(jArray[i], visited);
                            if (elementValue != null && !elementType.IsInstanceOfType(elementValue))
                            {
                                elementValue = ConvertValue(elementValue, elementType);
                            }
                            listInstance.Add(elementValue);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < jArray.Count; i++)
                        {
                            listInstance.Add(DeserializeInternal(jArray[i], visited));
                        }
                    }
                    return listInstance;
                }
                return null;
            }

            // Create uninitialized instance bypassing standard constructor calls
            object instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);

            var fieldsObject = valueToken as JObject;
            if (fieldsObject != null)
            {
                foreach (var property in fieldsObject.Properties())
                {
                    string fieldName = property.Name;
                    JToken fieldToken = property.Value;

                    FieldInfo field = GetFieldInherited(type, fieldName);
                    if (field != null)
                    {
                        object fieldValue = DeserializeInternal(fieldToken, visited);
                        if (fieldValue != null)
                        {
                            Type targetType = field.FieldType;
                            if (!targetType.IsInstanceOfType(fieldValue))
                            {
                                fieldValue = ConvertValue(fieldValue, targetType);
                            }
                        }
                        field.SetValue(instance, fieldValue);
                    }
                }
            }

            return instance;
        }

        private static FieldInfo GetFieldInherited(Type type, string fieldName)
        {
            Type currentType = type;
            while (currentType != null && currentType != typeof(object))
            {
                FieldInfo field = currentType.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
                currentType = currentType.BaseType;
            }
            return null;
        }

        private static object ConvertValue(object value, Type targetType)
        {
            if (value == null)
                return null;

            Type underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (underlyingType.IsEnum)
            {
                if (value is string str)
                    return Enum.Parse(underlyingType, str);
                return Enum.ToObject(underlyingType, value);
            }

            if (underlyingType == typeof(Guid))
            {
                if (value is string str)
                    return new Guid(str);
            }

            if (underlyingType == typeof(DateTimeOffset))
            {
                if (value is string str)
                    return DateTimeOffset.Parse(str);
            }

            if (underlyingType == typeof(TimeSpan))
            {
                if (value is string str)
                    return TimeSpan.Parse(str);
            }

            return Convert.ChangeType(value, underlyingType);
        }

        public static bool IsSimpleType(Type type)
        {
            Type underlyingType = Nullable.GetUnderlyingType(type) ?? type;

            return underlyingType.IsPrimitive ||
                   underlyingType == typeof(string) ||
                   underlyingType == typeof(decimal) ||
                   underlyingType == typeof(DateTime) ||
                   underlyingType == typeof(DateTimeOffset) ||
                   underlyingType == typeof(TimeSpan) ||
                   underlyingType == typeof(Guid) ||
                   underlyingType.IsEnum;
        }
    }
}