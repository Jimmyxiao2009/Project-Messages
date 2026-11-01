// Core/Prompting/PromptAssembler.cs —— 提示词装配管线
//
// 系统提示词不再是"一坨字符串"，而是按注入位置分段拼装。顺序固定在这一处，
// 别的代码不许再自己往 system 里塞东西。
//
// 装配顺序：
//   1. 模式注入 · 系统提示前（优先级降序）
//   2. 人设（systemPrompt；为空则按模板用描述/性格/场景/示例拼）
//   3. 用户人设
//   4. 知识库文件（逐文件截断 + 总量上限）
//   5. 世界书命中条目（按 Priority 排序，过滤 Enabled）
//   6. 长期记忆块
//   7. 最近会话摘要（可选）
//   8. 上下文压缩摘要
//   9. 工具与行为规则（开了工具才加）
//  10. 时间与环境提醒（可选）
//  11. 模式注入 · 系统提示后
//  ---- 以下是消息序列内的插入 ----
//  12. 对话顶注入
//  13. 预设消息
//  14. 对话底注入（插在最后一条用户消息之前）
//  15. 指定深度注入
//
// 唯一硬约束：**绝不在"带 tool_calls 的助手消息"和它的 tool 结果之间插任何东西**。
// DeepSeek 等 provider 要求这两条相邻，插进去推理链会断。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Yanshuai.Messages.Core
{
    public sealed class KnowledgeText
    {
        public string Name { get; set; } = "";
        public string Content { get; set; } = "";
    }

    public sealed class PromptContext
    {
        public List<RegexTransform> RegexRules { get; set; }
        public Profile Profile { get; set; }
        public ApiProfile Api { get; set; }
        public UserPersona User { get; set; }
        public Conversation Conversation { get; set; }
        public PromptLibrary Library { get; set; }

        public List<KnowledgeText> Knowledge { get; set; }
        public string MemoryBlock { get; set; } = "";
        public string RecentChatsBlock { get; set; } = "";

        public bool ToolsEnabled { get; set; }
        public List<string> ToolNames { get; set; }

        public DateTimeOffset NowLocal { get; set; } = DateTimeOffset.Now;
        public string Locale { get; set; } = "zh-CN";
        public string TimeZone { get; set; } = "";
        public string DeviceInfo { get; set; } = "";
        public string BatteryLevel { get; set; } = "";

        public PromptContext()
        {
            Knowledge = new List<KnowledgeText>();
            ToolNames = new List<string>();
        }
    }

    public sealed class AssembledPrompt
    {
        /// <summary>拼好的系统提示词（含所有注入段）。</summary>
        public string SystemPrompt { get; set; } = "";
        /// <summary>交给 provider 的完整消息序列（首条是 system）。</summary>
        public List<ChatTurn> Turns { get; set; }

        public AssembledPrompt()
        {
            Turns = new List<ChatTurn>();
        }
    }

    public static class PromptAssembler
    {
        public const int KnowledgePerFileChars = 1000;
        public const int KnowledgeTotalChars = 3000;

        public static AssembledPrompt Build(PromptContext ctx)
        {
            var assembled = new AssembledPrompt();
            if (ctx == null || ctx.Profile == null) return assembled;

            var profile = ctx.Profile;
            profile.Repair();

            var values = Placeholders.BuildValues(
                profile.Persona.Name,
                ctx.User != null ? ctx.User.Name : "",
                ctx.Api != null ? ctx.Api.Model : "",
                ctx.Api != null ? ctx.Api.Model : "",
                ctx.NowLocal,
                ctx.Locale,
                ctx.TimeZone,
                ctx.DeviceInfo,
                ctx.BatteryLevel);

            // 收集所有启用中的注入与命中的世界书条目
            var injections = CollectInjections(ctx, values);
            var history = BuildHistoryTurns(ctx, values);

            var system = new StringBuilder();

            AppendInjections(system, injections, InjectionPosition.BeforeSystem);
            AppendPersona(system, profile, values);
            AppendUserPersona(system, ctx.User, values);
            AppendKnowledge(system, ctx.Knowledge);
            AppendLorebookHits(system, injections, values);
            AppendMemory(system, ctx);
            AppendSummary(system, profile, ctx);
            AppendToolRules(system, ctx);
            AppendTimeReminder(system, profile, ctx, values);
            AppendInjections(system, injections, InjectionPosition.AfterSystem);

            assembled.SystemPrompt = system.ToString().Trim();
            assembled.Turns.Add(ChatTurn.System(assembled.SystemPrompt));

            // ── 消息序列 ─────────────────────────────────────────────────
            AppendInjectionsAsTurns(assembled.Turns, injections, InjectionPosition.TopOfChat, values);

            foreach (var preset in profile.Generation.PresetMessages)
            {
                if (string.IsNullOrEmpty(preset.Content)) continue;
                var text = Placeholders.Resolve(preset.Content, values);
                assembled.Turns.Add(string.Equals(preset.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? ChatTurn.Assistant(text)
                    : ChatTurn.User(text));
            }

            var depthInjections = FilterInjections(injections, InjectionPosition.AtDepth);
            var bottomInjections = FilterInjections(injections, InjectionPosition.BottomOfChat);

            // 找到最后一条用户消息的位置，把"对话底注入"插到它前面
            int lastUserIdx = -1;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (string.Equals(history[i].Role, "user", StringComparison.Ordinal)) { lastUserIdx = i; break; }
            }

            for (int i = 0; i < history.Count; i++)
            {
                if (i == lastUserIdx)
                {
                    AppendInjectionsDepth(assembled.Turns, depthInjections, history.Count - i, values);
                    AppendInjectionsAsTurns(assembled.Turns, bottomInjections, InjectionPosition.BottomOfChat, values);
                }
                assembled.Turns.Add(history[i]);
            }

            if (lastUserIdx < 0)
            {
                // 没有用户消息（少见，例如开场就是助手首句）：全部追加到末尾
                AppendInjectionsAsTurns(assembled.Turns, bottomInjections, InjectionPosition.BottomOfChat, values);
                AppendInjectionsDepth(assembled.Turns, depthInjections, 0, values);
            }

            if (ctx.RegexRules != null && ctx.RegexRules.Count > 0)
            {
                var transformer = new RegexTransformer();
                transformer.Load(ctx.RegexRules);
                foreach (var turn in assembled.Turns)
                    if (turn.Role == "user" || turn.Role == "assistant")
                        turn.Content = transformer.ApplyOutgoing(turn.Content, turn.Role == "user");
            }

            return assembled;
        }

        // ── 人设 ─────────────────────────────────────────────────────────

        private static void AppendPersona(StringBuilder sb, Profile profile, IDictionary<string, string> values)
        {
            var persona = profile.Persona;

            if (!string.IsNullOrEmpty(persona.SystemPrompt))
            {
                AppendSection(sb, Placeholders.Resolve(persona.SystemPrompt, values));
                return;
            }

            // 系统提示词为空时按模板拼，空字段跳过，不留空标题
            var sb2 = new StringBuilder();
            if (!string.IsNullOrEmpty(persona.Name)) sb2.Append("你是 ").Append(persona.Name).Append("。\n");
            if (!string.IsNullOrEmpty(persona.Description)) sb2.Append("[描述] ").Append(persona.Description).Append('\n');
            if (!string.IsNullOrEmpty(persona.Personality)) sb2.Append("[性格] ").Append(persona.Personality).Append('\n');
            if (!string.IsNullOrEmpty(persona.Scenario)) sb2.Append("[场景] ").Append(persona.Scenario).Append('\n');
            if (!string.IsNullOrEmpty(persona.ExampleDialogue))
            {
                sb2.Append("[说话风格参考]\n").Append(persona.ExampleDialogue).Append('\n');
            }
            if (!string.IsNullOrEmpty(persona.PostHistoryInstructions))
            {
                sb2.Append("[附加要求] ").Append(persona.PostHistoryInstructions).Append('\n');
            }

            var text = sb2.ToString().Trim();
            if (text.Length > 0) AppendSection(sb, Placeholders.Resolve(text, values));
        }

        private static void AppendUserPersona(StringBuilder sb, UserPersona user, IDictionary<string, string> values)
        {
            if (user == null) return;
            if (string.IsNullOrEmpty(user.Name) && string.IsNullOrEmpty(user.Description)) return;

            var sb2 = new StringBuilder();
            sb2.Append("[关于我] ");
            if (!string.IsNullOrEmpty(user.Name)) sb2.Append("称呼我为「").Append(user.Name).Append("」。");
            if (!string.IsNullOrEmpty(user.Description)) sb2.Append(user.Description);
            AppendSection(sb, Placeholders.Resolve(sb2.ToString(), values));
        }

        private static void AppendKnowledge(StringBuilder sb, List<KnowledgeText> knowledge)
        {
            if (knowledge == null || knowledge.Count == 0) return;

            var sb2 = new StringBuilder();
            int used = 0;
            foreach (var file in knowledge)
            {
                if (file == null || string.IsNullOrEmpty(file.Content)) continue;
                if (used >= KnowledgeTotalChars) break;

                int budget = Math.Min(KnowledgePerFileChars, KnowledgeTotalChars - used);
                var body = TextUtil.Truncate(file.Content, budget);
                sb2.Append("《").Append(file.Name).Append("》\n").Append(body).Append('\n');
                used += body.Length;
            }

            if (sb2.Length > 0)
            {
                AppendSection(sb, "[知识库]\n" + sb2.ToString().Trim());
            }
        }

        private static void AppendLorebookHits(StringBuilder sb, List<InjectionItem> injections, IDictionary<string, string> values)
        {
            var hits = new List<InjectionItem>();
            foreach (var item in injections)
            {
                if (item.IsLorebookEntry && item.Matched) hits.Add(item);
            }
            if (hits.Count == 0) return;

            // 这些已经被放进 system，从后续按位置注入的列表里排除
            foreach (var hit in hits) hit.Consumed = true;

            var sb2 = new StringBuilder();
            foreach (var hit in hits)
            {
                sb2.Append(hit.ResolvedContent).Append('\n');
            }
            AppendSection(sb, "[设定]\n" + sb2.ToString().Trim());
        }

        private static void AppendMemory(StringBuilder sb, PromptContext ctx)
        {
            if (!string.IsNullOrEmpty(ctx.MemoryBlock)) AppendSection(sb, ctx.MemoryBlock);
            if (!string.IsNullOrEmpty(ctx.RecentChatsBlock)) AppendSection(sb, ctx.RecentChatsBlock);
        }

        private static void AppendSummary(StringBuilder sb, Profile profile, PromptContext ctx)
        {
            var conv = ctx.Conversation;
            if (conv == null || string.IsNullOrEmpty(conv.Summary)) return;
            AppendSection(sb, "【早前对话摘要】\n" + conv.Summary);
        }

        private static void AppendToolRules(StringBuilder sb, PromptContext ctx)
        {
            if (!ctx.ToolsEnabled || ctx.ToolNames.Count == 0) return;

            var sb2 = new StringBuilder();
            sb2.Append("【工具使用规则】\n");
            sb2.Append("你有以下工具可用：").Append(string.Join("、", ctx.ToolNames.ToArray())).Append("。\n");
            sb2.Append("• 一次只调用一个工具，等结果回来再决定下一步。\n");
            sb2.Append("• 参数要完整、准确；不确定的地方先问用户，不要猜。\n");
            sb2.Append("• 涉及写入、拨号、发短信、读取图片这类操作需要用户批准，被拒绝就换方案，不要反复重试。\n");
            sb2.Append("• 拿到足够信息后就用自然语言给出最终回答，不要为了用工具而用工具。\n");
            AppendSection(sb, sb2.ToString().Trim());
        }

        private static void AppendTimeReminder(StringBuilder sb, Profile profile, PromptContext ctx, IDictionary<string, string> values)
        {
            if (!profile.Memory.TimeReminder) return;

            var text = "【时间】现在是 " + values[Placeholders.CurDateTime];
            if (!string.IsNullOrEmpty(values[Placeholders.TimeZone])) text += "（" + values[Placeholders.TimeZone] + "）";
            text += "。";
            AppendSection(sb, text);
        }

        // ── 注入收集与按位置输出 ─────────────────────────────────────────

        private sealed class InjectionItem
        {
            public string Id = "";
            public string Name = "";
            public string Content = "";
            public string ResolvedContent = "";
            public InjectionPosition Position;
            public string Role = "system";
            public int Priority;
            public int Depth = 4;
            public bool Matched;
            public bool Consumed;
            public bool IsLorebookEntry;
        }

        private static List<InjectionItem> CollectInjections(PromptContext ctx, IDictionary<string, string> values)
        {
            var list = new List<InjectionItem>();
            var profile = ctx.Profile;
            var library = ctx.Library;
            if (library == null) return list;

            // 模式注入：按档案启用的 id 拉取
            foreach (var id in profile.Prompts.InjectionIds)
            {
                var injection = library.FindInjection(id);
                if (injection == null || !injection.Enabled || string.IsNullOrEmpty(injection.Content)) continue;
                list.Add(new InjectionItem
                {
                    Id = injection.Id,
                    Name = injection.Name,
                    Content = injection.Content,
                    ResolvedContent = Placeholders.Resolve(injection.Content, values),
                    Position = injection.Position,
                    Role = injection.Role,
                    Priority = injection.Priority,
                    Depth = injection.Depth,
                    Matched = true,
                    IsLorebookEntry = false
                });
            }

            // 世界书：扫描最近若干条消息找关键词
            var recentText = RecentTextForLorebook(ctx);
            foreach (var lorebookId in profile.Prompts.LorebookIds)
            {
                var book = library.FindLorebook(lorebookId);
                if (book == null || !book.Enabled) continue;

                foreach (var entry in book.Entries)
                {
                    if (entry == null || !entry.Enabled || string.IsNullOrEmpty(entry.Content)) continue;

                    bool matched = entry.AlwaysActive || MatchesKeywords(entry, recentText);
                    if (!matched) continue;

                    list.Add(new InjectionItem
                    {
                        Id = entry.Id,
                        Name = entry.Name,
                        Content = entry.Content,
                        ResolvedContent = Placeholders.Resolve(entry.Content, values),
                        Position = entry.Position,
                        Role = entry.Role,
                        Priority = entry.Priority,
                        Depth = 4,
                        Matched = true,
                        IsLorebookEntry = true
                    });
                }
            }

            list.Sort(delegate (InjectionItem a, InjectionItem b)
            {
                return b.Priority.CompareTo(a.Priority);
            });
            return list;
        }

        private static string RecentTextForLorebook(PromptContext ctx)
        {
            var conv = ctx.Conversation;
            if (conv == null || conv.Messages == null) return string.Empty;

            var sb = new StringBuilder();
            int take = 4;   // 默认扫描深度，逐条目还会各自截断
            int start = Math.Max(0, conv.Messages.Count - take);
            for (int i = start; i < conv.Messages.Count; i++)
            {
                var m = conv.Messages[i];
                if (!string.IsNullOrEmpty(m.Content)) sb.Append(m.Content).Append('\n');
            }
            return sb.ToString();
        }

        public static bool MatchesKeywords(LorebookEntry entry, string text)
        {
            if (entry.Keywords == null || entry.Keywords.Count == 0) return entry.AlwaysActive;
            if (string.IsNullOrEmpty(text)) return false;

            foreach (var raw in entry.Keywords)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var keyword = raw.Trim();

                if (entry.UseRegex)
                {
                    try
                    {
                        var options = entry.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                        if (Regex.IsMatch(text, keyword, options)) return true;
                    }
                    catch (Exception)
                    {
                        // 正则写错不该让整轮失败，退化成普通包含匹配
                        if (TextUtil.Contains(text, keyword, entry.CaseSensitive
                            ? StringComparison.Ordinal
                            : StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    continue;
                }

                // 普通匹配：中文没有词边界概念，直接包含即可；
                // 英文必须用词边界，否则 "art" 会命中 "start"。
                // 注意：拉丁关键词一旦走词边界分支，就**不能**再落到下面的包含判断，
                // 否则词边界形同虚设。
                if (IsMostlyLatin(keyword))
                {
                    bool latinHit = false;
                    try
                    {
                        var pattern = @"\b" + Regex.Escape(keyword) + @"\b";
                        var latinOptions = entry.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                        latinHit = Regex.IsMatch(text, pattern, latinOptions);
                    }
                    catch (Exception)
                    {
                        // 正则异常时退化为包含匹配
                        latinHit = TextUtil.Contains(text, keyword, entry.CaseSensitive
                            ? StringComparison.Ordinal
                            : StringComparison.OrdinalIgnoreCase);
                    }

                    if (latinHit) return true;
                    continue;
                }

                if (TextUtil.Contains(text, keyword, entry.CaseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool IsMostlyLatin(string s)
        {
            int latin = 0;
            foreach (var ch in s)
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')) latin++;
            }
            return s.Length > 0 && latin * 2 > s.Length;
        }

        private static List<InjectionItem> FilterInjections(List<InjectionItem> all, InjectionPosition position)
        {
            var result = new List<InjectionItem>();
            foreach (var item in all)
            {
                if (item.Consumed) continue;
                if (item.Position != position) continue;
                result.Add(item);
            }
            return result;
        }

        private static void AppendInjections(StringBuilder sb, List<InjectionItem> all, InjectionPosition position)
        {
            var items = FilterInjections(all, position);
            if (items.Count == 0) return;

            // 同一位置、同一角色的多条合并成一段，结构更干净
            var sb2 = new StringBuilder();
            foreach (var item in items)
            {
                if (sb2.Length > 0) sb2.Append('\n');
                sb2.Append(item.ResolvedContent);
            }
            AppendSection(sb, sb2.ToString().Trim());
        }

        private static void AppendInjectionsAsTurns(
            List<ChatTurn> turns, List<InjectionItem> items, InjectionPosition position, IDictionary<string, string> values)
        {
            if (items == null || items.Count == 0) return;

            var byRole = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (item.Consumed || item.Position != position) continue;
                var role = string.IsNullOrEmpty(item.Role) ? "system" : item.Role.ToLowerInvariant();
                if (!string.Equals(role, "assistant", StringComparison.Ordinal)) role = "user";

                StringBuilder sb;
                if (!byRole.TryGetValue(role, out sb))
                {
                    sb = new StringBuilder();
                    byRole[role] = sb;
                }
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(item.ResolvedContent);
            }

            foreach (var kv in byRole)
            {
                var text = kv.Value.ToString().Trim();
                if (text.Length == 0) continue;
                turns.Add(string.Equals(kv.Key, "assistant", StringComparison.Ordinal)
                    ? ChatTurn.Assistant(text)
                    : ChatTurn.User(text));
            }
        }

        /// <summary>AtDepth：插在距末尾 depth 条的位置。</summary>
        private static void AppendInjectionsDepth(
            List<ChatTurn> turns, List<InjectionItem> items, int depthFromEnd, IDictionary<string, string> values)
        {
            if (items == null || items.Count == 0) return;

            var sb = new StringBuilder();
            foreach (var item in items)
            {
                if (item.Consumed || item.Position != InjectionPosition.AtDepth) continue;
                if (item.Depth != depthFromEnd && depthFromEnd > 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(item.ResolvedContent);
            }
            var text = sb.ToString().Trim();
            if (text.Length > 0) turns.Add(ChatTurn.User(text));
        }

        private static void AppendSection(StringBuilder sb, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(text);
        }

        // ── 历史消息 → provider 消息 ─────────────────────────────────────

        /// <summary>
        /// 把会话历史转成 ChatTurn。跳过错过的已压缩区间，并按 contextMessageSize 截取最近若干条。
        /// 工具调用与结果成对保留 —— 少一边会被服务端拒绝。
        /// </summary>
        public static List<ChatTurn> BuildHistoryTurns(PromptContext ctx, IDictionary<string, string> values)
        {
            var turns = new List<ChatTurn>();
            var conv = ctx.Conversation;
            if (conv == null || conv.Messages == null) return turns;

            int start = conv.SummarizedUpTo + 1;
            if (start < 0) start = 0;

            int limit = ctx.Profile.Api.ContextMessageSize;
            if (limit > 0)
            {
                int desired = conv.Messages.Count - limit;
                if (desired > start) start = desired;
            }

            // 起点不能落在 tool 结果上 —— 那会让 assistant 的 tool_calls 缺结果
            while (start < conv.Messages.Count && conv.Messages[start].Role == MessageRole.Tool) start++;
            // 也不能落在"有 tool_calls 但结果在后面"的 assistant 之前，往回退一步更安全
            while (start > 0 && conv.Messages[start].Role == MessageRole.Tool) start--;

            for (int i = start; i < conv.Messages.Count; i++)
            {
                var msg = conv.Messages[i];
                switch (msg.Role)
                {
                    case MessageRole.User:
                        {
                            var turn = ChatTurn.User(ApplyMessageTemplate(ctx, msg.Content, values));
                            // 必须走 ModelSelection.ResolveVision —— 这里曾经只看裸布尔量，
                            // 结果模型级元数据判定的"支持图片"到不了这里，图片被静默丢掉。
                            if (ModelSelection.ResolveVision(ctx.Profile, ctx.Api))
                            {
                                // 图片附件由调用方在附件解析后塞进来（Core 不读文件）
                                if (msg.Attachments != null)
                                {
                                    foreach (var att in msg.Attachments)
                                    {
                                        if (att.IsImage && !string.IsNullOrEmpty(att.ImageBase64))
                                        {
                                            turn.Images.Add(new ImagePart
                                            {
                                                MimeType = string.IsNullOrEmpty(att.MimeType) ? "image/png" : att.MimeType,
                                                Base64 = att.ImageBase64
                                            });
                                        }
                                    }
                                }
                            }
                            turns.Add(turn);
                            break;
                        }

                    case MessageRole.Assistant:
                        {
                            var turn = ChatTurn.Assistant(msg.Content ?? "");
                            turn.Reasoning = msg.Reasoning ?? "";
                            if (msg.HasToolCalls)
                            {
                                foreach (var call in msg.ToolCalls)
                                {
                                    turn.ToolCalls.Add(new ToolCallRequest
                                    {
                                        Id = call.Id,
                                        Name = call.Name,
                                        ArgumentsJson = call.ArgumentsJson
                                    });
                                }
                            }
                            turns.Add(turn);
                            break;
                        }

                    case MessageRole.Tool:
                        {
                            var body = msg.Content ?? "";

                            // 只保留最近若干条工具结果的全文，更早的截断。
                            // 长会话里工具输出会把上下文吃光，这一招是撑住长任务的关键。
                            int age = conv.Messages.Count - i;
                            if (age > Limits.KeepFullToolResults && body.Length > Limits.TrimmedToolResultChars)
                            {
                                body = TextUtil.Truncate(body, Limits.TrimmedToolResultChars);
                            }

                            turns.Add(ChatTurn.ToolResult(msg.ToolCallId, "", body));
                            break;
                        }

                    case MessageRole.System:
                        // 本地系统事件（压缩提示等）不进请求，它们是给用户看的
                        break;
                }
            }

            return turns;
        }

        /// <summary>套用档案的消息模板。模板缺 {{message}} 时 Repair() 已兜底成原样。</summary>
        public static string ApplyMessageTemplate(PromptContext ctx, string content, IDictionary<string, string> values)
        {
            var template = ctx.Profile.Generation.MessageTemplate;
            if (string.IsNullOrEmpty(template) || template == "{{message}}") return content ?? "";

            var withBody = template.Replace("{{message}}", content ?? "");
            return Placeholders.Resolve(withBody, values);
        }
    }
}
