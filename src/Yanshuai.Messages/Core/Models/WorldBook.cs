// Core/Models/WorldBook.cs —— 世界书与模式注入
//
// 两者的区别只有一点：模式注入靠开关（常驻），世界书条目靠关键词命中。
// 条目字段与 SillyTavern 的 character_book.entries 对齐，导入时直接落位。

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Yanshuai.Messages.Core
{
    public sealed class LorebookEntry
    {
        [JsonProperty("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("keywords")] public List<string> Keywords { get; set; }
        [JsonProperty("content")] public string Content { get; set; } = "";

        /// <summary>命中关键词时插到哪。SillyTavern 的 position 会映射过来。</summary>
        [JsonProperty("position")] public InjectionPosition Position { get; set; } = InjectionPosition.AfterSystem;

        /// <summary>user 或 assistant。决定插入的消息角色。</summary>
        [JsonProperty("role")] public string Role { get; set; } = "system";

        /// <summary>越大越先注入。</summary>
        [JsonProperty("priority")] public int Priority { get; set; } = 100;

        /// <summary>无条件注入（相当于常驻）。</summary>
        [JsonProperty("alwaysActive")] public bool AlwaysActive { get; set; }

        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;

        /// <summary>往回看多少条消息找关键词。</summary>
        [JsonProperty("scanDepth")] public int ScanDepth { get; set; } = 4;

        /// <summary>关键词当正则用。</summary>
        [JsonProperty("useRegex")] public bool UseRegex { get; set; }

        [JsonProperty("caseSensitive")] public bool CaseSensitive { get; set; }

        public LorebookEntry()
        {
            Keywords = new List<string>();
        }
    }

    public sealed class Lorebook
    {
        [JsonProperty("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("entries")] public List<LorebookEntry> Entries { get; set; }
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;

        public Lorebook()
        {
            Entries = new List<LorebookEntry>();
        }
    }

    public sealed class ModeInjection
    {
        [JsonProperty("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("content")] public string Content { get; set; } = "";
        [JsonProperty("position")] public InjectionPosition Position { get; set; } = InjectionPosition.AfterSystem;
        [JsonProperty("role")] public string Role { get; set; } = "system";
        [JsonProperty("priority")] public int Priority { get; set; } = 100;
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;

        /// <summary>AtDepth 时才用得上：距末尾第几条。</summary>
        [JsonProperty("depth")] public int Depth { get; set; } = 4;
    }

    public sealed class PromptLibrary
    {
        [JsonProperty("injections")] public List<ModeInjection> Injections { get; set; }
        [JsonProperty("lorebooks")] public List<Lorebook> Lorebooks { get; set; }

        /// <summary>
        /// 快捷消息。放在共享区（library.json）而不是档案里 ——
        /// 同一句"帮我把这段翻译一下"在任何档案下都想用，
        /// 每个档案各存一份的话用户得维护 N 遍。
        /// </summary>
        [JsonProperty("quickMessages")] public List<QuickMessage> QuickMessages { get; set; }

        public PromptLibrary()
        {
            Injections = new List<ModeInjection>();
            Lorebooks = new List<Lorebook>();
            QuickMessages = new List<QuickMessage>();
        }

        public ModeInjection FindInjection(string id)
        {
            foreach (var i in Injections)
            {
                if (i.Id == id) return i;
            }
            return null;
        }

        public Lorebook FindLorebook(string id)
        {
            foreach (var l in Lorebooks)
            {
                if (l.Id == id) return l;
            }
            return null;
        }
    }

    /// <summary>知识库文件（档案 files\ 目录下、被显式加入上下文的那批）。</summary>
    public sealed class KnowledgeFile
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("path")] public string Path { get; set; } = "";
        [JsonProperty("size")] public long SizeBytes { get; set; }
        [JsonProperty("addedAtUtc")] public DateTimeOffset AddedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// 快捷消息。点一下就填进输入框，不是直接发送 ——
    /// 用户多半还要改几个字（比如把占位符换成具体内容），直接发出去反而添乱。
    /// </summary>
    public sealed class QuickMessage
    {
        [JsonProperty("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>按钮上显示的文字。</summary>
        [JsonProperty("title")] public string Title { get; set; } = "";
        /// <summary>填进输入框的内容，可含 {char} {user} {time} 占位符。</summary>
        [JsonProperty("content")] public string Content { get; set; } = "";
        [JsonProperty("sort")] public int Sort { get; set; }
        /// <summary>false 时不出现在快捷栏，但仍保留在设置里。</summary>
        [JsonProperty("visible")] public bool Visible { get; set; } = true;
    }
}
