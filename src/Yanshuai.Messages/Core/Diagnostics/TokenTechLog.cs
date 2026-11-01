// Core/Diagnostics/TokenTechLog.cs —— 会话级 token 技术日志
//
// 用户要的是"这次对话到底花了多少、慢在哪、为什么贵"。
// 已有的 DiagnosticLog 是排障用的全局流水（会脱敏、会滚动），
// 不适合回答这个问题：它没有按会话聚合，也不保证能算出总量。
//
// 所以单独一份，按会话存成 JSONL（一行一条）：
//   · 追加便宜 —— 不用读回整个文件再重写
//   · 单行坏掉不影响其它行 —— 断电/写一半时最多丢最后一条
//   · 直接人眼可读，导出给用户也方便
//
// 里面**不记请求正文**，只记元数据（模型、用量、耗时、轮次、错误码）。
// 密钥与提示词内容一概不进这个文件。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Yanshuai.Messages.Core
{
    public sealed class TechLogEntry
    {
        [JsonProperty("at")] public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
        /// <summary>第几轮用户输入（从 1 开始）。</summary>
        [JsonProperty("turn")] public int Turn { get; set; }

        [JsonProperty("model")] public string Model { get; set; } = "";
        [JsonProperty("provider")] public string Provider { get; set; } = "";
        /// <summary>只记主机名，不记完整 URL（完整 URL 可能带 token 参数）。</summary>
        [JsonProperty("host")] public string Host { get; set; } = "";

        [JsonProperty("input")] public int InputTokens { get; set; }
        [JsonProperty("output")] public int OutputTokens { get; set; }
        [JsonProperty("cached")] public int CachedTokens { get; set; }
        /// <summary>用量是本地估算的（流式且服务端没回 usage）。</summary>
        [JsonProperty("estimated")] public bool Estimated { get; set; }

        [JsonProperty("durationMs")] public long DurationMs { get; set; }
        /// <summary>首字延迟。流式才有，非流式为 0。</summary>
        [JsonProperty("firstTokenMs")] public long FirstTokenMs { get; set; }
        /// <summary>这一轮里模型调用了几次（工具循环每多一轮就多一次）。</summary>
        [JsonProperty("modelCalls")] public int ModelCalls { get; set; }
        [JsonProperty("toolCalls")] public int ToolCalls { get; set; }

        /// <summary>这一轮是否做过上下文压缩。</summary>
        [JsonProperty("compressed")] public bool Compressed { get; set; }
        /// <summary>压缩省下的估算 token。</summary>
        [JsonProperty("savedTokens")] public int SavedTokens { get; set; }

        /// <summary>错误码（成功为空）。</summary>
        [JsonProperty("error")] public string Error { get; set; } = "";

        /// <summary>是否流式。</summary>
        [JsonProperty("stream")] public bool Stream { get; set; }
    }

    public sealed class TechLogSummary
    {
        public int Turns { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CachedTokens { get; set; }
        public int TotalTokens { get { return InputTokens + OutputTokens; } }
        public long TotalDurationMs { get; set; }
        public int ModelCalls { get; set; }
        public int ToolCalls { get; set; }
        public int EstimatedTurns { get; set; }
        public int FailedTurns { get; set; }
        public int SavedTokens { get; set; }

        /// <summary>平均每轮 token，用来判断上下文是不是在悄悄膨胀。</summary>
        public int AverageTokensPerTurn
        {
            get { return Turns > 0 ? TotalTokens / Turns : 0; }
        }

        /// <summary>缓存命中占输入的比例（0–1）。越低说明前缀复用越差。</summary>
        public double CacheHitRate
        {
            get { return InputTokens > 0 ? (double)CachedTokens / InputTokens : 0; }
        }

        /// <summary>总耗时（秒，带一位小数）。</summary>
        public double TotalSeconds
        {
            get { return Math.Round(TotalDurationMs / 1000.0, 1); }
        }
    }

    public sealed class TokenTechLog
    {
        private readonly IFileStore _store;
        private readonly ILogger _log;

        public TokenTechLog(IFileStore store, ILogger log)
        {
            _store = store;
            _log = log ?? NullLogger.Instance;
        }

        public static string PathFor(string profileSlug, string conversationId)
        {
            return profileSlug + "/conversations/tech/" + conversationId + ".jsonl";
        }

        /// <summary>追加一条。失败只记日志，不影响正在进行的对话。</summary>
        public async Task AppendAsync(string profileSlug, string conversationId, TechLogEntry entry, CancellationToken ct)
        {
            if (entry == null || string.IsNullOrEmpty(conversationId)) return;

            try
            {
                var path = PathFor(profileSlug, conversationId);

                var existing = "";
                if (await _store.ExistsAsync(path).ConfigureAwait(false))
                {
                    existing = await _store.ReadTextAsync(path).ConfigureAwait(false);
                }

                var line = JsonStore.SerializeCompact(entry);
                var text = string.IsNullOrEmpty(existing) ? line + "\n" : existing + line + "\n";

                await _store.WriteTextAsync(path, text).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Warning, "techlog", "写入失败：" + ex.GetType().Name);
            }
        }

        /// <summary>读最近 N 条（按写入顺序，最后一条最新）。</summary>
        public async Task<List<TechLogEntry>> ReadAsync(string profileSlug, string conversationId, int max, CancellationToken ct)
        {
            var result = new List<TechLogEntry>();
            if (string.IsNullOrEmpty(conversationId)) return result;

            var path = PathFor(profileSlug, conversationId);
            string text;
            try
            {
                if (!await _store.ExistsAsync(path).ConfigureAwait(false)) return result;
                text = await _store.ReadTextAsync(path).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Warning, "techlog", "读取失败：" + ex.GetType().Name);
                return result;
            }

            if (string.IsNullOrEmpty(text)) return result;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                try
                {
                    var entry = JsonBridge.Deserialize<TechLogEntry>(line);
                    if (entry != null) result.Add(entry);
                }
                catch (Exception)
                {
                    // 单行坏掉就跳过 —— 这正是选 JSONL 的原因
                }
            }

            if (max > 0 && result.Count > max)
            {
                result.RemoveRange(0, result.Count - max);
            }

            return result;
        }

        public async Task<TechLogSummary> SummarizeAsync(string profileSlug, string conversationId, CancellationToken ct)
        {
            var entries = await ReadAsync(profileSlug, conversationId, 0, ct).ConfigureAwait(false);
            return Summarize(entries);
        }

        public static TechLogSummary Summarize(List<TechLogEntry> entries)
        {
            var summary = new TechLogSummary();
            if (entries == null) return summary;

            foreach (var entry in entries)
            {
                if (entry == null) continue;
                summary.Turns++;
                summary.InputTokens += entry.InputTokens;
                summary.OutputTokens += entry.OutputTokens;
                summary.CachedTokens += entry.CachedTokens;
                summary.TotalDurationMs += entry.DurationMs;
                summary.ModelCalls += entry.ModelCalls;
                summary.ToolCalls += entry.ToolCalls;
                summary.SavedTokens += entry.SavedTokens;
                if (entry.Estimated) summary.EstimatedTurns++;
                if (!string.IsNullOrEmpty(entry.Error)) summary.FailedTurns++;
            }

            return summary;
        }

        public async Task ClearAsync(string profileSlug, string conversationId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(conversationId)) return;
            try
            {
                await _store.DeleteAsync(PathFor(profileSlug, conversationId)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Warning, "techlog", "清除失败：" + ex.GetType().Name);
            }
        }

        /// <summary>把摘要渲染成可复制的一段文本（用户反馈问题时贴出来用）。</summary>
        public static string Format(TechLogSummary summary, List<TechLogEntry> entries)
        {
            var sb = new StringBuilder();
            sb.Append("【会话用量】\n");
            sb.Append("轮次：").Append(summary.Turns).Append('\n');
            sb.Append("输入：").Append(summary.InputTokens).Append(" token");
            if (summary.CachedTokens > 0)
            {
                sb.Append("（其中缓存 ").Append(summary.CachedTokens)
                  .Append("，命中 ").Append((summary.CacheHitRate * 100).ToString("0", CultureInfo.InvariantCulture)).Append("%）");
            }
            sb.Append('\n');
            sb.Append("输出：").Append(summary.OutputTokens).Append(" token\n");
            sb.Append("合计：").Append(summary.TotalTokens).Append(" token\n");
            sb.Append("平均每轮：").Append(summary.AverageTokensPerTurn).Append(" token\n");
            sb.Append("模型调用：").Append(summary.ModelCalls).Append(" 次，工具调用 ")
              .Append(summary.ToolCalls).Append(" 次\n");
            sb.Append("总耗时：").Append(summary.TotalSeconds).Append(" 秒\n");

            if (summary.EstimatedTurns > 0)
            {
                sb.Append("注：有 ").Append(summary.EstimatedTurns).Append(" 轮的用量是本地估算的（服务端未返回）\n");
            }
            if (summary.SavedTokens > 0)
            {
                sb.Append("压缩省下：约 ").Append(summary.SavedTokens).Append(" token\n");
            }
            if (summary.FailedTurns > 0)
            {
                sb.Append("失败轮次：").Append(summary.FailedTurns).Append('\n');
            }

            if (entries != null && entries.Count > 0)
            {
                sb.Append("\n【逐轮明细】\n");
                foreach (var entry in entries)
                {
                    sb.Append('#').Append(entry.Turn).Append(' ');
                    sb.Append(entry.Model).Append("  ");
                    sb.Append(entry.InputTokens).Append('+').Append(entry.OutputTokens).Append(" token  ");
                    sb.Append((entry.DurationMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)).Append("s");
                    if (entry.Estimated) sb.Append("  (估算)");
                    if (!string.IsNullOrEmpty(entry.Error)) sb.Append("  [").Append(entry.Error).Append(']');
                    sb.Append('\n');
                }
            }

            return sb.ToString().TrimEnd();
        }
    }
}
