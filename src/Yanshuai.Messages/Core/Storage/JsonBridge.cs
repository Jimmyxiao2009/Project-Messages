// Core/Storage/JsonBridge.cs —— 对象与 JSON 之间的映射
//
// 为什么不用 Newtonsoft 的 JsonConvert：
// 在 UWP 5.4.7（Windows 10 Mobile 15254 自带的那版框架）上，Newtonsoft 的
// JsonSerializer/JsonSerializerSettings 一构造就抛 FieldAccessException
// （静态构造访问自己的 StreamingContext 字段失败），于是**所有** JSON 读写全废。
// 实测过的替代版本（10.0.3 / 12 / 13.0.3）都一样，换版本解决不了。
//
// 但它的**分词器是好的**：JObject / JToken / JsonTextReader / JsonTextWriter 都能正常工作。
// 所以这里保留它做词法与转义（这部分最容易写错：转义、代理对、数字格式），
// 只用反射自己做"对象 ↔ 节点树"的映射。
//
// 行为刻意与原来的 Newtonsoft 配置对齐，保证已有文件读得回来：
//   · 空值不写（NullValueHandling.Ignore）
//   · 认不出的字段忽略（MissingMemberHandling.Ignore）
//   · 日期写成 ISO 字符串，读的时候**不**自动转成日期类型，
//     这样 DateTimeOffset 的时区偏移不会被吞掉
//   · 枚举写成整数（Newtonsoft 的默认行为，已有文件里就是整数）
//
// 反射用的是 System.Reflection 的扩展方法（GetRuntimeProperties 等）：
// UWP 的 System.Type 上没有 GetProperties()，只有走扩展方法这条路。
// .NET Native 下这些反射需要 rd.xml 覆盖到模型类型 —— Default.rd.xml 里已经逐个声明了。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Yanshuai.Messages.Core
{
    public static class JsonBridge
    {
        // 每个类型只算一次属性表。反射在 .NET Native 上不便宜，而序列化很频繁。
        private static readonly Dictionary<Type, PropertyMap[]> _cache =
            new Dictionary<Type, PropertyMap[]>();

        private static readonly object _cacheGate = new object();

        // ═════════════════════════════════════════════════════════════════
        // 对外
        // ═════════════════════════════════════════════════════════════════

        public static string Serialize(object value, bool indented)
        {
            var token = ToToken(value);
            return token == null
                ? "null"
                : token.ToString(indented ? Formatting.Indented : Formatting.None);
        }

        public static T Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var token = Parse(json);
            if (token == null) return null;

            var value = FromToken(token, typeof(T));
            return value as T;
        }

        /// <summary>深拷贝。走一遍序列化再读回来，比手写 Clone 不容易漏字段。</summary>
        public static T Clone<T>(T value) where T : class
        {
            if (value == null) return null;
            return Deserialize<T>(Serialize(value, false));
        }

        public static JToken Parse(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)))
            {
                // 关掉日期自动转换：否则 "2026-10-12T01:00:00+08:00" 会被读成
                // 本机时区的 DateTime，DateTimeOffset 上原本的偏移就丢了。
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Double;
                return JToken.ReadFrom(reader);
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // 对象 → 节点树
        // ═════════════════════════════════════════════════════════════════

        public static JToken ToToken(object value)
        {
            if (value == null) return JValue.CreateNull();
            return WriteValue(value, value.GetType());
        }

        private static JToken WriteValue(object value, Type type)
        {
            if (value == null) return JValue.CreateNull();

            // Nullable values are boxed as their underlying type. Serializing
            // the declared Nullable<T> as an object used to emit {} and lose
            // model capabilities, token limits and cache timestamps on reload.
            type = Nullable.GetUnderlyingType(type) ?? type;

            if (IsSimple(type)) return WriteSimple(value, type);

            var dictionary = value as IDictionary;
            if (dictionary != null) return WriteDictionary(dictionary);

            var enumerable = value as IEnumerable;
            if (enumerable != null) return WriteArray(enumerable);

            return WriteObject(value, type);
        }

        private static JObject WriteDictionary(IDictionary dictionary)
        {
            var result = new JObject();
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key as string;
                if (key == null) key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(key)) continue;

                // 字典里值可能是 null（比如 Extensions 里用户塞了个空值）：
                // 这种情况仍然写出去，不然键会凭空消失
                result[key] = entry.Value == null
                    ? JValue.CreateNull()
                    : WriteValue(entry.Value, entry.Value.GetType());
            }
            return result;
        }

        private static JArray WriteArray(IEnumerable items)
        {
            var result = new JArray();
            foreach (var item in items)
            {
                result.Add(item == null ? JValue.CreateNull() : WriteValue(item, item.GetType()));
            }
            return result;
        }

        private static JObject WriteObject(object value, Type type)
        {
            var result = new JObject();
            var properties = PropertiesOf(type);

            foreach (var property in properties)
            {
                object propertyValue;
                try
                {
                    propertyValue = property.Info.GetValue(value);
                }
                catch (Exception)
                {
                    continue;
                }

                if (propertyValue == null) continue;      // NullValueHandling.Ignore

                result[property.Name] = WriteValue(propertyValue, property.PropertyType);
            }

            return result;
        }

        private static JToken WriteSimple(object value, Type type)
        {
            if (value is string) return new JValue((string)value);
            if (value is bool) return new JValue((bool)value);
            if (value is byte[]) return new JValue(Convert.ToBase64String((byte[])value));

            if (type.GetTypeInfo().IsEnum)
            {
                // 整数：与 Newtonsoft 的默认行为一致，已有文件里就是整数
                return new JValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            }

            if (value is DateTimeOffset)
            {
                var dto = (DateTimeOffset)value;
                return new JValue(dto.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture));
            }

            if (value is DateTime)
            {
                var dt = (DateTime)value;
                return new JValue(dt.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
            }

            if (value is TimeSpan)
            {
                return new JValue(((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture));
            }

            if (value is Guid)
            {
                return new JValue(((Guid)value).ToString("D"));
            }

            if (value is float) return new JValue((float)value);
            if (value is double) return new JValue((double)value);
            if (value is decimal) return new JValue((decimal)value);
            if (value is long) return new JValue((long)value);
            if (value is int) return new JValue((int)value);
            if (value is short) return new JValue((short)value);
            if (value is byte) return new JValue((byte)value);
            if (value is sbyte) return new JValue((sbyte)value);
            if (value is uint) return new JValue((uint)value);
            if (value is ulong) return new JValue((ulong)value);
            if (value is ushort) return new JValue((ushort)value);

            // 兜底：转成字符串总比抛异常好
            return new JValue(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        // ═════════════════════════════════════════════════════════════════
        // 节点树 → 对象
        // ═════════════════════════════════════════════════════════════════

        public static object FromToken(JToken token, Type type)
        {
            if (type == null) return null;

            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null)
            {
                if (IsNull(token)) return null;
                if (IsSimple(underlying) && !(token is JValue)) return null;
                return FromToken(token, underlying);
            }

            if (IsNull(token))
            {
                return type.GetTypeInfo().IsValueType ? Activator.CreateInstance(type) : null;
            }

            if (type == typeof(object)) return ReadArbitrary(token);

            if (type == typeof(string)) return token.Type == JTokenType.Null ? null : token.ToString();

            if (type == typeof(bool)) return ToBoolean(token);

            if (type.GetTypeInfo().IsEnum) return ToEnum(token, type);

            if (type == typeof(DateTimeOffset)) return ToDateTimeOffset(token);
            if (type == typeof(DateTime)) return ToDateTime(token);
            if (type == typeof(TimeSpan)) return ToTimeSpan(token);
            if (type == typeof(Guid)) return ToGuid(token);

            if (type == typeof(byte[]))
            {
                try { return Convert.FromBase64String(token.ToString()); }
                catch (Exception) { return new byte[0]; }
            }

            if (IsNumeric(type)) return ToNumeric(token, type);

            if (type.IsArray) return ReadArray(token, type.GetElementType());

            var dictionary = GenericDictionaryValueType(type);
            if (dictionary != null) return ReadDictionary(token, type, dictionary);

            var list = GenericListElementType(type);
            if (list != null) return ReadList(token, type, list);

            return ReadObject(token, type);
        }

        private static object ReadArbitrary(JToken token)
        {
            if (IsNull(token)) return null;

            switch (token.Type)
            {
                case JTokenType.String: return token.ToString();
                case JTokenType.Boolean: return (bool)token;
                case JTokenType.Integer: return (long)token;
                case JTokenType.Float: return (double)token;
                case JTokenType.Date: return (DateTime)token;
                case JTokenType.Array:
                {
                    var list = new List<object>();
                    foreach (var item in (JArray)token) list.Add(ReadArbitrary(item));
                    return list;
                }
                case JTokenType.Object:
                {
                    var map = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var pair in (JObject)token) map[pair.Key] = ReadArbitrary(pair.Value);
                    return map;
                }
                default:
                    return token.ToString();
            }
        }

        private static object ReadArray(JToken token, Type elementType)
        {
            var array = (JArray)token as JArray;
            if (array == null) return Array.CreateInstance(elementType, 0);

            var result = Array.CreateInstance(elementType, array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                result.SetValue(FromToken(array[i], elementType), i);
            }
            return result;
        }

        private static object ReadDictionary(JToken token, Type type, Type valueType)
        {
            var instance = Activator.CreateInstance(type);

            var add = type.GetRuntimeMethod("Add", new Type[] { typeof(string), valueType });
            if (add == null) return instance;

            var obj = token as JObject;
            if (obj == null) return instance;

            foreach (var pair in obj)
            {
                try { add.Invoke(instance, new object[] { pair.Key, FromToken(pair.Value, valueType) }); }
                catch (Exception) { }
            }

            return instance;
        }

        private static object ReadList(JToken token, Type type, Type elementType)
        {
            var instance = Activator.CreateInstance(type);
            var add = type.GetRuntimeMethod("Add", new Type[] { elementType });
            if (add == null) return instance;

            var array = token as JArray;
            if (array == null) return instance;

            foreach (var item in array)
            {
                try { add.Invoke(instance, new object[] { FromToken(item, elementType) }); }
                catch (Exception) { }
            }

            return instance;
        }

        private static object ReadObject(JToken token, Type type)
        {
            var obj = token as JObject;
            if (obj == null) return null;

            object instance;
            try
            {
                instance = Activator.CreateInstance(type);
            }
            catch (Exception)
            {
                return null;      // 没有无参构造的类型（比如接口）只能放弃
            }

            var properties = PropertiesOf(type);
            foreach (var property in properties)
            {
                if (!property.CanWrite) continue;

                JToken value;
                if (!obj.TryGetValue(property.Name, out value)) continue;   // 认不出的字段忽略

                // 显式 null 不覆盖：模型里的子对象与集合都在构造函数里给了默认值
                // （Persona、Memory、ToolOptions.Enabled…），拿 null 盖掉它们会让
                // 之后到处空引用。原引擎的 NullValueHandling.Ignore 是双向的，
                // 这里保持同样的语义。
                if (IsNull(value)) continue;

                try
                {
                    property.Info.SetValue(instance, FromToken(value, property.PropertyType));
                }
                catch (Exception)
                {
                    // 单个字段类型对不上不该让整个文件读不出来
                }
            }

            return instance;
        }

        // ═════════════════════════════════════════════════════════════════
        // 标量转换
        // ═════════════════════════════════════════════════════════════════

        private static bool ToBoolean(JToken token)
        {
            if (token.Type == JTokenType.Boolean) return (bool)token;

            var text = token.ToString();
            bool parsed;
            if (bool.TryParse(text, out parsed)) return parsed;

            double number;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                return Math.Abs(number) > double.Epsilon;
            }
            return false;
        }

        private static object ToEnum(JToken token, Type type)
        {
            if (token.Type == JTokenType.Integer)
            {
                try { return Enum.ToObject(type, ((long)token)); }
                catch (Exception) { }
            }

            try { return Enum.Parse(type, token.ToString(), true); }
            catch (Exception) { return Activator.CreateInstance(type); }
        }

        private static object ToDateTimeOffset(JToken token)
        {
            if (token.Type == JTokenType.Date)
            {
                var dt = (DateTime)token;
                return new DateTimeOffset(dt);
            }

            DateTimeOffset parsed;
            if (DateTimeOffset.TryParse(token.ToString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out parsed))
            {
                return parsed;
            }
            return default(DateTimeOffset);
        }

        private static object ToDateTime(JToken token)
        {
            if (token.Type == JTokenType.Date) return (DateTime)token;

            DateTime parsed;
            if (DateTime.TryParse(token.ToString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out parsed))
            {
                return parsed;
            }
            return default(DateTime);
        }

        private static object ToTimeSpan(JToken token)
        {
            TimeSpan parsed;
            if (TimeSpan.TryParse(token.ToString(), CultureInfo.InvariantCulture, out parsed)) return parsed;
            return default(TimeSpan);
        }

        private static object ToGuid(JToken token)
        {
            Guid parsed;
            if (Guid.TryParse(token.ToString(), out parsed)) return parsed;
            return Guid.Empty;
        }

        private static object ToNumeric(JToken token, Type type)
        {
            if (token.Type == JTokenType.Null) return Activator.CreateInstance(type);

            try
            {
                if (type == typeof(double)) return (double)token;
                if (type == typeof(float)) return (float)token;
                if (type == typeof(decimal)) return (decimal)token;
                if (type == typeof(long)) return (long)token;
                if (type == typeof(int)) return (int)token;
                if (type == typeof(short)) return (short)token;
                if (type == typeof(byte)) return (byte)token;
                if (type == typeof(sbyte)) return (sbyte)token;
                if (type == typeof(uint)) return (uint)token;
                if (type == typeof(ulong)) return (ulong)token;
                if (type == typeof(ushort)) return (ushort)token;
            }
            catch (Exception)
            {
                // 数字被写成字符串之类的情况，退回文本解析
            }

            try
            {
                return Convert.ChangeType(token.ToString(), type, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return Activator.CreateInstance(type);
            }
        }

        private static bool IsNull(JToken token)
        {
            return token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined;
        }

        // ═════════════════════════════════════════════════════════════════
        // 类型判断
        // ═════════════════════════════════════════════════════════════════

        private static bool IsSimple(Type type)
        {
            if (type == typeof(string)) return true;
            if (type == typeof(bool)) return true;
            if (type == typeof(byte[])) return true;
            if (type == typeof(DateTimeOffset)) return true;
            if (type == typeof(DateTime)) return true;
            if (type == typeof(TimeSpan)) return true;
            if (type == typeof(Guid)) return true;
            if (IsNumeric(type)) return true;

            var info = type.GetTypeInfo();
            return info.IsEnum;
        }

        private static bool IsNumeric(Type type)
        {
            return type == typeof(double) || type == typeof(float) || type == typeof(decimal)
                || type == typeof(long) || type == typeof(int) || type == typeof(short)
                || type == typeof(byte) || type == typeof(sbyte)
                || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort);
        }

        /// <summary>如果是 Dictionary&lt;string, T&gt; 就返回 T，否则返回 null。</summary>
        private static Type GenericDictionaryValueType(Type type)
        {
            var info = type.GetTypeInfo();
            if (!info.IsGenericType) return null;

            var definition = info.GetGenericTypeDefinition();
            if (definition != typeof(Dictionary<,>)) return null;

            var arguments = info.GenericTypeArguments;
            if (arguments.Length != 2) return null;
            if (arguments[0] != typeof(string)) return null;

            return arguments[1];
        }

        /// <summary>如果是 List&lt;T&gt; 就返回 T，否则返回 null。</summary>
        private static Type GenericListElementType(Type type)
        {
            var info = type.GetTypeInfo();
            if (!info.IsGenericType) return null;

            var definition = info.GetGenericTypeDefinition();
            if (definition != typeof(List<>)) return null;

            var arguments = info.GenericTypeArguments;
            return arguments.Length == 1 ? arguments[0] : null;
        }

        // ═════════════════════════════════════════════════════════════════
        // 属性表
        // ═════════════════════════════════════════════════════════════════

        private sealed class PropertyMap
        {
            public string Name;
            public Type PropertyType;
            public bool CanWrite;
            public PropertyInfo Info;
        }

        private static PropertyMap[] PropertiesOf(Type type)
        {
            lock (_cacheGate)
            {
                PropertyMap[] cached;
                if (_cache.TryGetValue(type, out cached)) return cached;
            }

            var list = new List<PropertyMap>();

            foreach (var property in type.GetRuntimeProperties())
            {
                var info = property.GetMethod;
                if (info == null || !info.IsPublic || info.IsStatic) continue;
                if (property.GetIndexParameters().Length > 0) continue;

                if (HasJsonIgnore(property)) continue;

                // 只认带 [JsonProperty] 的，或者能写回的。
                // 这样自动排除掉计算出来的只读属性（Total / HasToolCalls 之类），
                // 不用依赖每个类型都记得标 JsonIgnore。
                var declared = JsonPropertyName(property);
                if (declared == null && property.SetMethod == null) continue;

                var map = new PropertyMap
                {
                    Name = declared ?? property.Name,
                    PropertyType = property.PropertyType,
                    CanWrite = property.SetMethod != null && property.SetMethod.IsPublic,
                    Info = property
                };
                list.Add(map);
            }

            var array = list.ToArray();

            lock (_cacheGate)
            {
                _cache[type] = array;
            }

            return array;
        }

        private static bool HasJsonIgnore(PropertyInfo property)
        {
            try
            {
                foreach (var attribute in property.GetCustomAttributes(typeof(JsonIgnoreAttribute), true))
                {
                    if (attribute is JsonIgnoreAttribute) return true;
                }
            }
            catch (Exception) { }

            return false;
        }

        /// <summary>
        /// 取 [JsonProperty("x")] 里的名字。用非泛型的 GetCustomAttributes ——
        /// UWP 的反射扩展方法里非泛型重载更稳，泛型版在 .NET Native 上容易缺元数据。
        /// </summary>
        private static string JsonPropertyName(PropertyInfo property)
        {
            try
            {
                foreach (var attribute in property.GetCustomAttributes(typeof(JsonPropertyAttribute), true))
                {
                    var jsonProperty = attribute as JsonPropertyAttribute;
                    if (jsonProperty != null) return jsonProperty.PropertyName;
                }
            }
            catch (Exception) { }

            return null;
        }
    }
}
