// Core/Models/Group.cs —— 群聊
//
// 群聊 = 一个会话里挂多个角色档案。与"单角色会话"的差别只有三处：
//   1) 系统提示词要拼多个人设（各自一段，带名字分隔）；
//   2) 首句可能有多条（每个成员各一条）；
//   3) 回复模式决定"这一轮该谁说话"。
//
// 其余全部复用：工具、记忆、审批、时间线投影都不用改。
// 这是刻意设计 —— 群聊不该变成第二套对话系统。

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Yanshuai.Messages.Core
{
    /// <summary>群成员：引用一个档案，并覆盖它在群里的显示方式。</summary>
    public sealed class GroupMember
    {
        [JsonProperty("profileId")] public string ProfileId { get; set; } = "";
        /// <summary>群里显示的名字（留空则用档案名）。</summary>
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("avatar")] public string Avatar { get; set; } = "";
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
        /// <summary>发言顺序（轮流模式按这个排）。</summary>
        [JsonProperty("order")] public int Order { get; set; }

        /// <summary>这个成员在群里的首句（可空）。</summary>
        [JsonProperty("greeting")] public string Greeting { get; set; } = "";

        public void Repair()
        {
            if (string.IsNullOrEmpty(ProfileId)) Enabled = false;
        }
    }

    /// <summary>群聊回复模式。</summary>
    public static class GroupReplyModes
    {
        /// <summary>按 Order 轮流说。</summary>
        public const string RoundRobin = "round_robin";
        /// <summary>用户消息里 @某人 时只有那个人说。</summary>
        public const string Mention = "mention";
        /// <summary>把成员清单交给模型，由它决定谁来说（适合角色扮演）。</summary>
        public const string Free = "free";

        public static readonly string[] All = new string[] { RoundRobin, Mention, Free };

        public static string Label(string mode)
        {
            switch (mode)
            {
                case Mention: return "点名回复（@某人）";
                case Free: return "自由发言（模型决定）";
                default: return "轮流发言";
            }
        }

        public static string Normalize(string mode)
        {
            foreach (var candidate in All)
            {
                if (string.Equals(candidate, mode, StringComparison.Ordinal)) return candidate;
            }
            return RoundRobin;
        }
    }

    /// <summary>群聊决策：这一轮谁来说，以及为什么。</summary>
    public sealed class SpeakerDecision
    {
        public List<GroupMember> Speakers { get; private set; }
        public string Reason { get; set; } = "";

        public SpeakerDecision()
        {
            Speakers = new List<GroupMember>();
        }

        public bool HasSpeaker { get { return Speakers.Count > 0; } }
    }

    public static class GroupChat
    {
        /// <summary>只保留启用的成员，并按 Order 排好。</summary>
        public static List<GroupMember> ActiveMembers(Conversation conversation)
        {
            var list = new List<GroupMember>();
            if (conversation == null || conversation.Members == null) return list;

            foreach (var member in conversation.Members)
            {
                if (member == null || !member.Enabled) continue;
                if (string.IsNullOrEmpty(member.ProfileId)) continue;
                list.Add(member);
            }

            list.Sort(delegate (GroupMember a, GroupMember b) { return a.Order.CompareTo(b.Order); });
            return list;
        }

        public static bool IsGroup(Conversation conversation)
        {
            return ActiveMembers(conversation).Count > 1;
        }

        /// <summary>
        /// 决定这一轮由谁发言。
        ///
        /// 注意：这里刻意**不做**"模型自己决定"的复杂判断 ——
        /// 那需要额外一次模型调用，在手机上既慢又费额度。
        /// 自由模式交给提示词去引导（把成员清单写进系统提示，让主模型自己选择以谁的口吻写），
        /// 本方法只处理"轮流"与"点名"这两种确定性规则。
        /// </summary>
        public static SpeakerDecision DecideSpeakers(
            Conversation conversation, string userInput, int assistantMessagesInTurn)
        {
            var decision = new SpeakerDecision();
            var members = ActiveMembers(conversation);
            if (members.Count == 0) return decision;

            if (members.Count == 1)
            {
                decision.Speakers.Add(members[0]);
                decision.Reason = "单成员";
                return decision;
            }

            var mode = GroupReplyModes.Normalize(conversation.GroupReplyMode);

            if (mode == GroupReplyModes.Mention)
            {
                var mentioned = FindMentioned(members, userInput);
                if (mentioned.Count > 0)
                {
                    decision.Speakers.AddRange(mentioned);
                    decision.Reason = "点名";
                    return decision;
                }
                // 没点名就不指定，交给轮流
                mode = GroupReplyModes.RoundRobin;
            }

            if (mode == GroupReplyModes.RoundRobin)
            {
                // 按"本轮已经说了几条"推进指针，这样多轮生成时不会同一个人连说
                int index = assistantMessagesInTurn % members.Count;
                decision.Speakers.Add(members[index]);
                decision.Reason = "轮到 " + DisplayName(members[index]);
                return decision;
            }

            // 自由模式：不指定具体人，让提示词引导
            decision.Reason = "自由发言";
            return decision;
        }

        /// <summary>找出被 @ 的成员。支持 @名字 与 @名字 带全角空格。</summary>
        public static List<GroupMember> FindMentioned(List<GroupMember> members, string text)
        {
            var result = new List<GroupMember>();
            if (string.IsNullOrEmpty(text) || members == null) return result;

            foreach (var member in members)
            {
                var name = DisplayName(member);
                if (string.IsNullOrEmpty(name)) continue;

                // @名字 后面要么结束，要么是空白/标点，避免 @林 命中 @林深
                if (ContainsMention(text, name))
                {
                    result.Add(member);
                }
            }
            return result;
        }

        private static bool ContainsMention(string text, string name)
        {
            var token = "@" + name;
            int from = 0;

            while (true)
            {
                int index = text.IndexOf(token, from, StringComparison.OrdinalIgnoreCase);
                if (index < 0) return false;

                int after = index + token.Length;
                if (after >= text.Length) return true;

                char next = text[after];
                if (char.IsWhiteSpace(next) || char.IsPunctuation(next)) return true;

                from = index + 1;
            }
        }

        public static string DisplayName(GroupMember member)
        {
            if (member == null) return "";
            return string.IsNullOrWhiteSpace(member.Name) ? "" : member.Name.Trim();
        }

        /// <summary>
        /// 把多个成员的人设拼成一段群聊提示词。
        /// 每个成员一段、带名字分隔，并明确告诉模型"以谁的口吻写"。
        /// </summary>
        public static string BuildGroupPersonaBlock(
            Conversation conversation,
            Func<string, Profile> resolveProfile,
            IReadOnlyList<GroupMember> speakers)
        {
            var members = ActiveMembers(conversation);
            if (members.Count == 0) return "";

            var sb = new System.Text.StringBuilder();
            sb.Append("【这是一个群聊场景】\n");
            sb.Append("群里有以下角色：\n");

            foreach (var member in members)
            {
                var name = DisplayName(member);
                var profile = resolveProfile != null ? resolveProfile(member.ProfileId) : null;
                if (string.IsNullOrEmpty(name) && profile != null) name = profile.Name;

                sb.Append("· ").Append(name);

                if (profile != null && profile.Persona != null)
                {
                    var brief = FirstNonEmpty(profile.Persona.Personality, profile.Persona.Description);
                    if (!string.IsNullOrEmpty(brief))
                    {
                        sb.Append("：").Append(TextUtil.Truncate(brief, 120));
                    }
                }
                sb.Append('\n');
            }

            sb.Append('\n');
            if (speakers != null && speakers.Count > 0)
            {
                sb.Append("这一轮由 ");
                for (int i = 0; i < speakers.Count; i++)
                {
                    if (i > 0) sb.Append("、");
                    var name = DisplayName(speakers[i]);
                    if (string.IsNullOrEmpty(name))
                    {
                        var profile = resolveProfile != null ? resolveProfile(speakers[i].ProfileId) : null;
                        name = profile != null ? profile.Name : "某位角色";
                    }
                    sb.Append(name);
                }
                sb.Append(" 发言。请以这个角色的身份与语气回答，不要代替其他角色说话。\n");
            }
            else
            {
                sb.Append("请自己判断这一轮该由谁发言，并在回答开头用「角色名：」标明是谁在说话。");
                sb.Append("一次只让一个角色说话。\n");
            }

            sb.Append("每句话前面都标上「角色名：」，这样用户能分清是谁在说。");

            return sb.ToString();
        }

        private static string FirstNonEmpty(string a, string b)
        {
            if (!string.IsNullOrWhiteSpace(a)) return a.Trim();
            if (!string.IsNullOrWhiteSpace(b)) return b.Trim();
            return "";
        }
    }
}
