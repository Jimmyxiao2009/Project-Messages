// Core/Prompting/Placeholders.cs —— 占位符解析
//
// 支持 {{name}} 与 {name} 两种写法（两种都见得多）。未识别的占位符**原样保留**，
// 不静默替换成空串 —— 用户写错了应该看得见，而不是莫名其妙少一段。
//
// 环境信息（设备/时区/电量）由调用方填进 PromptContext，Core 不直接问系统，
// 这样测试里可以给出固定值。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Yanshuai.Messages.Core
{
    public static class Placeholders
    {
        public const string User = "user";
        public const string Char = "char";
        public const string CurDate = "cur_date";
        public const string CurTime = "cur_time";
        public const string CurDateTime = "cur_datetime";
        public const string ModelName = "model_name";
        public const string ModelId = "model_id";
        public const string Locale = "locale";
        public const string TimeZone = "timezone";
        public const string DeviceInfo = "device_info";
        public const string BatteryLevel = "battery_level";
        public const string Nickname = "nickname";

        public static readonly string[] All = new string[]
        {
            User, Char, CurDate, CurTime, CurDateTime,
            ModelName, ModelId, Locale, TimeZone, DeviceInfo, BatteryLevel, Nickname
        };

        /// <summary>把文本里的占位符替换成实际值。未知占位符保持原样。</summary>
        public static string Resolve(string text, IDictionary<string, string> values)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0) return text ?? string.Empty;

            var sb = new StringBuilder(text.Length + 32);
            int i = 0;

            while (i < text.Length)
            {
                char ch = text[i];
                if (ch != '{')
                {
                    sb.Append(ch);
                    i++;
                    continue;
                }

                bool doubled = (i + 1 < text.Length && text[i + 1] == '{');
                int openLen = doubled ? 2 : 1;
                int closeIdx = text.IndexOf(doubled ? "}}" : "}", i + openLen, StringComparison.Ordinal);

                if (closeIdx < 0)
                {
                    sb.Append(ch);
                    i++;
                    continue;
                }

                var name = text.Substring(i + openLen, closeIdx - (i + openLen)).Trim();
                string value;
                if (name.Length > 0 && IsPlainName(name) && values != null && values.TryGetValue(name, out value))
                {
                    sb.Append(value ?? string.Empty);
                    i = closeIdx + (doubled ? 2 : 1);
                    continue;
                }

                // 不认识的占位符：原样吐出去，让用户看得见
                sb.Append(text, i, closeIdx - i + (doubled ? 2 : 1));
                i = closeIdx + (doubled ? 2 : 1);
            }

            return sb.ToString();
        }

        private static bool IsPlainName(string name)
        {
            foreach (var ch in name)
            {
                if (!char.IsLetterOrDigit(ch) && ch != '_') return false;
            }
            return true;
        }

        /// <summary>收集一条档案可用的占位符取值。</summary>
        public static Dictionary<string, string> BuildValues(
            string charName,
            string userName,
            string modelId,
            string modelName,
            DateTimeOffset nowLocal,
            string locale,
            string timeZone,
            string deviceInfo,
            string batteryLevel)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            values[User] = userName ?? string.Empty;
            values[Nickname] = userName ?? string.Empty;
            values[Char] = charName ?? string.Empty;
            values[ModelId] = modelId ?? string.Empty;
            values[ModelName] = string.IsNullOrEmpty(modelName) ? (modelId ?? string.Empty) : modelName;

            values[CurDate] = nowLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            values[CurTime] = nowLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
            values[CurDateTime] = nowLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            values[Locale] = string.IsNullOrEmpty(locale) ? "zh-CN" : locale;
            values[TimeZone] = timeZone ?? string.Empty;
            values[DeviceInfo] = deviceInfo ?? string.Empty;
            values[BatteryLevel] = batteryLevel ?? string.Empty;

            return values;
        }
    }
}
