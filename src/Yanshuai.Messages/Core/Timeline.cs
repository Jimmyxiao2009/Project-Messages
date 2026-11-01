// Core/Timeline.cs —— 时间线投影
//
// 纯函数：会话（消息序列）→ 时间线行。整个"过程怎么显示"的规则只有这一处，
// UI 只负责把 TimelineRow 画出来，不参与判断。这样规则可以被单元测试覆盖，
// 也不会像旧项目那样"轨道折叠状态"和"消息数据"两边打架。
//
// 一"轮"的定义：从一条用户消息开始，到下一条用户消息之前。
//   轮内：所有 assistant / tool 消息 → 一条 Process 行的若干步骤
//        最后一条 isFinal 的 assistant → 一条 Output 行
// 顺序上 Process 在 Output 之前。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Yanshuai.Messages.Core
{
    public static class TimelineProjector
    {
        /// <summary>
        /// 投影整个会话。
        /// liveSteps 是本轮正在跑（还没落盘）的步骤；传入后当前轮会用它们显示。
        /// </summary>
        public static List<TimelineRow> Project(
            Conversation conversation,
            bool running,
            IReadOnlyList<StepRecord> liveSteps)
        {
            var rows = new List<TimelineRow>();
            if (conversation == null || conversation.Messages == null) return rows;

            var turnSteps = new List<StepRecord>();
            var turnToolIndex = new Dictionary<string, StepRecord>(StringComparer.Ordinal);
            bool turnFailed = false;
            bool turnStopped = false;
            DateTimeOffset turnStart = DateTimeOffset.MinValue;
            int turnTokensIn = 0;
            int turnTokensOut = 0;

            Action flushProcess = delegate ()
            {
                if (turnSteps.Count == 0) return;
                var row = new TimelineRow
                {
                    Kind = RowKind.Process,
                    Key = "p:" + (turnStart == DateTimeOffset.MinValue
                        ? Guid.NewGuid().ToString("N")
                        : turnStart.UtcTicks.ToString(CultureInfo.InvariantCulture)),
                    Steps = turnSteps,
                    State = turnFailed ? StepState.Failed
                          : turnStopped ? StepState.Stopped
                          : StepState.Done,
                    Summary = BuildSummary(turnSteps),
                    Collapsed = !(turnFailed || turnStopped),
                    Time = turnStart,
                    TokensIn = turnTokensIn,
                    TokensOut = turnTokensOut
                };
                rows.Add(row);

                turnSteps = new List<StepRecord>();
                turnToolIndex.Clear();
                turnFailed = false;
                turnStopped = false;
                turnStart = DateTimeOffset.MinValue;
                turnTokensIn = 0;
                turnTokensOut = 0;
            };

            for (int i = 0; i < conversation.Messages.Count; i++)
            {
                var msg = conversation.Messages[i];

                if (msg.Role == MessageRole.User)
                {
                    flushProcess();
                    rows.Add(new TimelineRow
                    {
                        Kind = msg.Failed ? RowKind.Error : RowKind.User,
                        Key = "u:" + msg.Id,
                        Source = new MessageIdRef { MessageId = msg.Id, Index = i },
                        Text = msg.Content,
                        Attachments = msg.Attachments ?? new List<Attachment>(),
                        Failed = msg.Failed,
                        ErrorCode = msg.ErrorCode,
                        Time = msg.CreatedAtUtc,
                        TokensIn = msg.TokensIn,
                        TokensOut = msg.TokensOut
                    });
                    continue;
                }

                if (msg.Role == MessageRole.System)
                {
                    flushProcess();
                    rows.Add(new TimelineRow
                    {
                        Kind = msg.Failed ? RowKind.Error : RowKind.Marker,
                        Key = "m:" + msg.Id,
                        Source = new MessageIdRef { MessageId = msg.Id, Index = i },
                        Text = msg.Content,
                        Failed = msg.Failed,
                        ErrorCode = msg.ErrorCode,
                        Time = msg.CreatedAtUtc
                    });
                    continue;
                }

                if (msg.Role == MessageRole.Tool)
                {
                    if (turnStart == DateTimeOffset.MinValue) turnStart = msg.CreatedAtUtc;
                    StepRecord step;
                    if (!string.IsNullOrEmpty(msg.ToolCallId) && turnToolIndex.TryGetValue(msg.ToolCallId, out step))
                    {
                        step.Body = msg.Content ?? string.Empty;
                        step.State = msg.Failed ? StepState.Failed : StepState.Done;
                        if (msg.Failed) turnFailed = true;
                    }
                    continue;
                }

                // ── assistant ──────────────────────────────────────────────
                if (turnStart == DateTimeOffset.MinValue) turnStart = msg.CreatedAtUtc;
                turnTokensIn += msg.TokensIn;
                turnTokensOut += msg.TokensOut;

                // 把这条 assistant 消息里的"过程"收进当前轨道：思考与工具调用。
                // 定稿消息和中间消息都要走这里 —— 见下面 IsFinal 分支的说明。
                Action collectProcess = delegate ()
                {
                    if (!string.IsNullOrEmpty(msg.Reasoning))
                    {
                        turnSteps.Add(new StepRecord
                        {
                            Kind = StepKind.Reasoning,
                            State = StepState.Done,
                            Title = "思考",
                            Body = msg.Reasoning,
                            StartedAtUtc = msg.CreatedAtUtc,
                            CallId = msg.Id + ":reasoning"
                        });
                    }

                    if (msg.HasToolCalls)
                    {
                        foreach (var call in msg.ToolCalls)
                        {
                            var step = new StepRecord
                            {
                                Kind = StepKind.Tool,
                                State = msg.Failed ? StepState.Failed : StepState.Running,
                                Title = ToolDisplayName(call.Name),
                                Subtitle = SummarizeArguments(call.ArgumentsJson),
                                StartedAtUtc = msg.CreatedAtUtc,
                                CallId = call.Id
                            };
                            turnSteps.Add(step);
                            if (!string.IsNullOrEmpty(call.Id)) turnToolIndex[call.Id] = step;
                            if (msg.Failed) turnFailed = true;
                        }
                    }
                };

                if (msg.IsFinal)
                {
                    // 必须先把过程收进来再 flush。这里曾经直接 flush + continue，
                    // 于是**定稿后思考与工具步骤整段消失**：流式生成时能看到"思考"，
                    // 回复一结束（消息被标记 IsFinal 并重建时间线）就再也找不到了，
                    // 重新打开会话同样没有 —— 因为重建时 reasoning 根本没被读。
                    collectProcess();
                    flushProcess();
                    rows.Add(new TimelineRow
                    {
                        Kind = msg.Failed ? RowKind.Error : RowKind.Output,
                        Key = "o:" + msg.Id,
                        Source = new MessageIdRef { MessageId = msg.Id, Index = i },
                        Text = msg.Content,
                        IsFinal = true,
                        Failed = msg.Failed,
                        ErrorCode = msg.ErrorCode,
                        Time = msg.CreatedAtUtc,
                        TokensIn = msg.TokensIn,
                        TokensOut = msg.TokensOut
                    });
                    continue;
                }

                collectProcess();

                if (!string.IsNullOrEmpty(msg.Content))
                {
                    turnSteps.Add(new StepRecord
                    {
                        Kind = StepKind.Note,
                        State = StepState.Done,
                        Title = string.Empty,
                        Body = msg.Content,
                        StartedAtUtc = msg.CreatedAtUtc
                    });
                }
            }

            // 正在跑的一轮：把实时步骤接上去
            if (running)
            {
                if (liveSteps != null && liveSteps.Count > 0)
                {
                    foreach (var s in liveSteps)
                    {
                        if (s == null) continue;
                        // 实时步骤和上面从消息推导出的步骤是同一批的两个来源
                        // （AgentRunner 先落过程消息再推 StepStarted）。按 CallId 去重，
                        // 没有 CallId 的老数据退回"同类型+同标题+同正文"判重。
                        bool duplicate = false;
                        if (!string.IsNullOrEmpty(s.CallId))
                        {
                            foreach (var existing in turnSteps)
                            {
                                if (string.Equals(existing.CallId, s.CallId, StringComparison.Ordinal)) { duplicate = true; break; }
                            }
                        }
                        else
                        {
                            foreach (var existing in turnSteps)
                            {
                                if (existing.Kind == s.Kind
                                    && string.Equals(existing.Title, s.Title, StringComparison.Ordinal)
                                    && string.Equals(existing.Body, s.Body, StringComparison.Ordinal)) { duplicate = true; break; }
                            }
                        }
                        if (!duplicate) turnSteps.Add(s);
                    }
                }
                if (turnSteps.Count > 0)
                {
                    var row = new TimelineRow
                    {
                        Kind = RowKind.Process,
                        Key = "plive",
                        Steps = turnSteps,
                        State = StepState.Running,
                        Summary = BuildSummary(turnSteps, true),
                        Collapsed = false,
                        Time = turnStart == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : turnStart,
                        TokensIn = turnTokensIn,
                        TokensOut = turnTokensOut
                    };
                    rows.Add(row);
                }
            }

            return rows;
        }

        /// <summary>
        /// 把一条轨道的步骤转成"该显示的列表"：运行中且步骤很多时，
        /// 把最早的若干步折叠成一个 More 节点，只露最近 RunningVisibleSteps 步。
        /// </summary>
        public static List<StepRecord> VisibleSteps(IReadOnlyList<StepRecord> steps, bool running)
        {
            var result = new List<StepRecord>();
            if (steps == null || steps.Count == 0) return result;

            if (running && steps.Count > Limits.RunningVisibleSteps + 1)
            {
                int hidden = steps.Count - Limits.RunningVisibleSteps;
                int reasoning = 0, tools = 0, notes = 0;
                for (int i = 0; i < hidden; i++)
                {
                    switch (steps[i].Kind)
                    {
                        case StepKind.Reasoning: reasoning++; break;
                        case StepKind.Tool: tools++; break;
                        case StepKind.Note: notes++; break;
                    }
                }

                var more = new StepRecord
                {
                    Kind = StepKind.More,
                    State = StepState.Done,
                    Title = "显示前面 " + hidden.ToString(CultureInfo.InvariantCulture) + " 步",
                    Subtitle = CounterText(reasoning, tools, notes, 0),
                    StartedAtUtc = steps[0].StartedAtUtc
                };
                result.Add(more);

                for (int i = hidden; i < steps.Count; i++) result.Add(steps[i]);
                return result;
            }

            for (int i = 0; i < steps.Count; i++) result.Add(steps[i]);
            return result;
        }

        /// <summary>形如 "思考 2 · 工具 4 · 1 失败"。</summary>
        public static string BuildSummary(IReadOnlyList<StepRecord> steps, bool running = false)
        {
            int reasoning = 0, tools = 0, notes = 0, failed = 0, runningCount = 0;
            long ms = 0;

            if (steps != null)
            {
                foreach (var s in steps)
                {
                    switch (s.Kind)
                    {
                        case StepKind.Reasoning: reasoning++; break;
                        case StepKind.Tool: tools++; break;
                        case StepKind.Note: notes++; break;
                    }
                    if (s.State == StepState.Failed) failed++;
                    if (s.State == StepState.Running) runningCount++;
                    ms += s.DurationMs;
                }
            }

            var text = CounterText(reasoning, tools, notes, failed);
            if (string.IsNullOrEmpty(text)) return string.Empty;

            if (running || runningCount > 0) return text;
            if (ms >= 1000)
            {
                return text + " · 用时 " + (ms / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " 秒";
            }
            return text;
        }

        private static string CounterText(int reasoning, int tools, int notes, int failed)
        {
            var sb = new StringBuilder();
            if (reasoning > 0) Append(sb, "思考 " + reasoning.ToString(CultureInfo.InvariantCulture));
            if (tools > 0) Append(sb, "工具 " + tools.ToString(CultureInfo.InvariantCulture));
            if (notes > 0) Append(sb, "说明 " + notes.ToString(CultureInfo.InvariantCulture));
            if (failed > 0) Append(sb, failed.ToString(CultureInfo.InvariantCulture) + " 失败");
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string part)
        {
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append(part);
        }

        /// <summary>工具名 → 人话。放在逻辑层，UI 不用再维护一份映射表。</summary>
        public static string ToolDisplayName(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return "工具";
            switch (toolName)
            {
                case "read_file": return "读取文件";
                case "write_file": return "写入文件";
                case "list_files": return "列出文件";
                case "web_search": return "联网搜索";
                case "fetch_page": return "抓取网页";
                case "read_image": return "读取图片";
                case "calendar_list": return "查看日程";
                case "calendar_create": return "新建日程";
                case "contacts_search": return "查找联系人";
                case "make_call": return "拨打电话";
                case "send_sms": return "发送短信";
                case "open_app": return "打开应用";
                case "media_control": return "媒体控制";
                case "spawn_subagent": return "子代理";
                case "mcp_list": return "列出 MCP";
                case "mcp_call": return "调用 MCP";
                case "skill_list": return "列出技能";
                case "skill_use": return "使用技能";
                case "get_time": return "查看时间";
                case "memory_search": return "检索记忆";
                default: return toolName;
            }
        }

        /// <summary>把工具入参压成一行摘要，给轨道步骤做副标题。</summary>
        public static string SummarizeArguments(string argumentsJson)
        {
            if (string.IsNullOrEmpty(argumentsJson)) return string.Empty;

            var pairs = ToolArguments.Parse(argumentsJson);
            if (pairs.Count == 0) return TextUtil.Truncate(TextUtil.Normalize(argumentsJson), 80);

            var sb = new StringBuilder();
            foreach (var kv in pairs)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Key).Append('=').Append(kv.Value);
                if (sb.Length > 70) break;
            }
            return TextUtil.Truncate(sb.ToString().TrimEnd(',', ' '), 80);
        }
    }
}
