// Core/Storage/JsonStore.cs —— 统一的 JSON 序列化与容错读取
//
// 三条原则：
//   1) 解析失败不能把应用带崩。旧项目一个损坏的 JSON 会让整个 app 起不来。
//      这里统一走 TryDeserialize，坏文件返回 null 并记一条日志，由调用方决定
//      是重建默认值还是进回收区。
//   2) 属性名显式标注（见 Models），不依赖 C# 属性名 —— 改名不会悄悄破坏老数据。
//   3) **不直接用 Newtonsoft 的 JsonConvert**。它在 UWP 5.4.7 上一构造就抛
//      FieldAccessException，整套持久化会全废。实际映射交给 JsonBridge
//      （用 Newtonsoft 的分词器 + 我们自己的对象映射）。详见 JsonBridge 的注释。

using System;

namespace Yanshuai.Messages.Core
{
    public static class JsonStore
    {
        public static string Serialize(object value)
        {
            return JsonBridge.Serialize(value, true);
        }

        /// <summary>紧凑输出（每行一条的日志用，省体积也方便按行读）。</summary>
        public static string SerializeCompact(object value)
        {
            return JsonBridge.Serialize(value, false);
        }

        /// <summary>解析失败返回 null，不抛。what 只用于日志，不要放敏感内容。</summary>
        public static T TryDeserialize<T>(string json, ILogger log, string what) where T : class
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return JsonBridge.Deserialize<T>(json);
            }
            catch (Exception ex)
            {
                // 只记"解析失败"在真机上完全没法排障 —— 到底是编码坏了、字段类型不对、
                // 还是 .NET Native 缺 rd.xml 条目，得看异常本身。
                // 只取异常类型与首行消息，且过一遍截断，避免把文件内容带进日志。
                if (log != null) log.Log(LogLevel.Error, "json", "解析失败: " + what + " → " + Describe(ex));
                return null;
            }
        }

        private static string Describe(Exception ex)
        {
            if (ex == null) return "未知";

            var message = ex.Message ?? "";
            int newline = message.IndexOf('\n');
            if (newline > 0) message = message.Substring(0, newline);

            return Sanitize(ex.GetType().Name + ": " + message);
        }

        /// <summary>截断并去掉可能的换行，保证一行日志。</summary>
        private static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var trimmed = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return trimmed.Length > 200 ? trimmed.Substring(0, 200) + "…" : trimmed;
        }

        /// <summary>读文件并解析；文件不存在或解析失败都返回 null。</summary>
        public static async System.Threading.Tasks.Task<T> TryReadAsync<T>(
            IFileStore store, string relativePath, ILogger log)
            where T : class
        {
            string raw = null;
            try
            {
                if (!await store.ExistsAsync(relativePath)) return null;
                raw = await store.ReadTextAsync(relativePath);
            }
            catch (Exception)
            {
                if (log != null) log.Log(LogLevel.Error, "json", "读取失败: " + relativePath);
                return null;
            }
            return TryDeserialize<T>(raw, log, relativePath);
        }
    }
}
