// Core/Text/QuickMessages.cs —— 快捷消息的占位符展开
//
// 复用提示词里已有的占位符约定（Placeholders），这样用户在快捷消息里
// 写 {char} 与在系统提示里写 {char} 是同一套语义，不用记两套。
//
// 展开只做替换，不做任何"智能"处理：认不出的占位符原样留着 ——
// 静默删掉会让用户以为自己写错了，留着至少看得见。

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Yanshuai.Messages.Core
{
    public sealed class QuickMessageContext
    {
        public string CharacterName { get; set; } = "";
        public string UserName { get; set; } = "";
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Now;
        public string ConversationTitle { get; set; } = "";
    }

    public static class QuickMessages
    {
        /// <summary>默认快捷消息。新用户不必从零开始攒。</summary>
        public static List<QuickMessage> CreateDefaults()
        {
            var list = new List<QuickMessage>();
            int sort = 0;

            list.Add(Make("继续", "继续", sort++));
            list.Add(Make("展开说说", "展开详细说说。", sort++));
            list.Add(Make("更简短", "把上面的回答压缩到一半长度，保留关键信息。", sort++));
            list.Add(Make("翻译成英文", "把上面这段翻译成英语，代码与专有名词保持原样。", sort++));
            list.Add(Make("改写成代码", "把上面的需求写成可运行的代码。", sort++));
            list.Add(Make("检查错误", "检查上面内容里的错误，逐条指出并给出修正。", sort++));

            return list;
        }

        private static QuickMessage Make(string title, string content, int sort)
        {
            return new QuickMessage { Title = title, Content = content, Sort = sort };
        }

        /// <summary>
        /// 把占位符换成实际内容。直接走 Placeholders.Resolve ——
        /// 这样 {char} 在快捷消息与系统提示里是同一套语义、同一套容错，
        /// 用户不用记两种写法（也支持 {{char}} 的双花括号写法）。
        /// </summary>
        public static string Expand(string template, QuickMessageContext context)
        {
            if (string.IsNullOrEmpty(template)) return "";
            if (context == null) return template;

            var values = Placeholders.BuildValues(
                context.CharacterName,
                context.UserName,
                "",                 // 快捷消息不关心模型 id
                "",
                context.Now,
                "zh-CN",
                TimeZoneInfo.Local.DisplayName,
                "",
                "");

            return Placeholders.Resolve(template, values);
        }

        /// <summary>按 Sort 排好、只留可见的。界面直接用这个结果。</summary>
        public static List<QuickMessage> Visible(List<QuickMessage> all)
        {
            var result = new List<QuickMessage>();
            if (all == null) return result;

            foreach (var item in all)
            {
                if (item == null) continue;
                if (!item.Visible) continue;
                if (string.IsNullOrWhiteSpace(item.Title) && string.IsNullOrWhiteSpace(item.Content)) continue;
                result.Add(item);
            }

            result.Sort(delegate (QuickMessage a, QuickMessage b) { return a.Sort.CompareTo(b.Sort); });
            return result;
        }

        /// <summary>下一个排序值（新建时用，避免插到中间还要重排）。</summary>
        public static int NextSort(List<QuickMessage> all)
        {
            int max = -1;
            if (all != null)
            {
                foreach (var item in all)
                {
                    if (item != null && item.Sort > max) max = item.Sort;
                }
            }
            return max + 1;
        }
    }
}
