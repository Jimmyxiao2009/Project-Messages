// Core/ToolArguments.cs —— 工具入参解析
//
// 模型给的 arguments 是一个 JSON 字符串，而且并不总是合法 JSON
// （DeepSeek 与一些小模型偶尔会截断或漏引号）。这里统一兜底：
// 解析失败不抛异常，返回空集合，由工具自己校验参数并给出可读错误。

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Yanshuai.Messages.Core
{
    public static class ToolArguments
    {
        private static JObject TryParse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var token = JToken.Parse(json);
                return token as JObject;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>顶层字段的有序键值对，值统一转成短字符串显示。</summary>
        public static List<KeyValuePair<string, string>> Parse(string json)
        {
            var result = new List<KeyValuePair<string, string>>();
            var obj = TryParse(json);
            if (obj == null) return result;

            foreach (var prop in obj.Properties())
            {
                result.Add(new KeyValuePair<string, string>(prop.Name, ShortValue(prop.Value)));
            }
            return result;
        }

        private static string ShortValue(JToken token)
        {
            if (token == null) return "";
            switch (token.Type)
            {
                case JTokenType.String:
                    {
                        var s = token.Value<string>() ?? "";
                        return TextUtil.Truncate(TextUtil.Normalize(s), 60);
                    }
                case JTokenType.Integer:
                case JTokenType.Float:
                    return token.ToString();
                case JTokenType.Boolean:
                    // 归一成小写，避免 UI 上出现 "True" 这种 .NET 风格
                    return token.Value<bool>() ? "true" : "false";
                case JTokenType.Null:
                    return "null";
                case JTokenType.Array:
                    {
                        var arr = token as JArray;
                        return arr == null ? "[]" : "[" + arr.Count.ToString(CultureInfo.InvariantCulture) + " 项]";
                    }
                case JTokenType.Object:
                    {
                        var o = token as JObject;
                        return o == null ? "{}" : "{" + o.Count.ToString(CultureInfo.InvariantCulture) + " 字段}";
                    }
                default:
                    return TextUtil.Truncate(token.ToString(), 60);
            }
        }

        public static bool Has(string json, string key)
        {
            var obj = TryParse(json);
            return obj != null && obj.Property(key) != null;
        }

        public static string GetString(string json, string key, string fallback)
        {
            var obj = TryParse(json);
            if (obj == null) return fallback;
            var prop = obj.Property(key);
            if (prop == null || prop.Value == null || prop.Value.Type == JTokenType.Null) return fallback;
            if (prop.Value.Type == JTokenType.String) return prop.Value.Value<string>();
            return prop.Value.ToString();
        }

        public static int GetInt(string json, string key, int fallback)
        {
            var obj = TryParse(json);
            if (obj == null) return fallback;
            var prop = obj.Property(key);
            if (prop == null || prop.Value == null) return fallback;

            if (prop.Value.Type == JTokenType.Integer || prop.Value.Type == JTokenType.Float)
            {
                double d;
                if (double.TryParse(prop.Value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                {
                    return (int)Math.Round(d);
                }
            }
            if (prop.Value.Type == JTokenType.String)
            {
                int i;
                if (int.TryParse(prop.Value.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return i;
            }
            return fallback;
        }

        public static bool GetBool(string json, string key, bool fallback)
        {
            var obj = TryParse(json);
            if (obj == null) return fallback;
            var prop = obj.Property(key);
            if (prop == null || prop.Value == null) return fallback;

            if (prop.Value.Type == JTokenType.Boolean) return prop.Value.Value<bool>();
            if (prop.Value.Type == JTokenType.Integer) return prop.Value.Value<int>() != 0;
            if (prop.Value.Type == JTokenType.String)
            {
                var s = (prop.Value.Value<string>() ?? "").Trim().ToLowerInvariant();
                if (s == "true" || s == "1" || s == "yes") return true;
                if (s == "false" || s == "0" || s == "no") return false;
            }
            return fallback;
        }

        public static List<string> GetStringArray(string json, string key)
        {
            var result = new List<string>();
            var obj = TryParse(json);
            if (obj == null) return result;
            var prop = obj.Property(key);
            if (prop == null || prop.Value == null) return result;

            var arr = prop.Value as JArray;
            if (arr != null)
            {
                foreach (var item in arr)
                {
                    if (item == null || item.Type == JTokenType.Null) continue;
                    result.Add(item.Type == JTokenType.String ? item.Value<string>() : item.ToString());
                }
                return result;
            }

            // 也接受逗号分隔的字符串，模型有时会这么给
            if (prop.Value.Type == JTokenType.String)
            {
                var raw = prop.Value.Value<string>();
                if (!string.IsNullOrEmpty(raw))
                {
                    foreach (var part in raw.Split(','))
                    {
                        var t = part.Trim();
                        if (t.Length > 0) result.Add(t);
                    }
                }
            }
            return result;
        }

        public static Dictionary<string, string> GetStringMap(string json, string key)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var obj = TryParse(json);
            if (obj == null) return result;
            var prop = obj.Property(key);
            if (prop == null || prop.Value == null) return result;

            var sub = prop.Value as JObject;
            if (sub == null) return result;
            foreach (var p in sub.Properties())
            {
                result[p.Name] = p.Value == null || p.Value.Type == JTokenType.Null
                    ? string.Empty
                    : (p.Value.Type == JTokenType.String ? p.Value.Value<string>() : p.Value.ToString());
            }
            return result;
        }

        /// <summary>校验 JSON 是否合法（Claude 的 tool_use 参数需要这一步，否则会 400）。</summary>
        public static bool IsValidJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                JToken.Parse(json);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 修补被截断的 JSON：补足缺失的右括号/右引号。
        /// 小模型输出长参数时会被 max_tokens 截断，这是常见故障。
        /// </summary>
        public static string RepairTruncated(string json)
        {
            if (string.IsNullOrEmpty(json)) return "{}";
            if (IsValidJson(json)) return json;

            int depth = 0;
            bool inString = false;
            bool escape = false;
            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];
                if (escape) { escape = false; continue; }
                if (ch == '\\') { escape = true; continue; }
                if (ch == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (ch == '{' || ch == '[') depth++;
                else if (ch == '}' || ch == ']') depth--;
            }

            var sb = new System.Text.StringBuilder(json);
            if (inString) sb.Append('"');
            // 去掉尾部悬挂的逗号
            while (sb.Length > 0)
            {
                char last = sb[sb.Length - 1];
                if (last == ',' || char.IsWhiteSpace(last)) sb.Length--;
                else break;
            }
            for (int i = 0; i < depth; i++) sb.Append('}');
            var candidate = sb.ToString();
            return IsValidJson(candidate) ? candidate : "{}";
        }
    }
}
