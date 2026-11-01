// Core/Models/Memory.cs —— 记忆条目与记忆池文件
//
// 关键设计：向量与文本分开存（vectors 段）。换嵌入模型时只需要丢掉 vectors 段重建索引，
// 文本一条不丢。旧项目把向量塞在每个 MemoryItem 里，换模型后旧条目全部失配而且没法区分
// "没算过" 和 "算过但维度不对"。

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Yanshuai.Messages.Core
{
    public sealed class MemoryItem
    {
        [JsonProperty("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonProperty("text")] public string Text { get; set; } = "";
        [JsonProperty("category")] public MemoryCategory Category { get; set; } = MemoryCategory.Fact;
        /// <summary>0–1。</summary>
        [JsonProperty("importance")] public double Importance { get; set; } = 0.6;
        [JsonProperty("scope")] public MemoryScope Scope { get; set; } = MemoryScope.Profile;
        [JsonProperty("createdAtUtc")] public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        [JsonProperty("updatedAtUtc")] public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        [JsonProperty("accessCount")] public int AccessCount { get; set; }
        [JsonProperty("sourceConversationId")] public string SourceConversationId { get; set; } = "";

        /// <summary>用于去重，不落盘。</summary>
        [JsonIgnore]
        public string NormalizedKey
        {
            get { return TextUtil.Normalize(Text); }
        }
    }

    /// <summary>记忆池文件。文本与向量分两段。</summary>
    public sealed class MemoryFile
    {
        [JsonProperty("items")] public List<MemoryItem> Items { get; set; }

        /// <summary>itemId -> 向量。缺失表示"这条没算过向量"。</summary>
        [JsonProperty("vectors")] public Dictionary<string, float[]> Vectors { get; set; }

        /// <summary>算这些向量时用的嵌入模型标识，换模型时用来判断要不要重建。</summary>
        [JsonProperty("embedder")] public string EmbedderId { get; set; } = "";
        [JsonProperty("dimensions")] public int Dimensions { get; set; }

        public MemoryFile()
        {
            Items = new List<MemoryItem>();
            Vectors = new Dictionary<string, float[]>();
        }
    }

    public sealed class UserPersonaFile
    {
        [JsonProperty("items")] public List<UserPersona> Items { get; set; }
        [JsonProperty("activeId")] public string ActiveId { get; set; } = "";

        public UserPersonaFile()
        {
            Items = new List<UserPersona>();
        }

        public UserPersona Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var p in Items)
            {
                if (p.Id == id) return p;
            }
            return null;
        }
    }

    /// <summary>全局模型档案集合（_shared/api-profiles.json）。</summary>
    public sealed class ApiProfileFile
    {
        [JsonProperty("items")] public List<ApiProfile> Items { get; set; }
        [JsonProperty("defaultId")] public string DefaultId { get; set; } = "";

        public ApiProfileFile()
        {
            Items = new List<ApiProfile>();
        }

        public ApiProfile Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var p in Items)
            {
                if (p.Id == id) return p;
            }
            return null;
        }

        /// <summary>
        /// 三级解析：档案指定 → 全局默认 → 第一个。
        /// 全部落空返回 null，由调用方给出"还没配模型"的可读错误。
        /// </summary>
        public ApiProfile Resolve(string profilePreferredId)
        {
            var p = Find(profilePreferredId);
            if (p != null) return p;
            p = Find(DefaultId);
            if (p != null) return p;
            return Items.Count > 0 ? Items[0] : null;
        }
    }

    /// <summary>已授权的敏感操作键（_shared/permissions.json）。</summary>
    public sealed class PermissionFile
    {
        [JsonProperty("granted")] public List<string> Granted { get; set; }
        [JsonProperty("updatedAtUtc")] public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

        public PermissionFile()
        {
            Granted = new List<string>();
        }

        public bool Has(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            foreach (var k in Granted)
            {
                if (string.Equals(k, key, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public void Grant(string key)
        {
            if (string.IsNullOrEmpty(key) || Has(key)) return;
            Granted.Add(key);
            UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        public void Revoke(string key)
        {
            for (int i = Granted.Count - 1; i >= 0; i--)
            {
                if (string.Equals(Granted[i], key, StringComparison.Ordinal)) Granted.RemoveAt(i);
            }
            UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public sealed class McpServerConfig
    {
        [JsonProperty("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonProperty("name")] public string Name { get; set; } = "";
        /// <summary>目前只支持 http。</summary>
        [JsonProperty("transport")] public string Transport { get; set; } = "http";
        [JsonProperty("url")] public string Url { get; set; } = "";
        [JsonProperty("headers")] public Dictionary<string, string> Headers { get; set; }
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
        /// <summary>上次拉取到的工具名，供设置页展示与工具列表过滤。</summary>
        [JsonProperty("tools")] public List<string> Tools { get; set; }
        [JsonProperty("toolDescriptions")] public Dictionary<string, string> ToolDescriptions { get; set; } = new Dictionary<string, string>();
        [JsonProperty("toolSchemas")] public Dictionary<string, string> ToolSchemas { get; set; } = new Dictionary<string, string>();
        [JsonProperty("disabledTools")] public List<string> DisabledTools { get; set; } = new List<string>();

        public McpServerConfig()
        {
            Headers = new Dictionary<string, string>();
            Tools = new List<string>();
        }
    }

    public sealed class SkillDefinition
    {
        [JsonProperty("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("description")] public string Description { get; set; } = "";
        [JsonProperty("content")] public string Content { get; set; } = "";
        /// <summary>prefix | system | replace</summary>
        [JsonProperty("mode")] public string Mode { get; set; } = "prefix";
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
    }
}
