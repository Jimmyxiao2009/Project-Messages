// Core/ContextCompressor.cs —— 上下文压缩
//
// 触发条件：估算 token 超过模型窗口的 80%，且可压缩的消息不少于 6 条。
// 保留最近 4 条不动，更早的交给模型总结成 800–1500 字的结构化摘要。
//
// 与旧项目的关键差异：摘要是**替换式**写入（Summary = newSummary），
// 不是把新摘要追加到旧摘要后面。旧做法会让摘要越滚越长，最后比原文还占地方。
// 另外 SummarizedUpTo 只前进不后退，避免反复压缩同一段。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public sealed class CompactResult
    {
        public bool Compacted;
        public string Reason = "";
        public string Summary = "";
        public int CompressedCount;
        public int SummarizedUpTo;
        public int TokensBefore;
        public int TokensAfter;
    }

    public sealed class ContextCompressor
    {
        private readonly ILogger _log;

        public ContextCompressor(ILogger log)
        {
            _log = log;
        }

        /// <summary>估算整轮请求的用量（系统提示 + 历史 + 摘要）。</summary>
        public static int EstimateConversationTokens(Conversation conversation, string systemPrompt, int startIndex)
        {
            int total = TokenEstimator.Estimate(systemPrompt);

            if (!string.IsNullOrEmpty(conversation.Summary)) total += TokenEstimator.Estimate(conversation.Summary);

            if (conversation.Messages != null)
            {
                if (startIndex < 0) startIndex = 0;
                for (int i = startIndex; i < conversation.Messages.Count; i++)
                {
                    var message = conversation.Messages[i];
                    total += TokenEstimator.EstimateMessage(message.Content, message.Reasoning);
                    if (message.Attachments != null)
                    {
                        // 图片按固定开销估，不真的去读文件
                        foreach (var attachment in message.Attachments)
                        {
                            if (attachment.IsImage) total += 800;
                        }
                    }
                }
            }

            return total;
        }

        /// <summary>判断是否需要压缩。</summary>
        public static bool ShouldCompact(Conversation conversation, string systemPrompt, int contextLimit)
        {
            if (conversation == null) return false;
            if (contextLimit <= 0) contextLimit = ModelContextLimits.DefaultLimit;

            int start = conversation.SummarizedUpTo + 1;
            if (start < 0) start = 0;

            int compressible = conversation.Messages.Count - start;
            if (compressible < Limits.CompactMinMessages) return false;

            int estimated = EstimateConversationTokens(conversation, systemPrompt, start);
            return estimated > contextLimit * Limits.CompactThreshold;
        }

        /// <summary>
        /// 执行压缩。completeAsync 由调用方提供（用副模型跑，省额度）。
        /// 失败时不改动会话，只返回原因。
        /// </summary>
        public async Task<CompactResult> CompactAsync(
            Conversation conversation,
            string systemPrompt,
            int contextLimit,
            Func<string, CancellationToken, Task<string>> completeAsync,
            CancellationToken ct)
        {
            var result = new CompactResult();
            if (conversation == null) { result.Reason = "没有会话"; return result; }
            if (completeAsync == null) { result.Reason = "没有可用的总结模型"; return result; }

            if (contextLimit <= 0) contextLimit = ModelContextLimits.DefaultLimit;

            int start = conversation.SummarizedUpTo + 1;
            if (start < 0) start = 0;

            int total = conversation.Messages.Count;
            int compressible = total - start - Limits.CompactKeepRecent;

            if (compressible < 2)
            {
                result.Reason = "可压缩的消息太少";
                return result;
            }

            result.TokensBefore = EstimateConversationTokens(conversation, systemPrompt, start);

            // 取要压缩的那一段
            var slice = new List<Message>();
            for (int i = start; i < start + compressible; i++) slice.Add(conversation.Messages[i]);

            var prompt = BuildSummaryPrompt(conversation.Summary, slice);
            var summary = await completeAsync(prompt, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(summary))
            {
                result.Reason = "总结模型返回了空内容";
                return result;
            }

            conversation.Summary = summary.Trim();
            conversation.SummarizedUpTo = start + compressible - 1;
            conversation.LastCompactAtUtc = DateTimeOffset.UtcNow;

            result.Compacted = true;
            result.Summary = conversation.Summary;
            result.CompressedCount = compressible;
            result.SummarizedUpTo = conversation.SummarizedUpTo;
            result.TokensAfter = EstimateConversationTokens(conversation, systemPrompt, conversation.SummarizedUpTo + 1);

            if (_log != null)
            {
                _log.Log(LogLevel.Info, "compact",
                    "压缩 " + compressible.ToString(CultureInfo.InvariantCulture) + " 条，"
                    + result.TokensBefore.ToString(CultureInfo.InvariantCulture) + " → "
                    + result.TokensAfter.ToString(CultureInfo.InvariantCulture));
            }
            return result;
        }

        /// <summary>构造总结用的提示词。结构化输出，便于后续注入时阅读。</summary>
        public static string BuildSummaryPrompt(string previousSummary, IReadOnlyList<Message> slice)
        {
            var sb = new StringBuilder();
            sb.Append("把下面这段对话压缩成一段结构化摘要，供后续对话继续使用。\n\n");
            sb.Append("要求：\n");
            sb.Append("• 800 到 1500 字。\n");
            sb.Append("• 分四段：【主题】在聊什么；【已完成】已经确定或做完的事；【关键信息】具体事实、数字、路径、名称；【待办】还没解决的。\n");
            sb.Append("• 只保留对后续对话有用的信息，不要保留寒暄与重复内容。\n");
            sb.Append("• 不要写成「用户说…助手说…」的流水账。\n\n");

            if (!string.IsNullOrEmpty(previousSummary))
            {
                sb.Append("── 之前已有的摘要（请合并进去，不要丢弃）──\n");
                sb.Append(previousSummary).Append("\n\n");
            }

            sb.Append("── 需要压缩的对话 ──\n");
            foreach (var message in slice)
            {
                if (message == null) continue;
                var role = message.IsUser ? "用户" : (message.Role == MessageRole.Tool ? "工具结果" : "助手");

                var body = message.Content ?? "";
                // 单条太长先截断，避免总结请求本身就超限
                if (body.Length > 2000) body = body.Substring(0, 2000) + "…（已截断）";

                sb.Append(role).Append("：").Append(body).Append('\n');
            }

            return sb.ToString();
        }
    }
}
