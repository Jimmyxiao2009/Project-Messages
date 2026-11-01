// Core/Providers/ModelMetadata.cs —— 模型元数据与模型目录
//
// 解决的问题：用户要「按 provider 分组看到所有模型，并知道每个模型的窗口与能力」。
// 光从 /v1/models 拿到的往往只有一个 id（OpenAI 连窗口都不给），所以这里做三层合并：
//
//   手动覆盖（用户自己标） > 服务端返回（OpenRouter 这类会给全） > 内置元数据表（按名字推断）
//
// 内置表用「子串匹配 + 最长模式优先」。这一点很关键：
// 旧实现是"表里第一个命中就返回"，于是 gpt-4 会抢在 gpt-4o-mini 前面命中，
// 导致 mini 被当成 8k 窗口的老 gpt-4。现在取最长命中的那个模式。
//
// 能力一律用三态（true / false / null 未知）。宁可显示"未知，可手动指定"，
// 也不要猜一个错的能力出来 —— 猜错会让用户在发送后才发现不支持图片。

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Yanshuai.Messages.Core
{
    public enum MetadataSource
    {
        Unknown = 0,
        BuiltIn = 1,
        Server = 2,
        Manual = 3
    }

    public sealed class ModelMetadata
    {
        public string ModelId { get; set; } = "";
        public string DisplayName { get; set; } = "";

        /// <summary>0 表示未知。</summary>
        public int ContextWindow { get; set; }
        public int MaxOutputTokens { get; set; }
        public List<string> InputModalities { get; set; } = new List<string>();
        public List<string> OutputModalities { get; set; } = new List<string>();
        public List<string> SupportedParameters { get; set; } = new List<string>();

        /// <summary>三态：true 支持 / false 不支持 / null 未知。</summary>
        public bool? Vision { get; set; }
        public bool? Reasoning { get; set; }
        public bool? Tools { get; set; }

        /// <summary>0 未知 1 便宜 2 中等 3 贵。只用于界面排序与提示，不做计算。</summary>
        public int PriceTier { get; set; }

        public MetadataSource Source { get; set; } = MetadataSource.Unknown;

        /// <summary>命中的模式或服务端字段，用于在界面上说明"这个数是从哪来的"。</summary>
        public string Origin { get; set; } = "";

        public bool IsFullyUnknown
        {
            get { return ContextWindow == 0 && MaxOutputTokens == 0 && Vision == null && Reasoning == null && Tools == null; }
        }

        /// <summary>是否有任何一项是猜的（界面据此提示"部分能力未知"）。</summary>
        public bool IsPartial
        {
            get { return ContextWindow == 0 || Vision == null; }
        }

        public string WindowLabel
        {
            get
            {
                if (ContextWindow <= 0) return "窗口未知";
                if (ContextWindow >= 1000000) return (ContextWindow / 1000000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
                if (ContextWindow >= 1000) return (ContextWindow / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k";
                return ContextWindow.ToString(CultureInfo.InvariantCulture);
            }
        }

        public string PriceLabel
        {
            get
            {
                switch (PriceTier)
                {
                    case 1: return "$";
                    case 2: return "$$";
                    case 3: return "$$$";
                    default: return "";
                }
            }
        }

        public ModelMetadata Clone()
        {
            return new ModelMetadata
            {
                ModelId = ModelId,
                DisplayName = DisplayName,
                ContextWindow = ContextWindow,
                MaxOutputTokens = MaxOutputTokens,
                InputModalities = new List<string>(InputModalities ?? new List<string>()),
                OutputModalities = new List<string>(OutputModalities ?? new List<string>()),
                SupportedParameters = new List<string>(SupportedParameters ?? new List<string>()),
                Vision = Vision,
                Reasoning = Reasoning,
                Tools = Tools,
                PriceTier = PriceTier,
                Source = Source,
                Origin = Origin
            };
        }

        /// <summary>
        /// 用 override 覆盖 this 里"已知"的字段。
        /// 规则：override 给了值就覆盖；override 是 null/0 表示"它也不知道"，保留原值。
        /// </summary>
        public void ApplyOverride(ModelMetadata other)
        {
            if (other == null) return;

            if (other.ContextWindow > 0) ContextWindow = other.ContextWindow;
            if (other.MaxOutputTokens > 0) MaxOutputTokens = other.MaxOutputTokens;
            if (other.InputModalities != null && other.InputModalities.Count > 0) InputModalities = new List<string>(other.InputModalities);
            if (other.OutputModalities != null && other.OutputModalities.Count > 0) OutputModalities = new List<string>(other.OutputModalities);
            if (other.SupportedParameters != null && other.SupportedParameters.Count > 0) SupportedParameters = new List<string>(other.SupportedParameters);
            if (other.Vision.HasValue) Vision = other.Vision;
            if (other.Reasoning.HasValue) Reasoning = other.Reasoning;
            if (other.Tools.HasValue) Tools = other.Tools;
            if (other.PriceTier > 0) PriceTier = other.PriceTier;

            if (!string.IsNullOrEmpty(other.DisplayName)) DisplayName = other.DisplayName;
            if (!string.IsNullOrEmpty(other.Origin)) Origin = other.Origin;
            Source = other.Source;
        }
    }

    /// <summary>内置元数据表：按模型名子串匹配。</summary>
    public static class ModelMetadataTable
    {
        /// <summary>
        /// 格式：`模式|窗口|视觉|推理|工具|价格档[|排除词,排除词][|权重]`
        ///   窗口：0 = 未知
        ///   视觉/推理/工具：1 = 支持，0 = 不支持，- = 未知
        ///   价格档：0 未知 / 1 便宜 / 2 中等 / 3 贵
        ///   排除词：命中任意一个就跳过这条（解决家族重叠，例如
        ///           deepseek-r1-distill-qwen-7b 会同时命中 deepseek 与 qwen 两个家族）
        ///   权重：命中时额外加的分，用于在"模式长度相近"时强行指定优先级
        ///
        /// 匹配是打分制，不是"第一个命中就返回"：得分 = 模式长度 + 权重，
        /// 取最高分的条目。这样长模式天然压过家族兜底。
        /// </summary>
        private static readonly string[] Raw = new string[]
        {
            // ── 蒸馏版 / 小参数版：必须先于家族条目被判定，否则会被父模型抢走元数据。
            //    例如 deepseek-r1-distill-qwen-7b 同时含 "deepseek-r1" 与 "qwen"，
            //    用排除词把它从两个家族里摘出来，单独给一份更贴切的元数据。
            "distill|0|-|1|0|1||40",
            "r1-distill|0|0|1|0|1||60",
            "deepseek-r1-distill|0|0|1|0|1||80",

            // ── 视觉专用模型（不要被文本家族抢走） ──
            "vl-|0|1|0|0|1||30",
            "-vl|0|1|0|0|1||30",
            "vision|0|1|0|1|1||25",

            // ── 推理专用 ──
            "-thinking|0|0|1|1|1||35",
            "thinking|0|0|1|1|1||20",
            "-reasoner|0|0|1|1|1||35",

            // ── OpenAI ──
            "gpt-4o-mini|128000|1|0|1|1",
            "gpt-4o|128000|1|0|1|2",
            "gpt-4.1-mini|1047576|1|0|1|1",
            "gpt-4.1-nano|1047576|1|0|1|1",
            "gpt-4.1|1047576|1|0|1|2",
            "gpt-4-turbo|128000|1|0|1|3",
            "gpt-4-32k|32768|0|0|1|3",
            "gpt-4|8192|0|0|1|3",
            "gpt-3.5-turbo|16385|0|0|1|1",
            "o4-mini|200000|1|1|1|2",
            "o3-mini|200000|0|1|1|2",
            "o3|200000|1|1|1|3",
            "o1-mini|128000|0|1|0|2",
            "o1-preview|128000|0|1|0|3",
            "o1|200000|1|1|1|3",
            // 开源权重
            "gpt-oss-120b|131072|0|1|1|1",
            "gpt-oss-20b|131072|0|1|1|1",

            // ── Anthropic ──
            "claude-opus-4|200000|1|1|1|3",
            "claude-sonnet-4|200000|1|1|1|2",
            "claude-haiku-4|200000|1|1|1|1",
            "claude-3-7-sonnet|200000|1|1|1|2",
            "claude-3-5-sonnet|200000|1|0|1|2",
            "claude-3-5-haiku|200000|1|0|1|1",
            "claude-3-opus|200000|1|0|1|3",
            "claude-3-haiku|200000|1|0|1|1",
            "claude-3-sonnet|200000|1|0|1|2",
            // 家族兜底：任何没单列的 claude 变体（新版本、第三方中转改名）都落这里。
            // Anthropic 全系都支持图片与工具；是否推理按具体型号，用 - 表示未知。
            "claude|200000|1|-|1|2",

            // ── Google ──
            "gemini-2.5-pro|1048576|1|1|1|2",
            "gemini-2.5-flash-lite|1048576|1|1|1|1",
            "gemini-2.5-flash|1048576|1|1|1|1",
            "gemini-2.0-flash-thinking|1048576|1|1|1|1",
            "gemini-2.0-flash|1048576|1|0|1|1",
            "gemini-1.5-pro|2097152|1|0|1|2",
            "gemini-1.5-flash|1048576|1|0|1|1",
            "gemma-3|131072|1|0|0|1",
            "gemma-2|8192|0|0|0|1",
            "gemma|8192|0|0|0|1",

            // ── DeepSeek ──
            "deepseek-reasoner|128000|0|1|1|1",
            "deepseek-chat|128000|0|0|1|1",
            "deepseek-v3|64000|0|0|1|1",
            "deepseek-r1|64000|0|1|0|1",
            "deepseek-coder|64000|0|0|1|1",
            "deepseek|64000|-|-|1|1",

            // ── 通义千问 ──
            "qwen3-coder|262144|0|0|1|1",
            "qwen3|131072|0|1|1|1",
            "qwen2.5-vl|32768|1|0|0|1",
            "qwen2.5-coder|131072|0|0|1|1",
            "qwen2.5|131072|0|0|1|1",
            "qwen-vl-max|32768|1|0|0|2",
            "qwen-vl|32768|1|0|0|1",
            "qwen-max|32768|0|0|1|2",
            "qwen-plus|131072|0|0|1|1",
            "qwen-turbo|131072|0|0|1|1",
            "qwen-long|10485760|0|0|1|1",
            "qwen|32768|-|-|1|1",
            "qwq|131072|0|1|0|1",

            // ── 智谱 GLM ──
            "glm-4.5|128000|0|1|1|2",
            "glm-4-plus|128000|0|0|1|2",
            "glm-4-flash|128000|0|0|1|1",
            "glm-4v|8192|1|0|0|1",
            "glm-4|128000|0|0|1|2",
            "glm|32768|-|-|1|1",

            // ── Moonshot / Kimi ──
            "moonshot-v1-128k|131072|0|0|1|2",
            "moonshot-v1-32k|32768|0|0|1|1",
            "moonshot-v1-8k|8192|0|0|1|1",
            "moonshot|32768|-|-|1|1",

            // ── Meta Llama ──
            "llama-4-maverick|1048576|1|0|1|2",
            "llama-4-scout|1048576|1|0|1|1",
            "llama-3.3-70b|131072|0|0|1|1",
            "llama-3.2-vision|131072|1|0|1|1",
            "llama-3.2|131072|0|0|1|1",
            "llama-3.1-405b|131072|0|0|1|3",
            "llama-3.1-70b|131072|0|0|1|2",
            "llama-3.1-8b|131072|0|0|1|1",
            "llama-3|8192|0|0|1|1",
            "llama|8192|0|0|1|1",

            // ── Mistral ──
            "pixtral|131072|1|0|1|1",
            "mistral-large|131072|0|0|1|2",
            "mistral-small|32768|0|0|1|1",
            "mistral-nemo|131072|0|0|1|1",
            "mistral|32768|0|0|1|1",
            "mixtral|32768|0|0|1|1",

            // ── xAI ──
            "grok-4|256000|1|1|1|3",
            "grok-3-mini|131072|0|1|1|1",
            "grok-3|131072|0|0|1|3",
            "grok-2-vision|32768|1|0|1|2",
            "grok|32768|-|-|1|2",

            // ── 字节豆包 / 火山 ──
            "doubao-1.5-vision|0|1|0|1|1",
            "doubao-1.5-pro|0|0|1|1|1",
            "doubao-pro|0|-|-|1|1",
            "doubao|0|-|-|1|1",

            // ── 其它国内 ──
            "yi-large|32768|0|0|1|2",
            "yi-vision|32768|1|0|0|2",
            "yi|8192|-|-|1|1",
            "minimax-text|0|0|0|1|1",
            "abab|0|-|-|1|1",
            "baichuan|32768|-|-|1|1",
            "spark|8192|-|-|1|1",
            "hunyuan|32768|-|-|1|1",
            "ernie|32768|-|-|1|1",
            "step-1|32768|-|-|1|1",

            // ── 其它开源 ──
            "phi-4|16384|0|0|1|1",
            "phi-3|131072|0|0|0|1",
            "phi|4096|0|0|0|1",
            "command-r-plus|131072|0|0|1|2",
            "command-r|131072|0|0|1|1",
            "hermes|32768|-|-|1|1",
            "dolphin|32768|-|-|1|1",
            "nous|32768|-|-|0|1",
            "vicuna|4096|0|0|0|1",
            "openchat|8192|0|0|0|1",
            "zephyr|32768|0|0|0|1",
            "solar|32768|0|0|0|1",
            "wizardlm|16384|0|0|0|1",
            "deepseek-vl|4096|1|0|0|1"
        };

        private sealed class Entry
        {
            public string Pattern;
            public int Window;
            public bool? Vision;
            public bool? Reasoning;
            public bool? Tools;
            public int PriceTier;
            public string[] Exclusions;
            public int Weight;
            /// <summary>得分 = 模式长度 + 权重。</summary>
            public int Score;
        }

        private static List<Entry> _entries;

        private static List<Entry> EnsureLoaded()
        {
            if (_entries != null) return _entries;

            var list = new List<Entry>(Raw.Length);
            foreach (var line in Raw)
            {
                var parts = line.Split('|');
                if (parts.Length < 6) continue;

                var entry = new Entry
                {
                    Pattern = parts[0],
                    Window = ParseInt(parts[1]),
                    Vision = ParseTri(parts[2]),
                    Reasoning = ParseTri(parts[3]),
                    Tools = ParseTri(parts[4]),
                    PriceTier = ParseInt(parts[5]),
                    Exclusions = SplitExclusions(parts.Length > 6 ? parts[6] : ""),
                    Weight = parts.Length > 7 ? ParseInt(parts[7]) : 0
                };
                entry.Score = entry.Pattern.Length + entry.Weight;
                list.Add(entry);
            }

            _entries = list;
            return _entries;
        }

        private static string[] SplitExclusions(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new string[0];

            var parts = text.Split(',');
            var list = new List<string>(parts.Length);
            foreach (var part in parts)
            {
                var value = part.Trim().ToLowerInvariant();
                if (value.Length > 0) list.Add(value);
            }
            return list.ToArray();
        }

        private static int ParseInt(string text)
        {
            int value;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static bool? ParseTri(string text)
        {
            if (text == "1") return true;
            if (text == "0") return false;
            return null;
        }

        public static int Count
        {
            get { return EnsureLoaded().Count; }
        }

        /// <summary>
        /// 打分式匹配：取「模式长度 + 权重」最高的条目，命中排除词的条目直接跳过。
        /// 返回 null 表示表里没有这个模型的任何线索。
        /// </summary>
        public static ModelMetadata Lookup(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId)) return null;

            var name = modelId.Trim().ToLowerInvariant();
            var entries = EnsureLoaded();

            Entry best = null;
            foreach (var entry in entries)
            {
                if (name.IndexOf(entry.Pattern, StringComparison.Ordinal) < 0) continue;

                // 排除词：命中任意一个就跳过这条
                bool excluded = false;
                for (int i = 0; i < entry.Exclusions.Length; i++)
                {
                    if (name.IndexOf(entry.Exclusions[i], StringComparison.Ordinal) >= 0)
                    {
                        excluded = true;
                        break;
                    }
                }
                if (excluded) continue;

                if (best == null || entry.Score > best.Score)
                {
                    best = entry;
                }
            }

            if (best == null) return null;

            return new ModelMetadata
            {
                ModelId = modelId,
                DisplayName = modelId,
                ContextWindow = best.Window,
                Vision = best.Vision,
                Reasoning = best.Reasoning,
                Tools = best.Tools,
                PriceTier = best.PriceTier,
                Source = MetadataSource.BuiltIn,
                Origin = "内置表：" + best.Pattern
            };
        }

        /// <summary>给一个模型名，返回合并了内置表的元数据（拿不到线索时返回"全未知"对象）。</summary>
        public static ModelMetadata Resolve(string modelId)
        {
            var metadata = Lookup(modelId);
            if (metadata != null) return metadata;

            return new ModelMetadata
            {
                ModelId = modelId ?? "",
                DisplayName = modelId ?? "",
                Source = MetadataSource.Unknown,
                Origin = "未知"
            };
        }
    }

    /// <summary>模型选择器里的一行：模型 + 它属于哪个 provider。</summary>
    public sealed class ModelCatalogEntry
    {
        public string ApiProfileId { get; set; } = "";
        public string ApiProfileName { get; set; } = "";
        public string Provider { get; set; } = "openai";
        public ModelMetadata Metadata { get; set; }
        public bool IsDefault { get; set; }

        public string ModelId { get { return Metadata != null ? Metadata.ModelId : ""; } }

        /// <summary>能力徽标串，界面直接显示，不用自己拼。</summary>
        public string Badges
        {
            get
            {
                if (Metadata == null) return "";

                var parts = new List<string>();
                parts.Add(Metadata.Vision == true ? "图片" : Metadata.Vision == false ? "无图片" : "图片未知");
                parts.Add(Metadata.Reasoning == true ? "推理" : Metadata.Reasoning == false ? "无推理" : "推理未知");
                parts.Add(Metadata.Tools == true ? "工具" : Metadata.Tools == false ? "无工具" : "工具未知");
                parts.Add(Metadata.WindowLabel);
                var price = Metadata.PriceLabel;
                if (!string.IsNullOrEmpty(price)) parts.Add(price);

                return string.Join(" · ", parts.ToArray());
            }
        }
    }

    public sealed class ModelCatalogView
    {
        public List<ModelCatalogEntry> Entries { get; set; }
        /// <summary>按 provider 名分组后的结果，界面直接按这个渲染。</summary>
        public List<KeyValuePair<string, List<ModelCatalogEntry>>> Groups { get; set; }

        public ModelCatalogView()
        {
            Entries = new List<ModelCatalogEntry>();
            Groups = new List<KeyValuePair<string, List<ModelCatalogEntry>>>();
        }

        public int Count { get { return Entries.Count; } }
    }

    /// <summary>
    /// 组装"所有 provider 下的所有模型"。数据来源是各 provider 上次拉取缓存的模型列表
    /// （ApiProfile.CachedModels）；某个 provider 还没拉过就只列出它当前的模型名。
    /// </summary>
    public static class ModelCatalogBuilder
    {
        public static ModelCatalogView Build(
            ApiProfileFile profiles,
            string selectedApiProfileId,
            string selectedModelId)
        {
            var view = new ModelCatalogView();
            if (profiles == null || profiles.Items == null) return view;

            foreach (var api in profiles.Items)
            {
                if (api == null) continue;

                var ids = new List<string>();
                if (api.CachedModels != null)
                {
                    foreach (var id in api.CachedModels)
                    {
                        if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id)) ids.Add(id);
                    }
                }
                if (api.ManualModels != null) foreach (var id in api.ManualModels)
                    if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id)) ids.Add(id);
                if (api.Id == selectedApiProfileId && !string.IsNullOrWhiteSpace(selectedModelId) && !ids.Contains(selectedModelId)) ids.Add(selectedModelId);

                // 没拉过列表也要能看到当前在用的那个模型
                if (!string.IsNullOrWhiteSpace(api.Model) && !ids.Contains(api.Model)) ids.Add(api.Model);
                if (!string.IsNullOrWhiteSpace(api.SubModel) && !ids.Contains(api.SubModel)) ids.Add(api.SubModel);

                if (ids.Count == 0) continue;

                var group = new List<ModelCatalogEntry>();
                foreach (var id in ids)
                {
                    // 用 provider 自己的解析：缓存（手动/服务端）优先，其次内置表
                    var metadata = api.ResolveMetadata(id);
                    if (metadata != null && string.IsNullOrEmpty(metadata.DisplayName)) metadata.DisplayName = id;
                    group.Add(new ModelCatalogEntry
                    {
                        ApiProfileId = api.Id,
                        ApiProfileName = string.IsNullOrEmpty(api.Name) ? api.Id : api.Name,
                        Provider = api.Provider,
                        Metadata = metadata,
                        IsDefault = string.Equals(api.Id, selectedApiProfileId, StringComparison.Ordinal)
                                 && string.Equals(id, selectedModelId, StringComparison.Ordinal)
                    });
                }

                // 组内排序：当前在用的永远在最上（用户一进来就能看到自己在用哪个），
                // 然后能力已知的在前，最后按名字。
                group.Sort(delegate (ModelCatalogEntry a, ModelCatalogEntry b)
                {
                    if (a.IsDefault != b.IsDefault) return a.IsDefault ? -1 : 1;

                    bool aUnknown = a.Metadata == null || a.Metadata.IsFullyUnknown;
                    bool bUnknown = b.Metadata == null || b.Metadata.IsFullyUnknown;
                    if (aUnknown != bUnknown) return aUnknown ? 1 : -1;

                    return string.Compare(a.ModelId, b.ModelId, StringComparison.OrdinalIgnoreCase);
                });

                view.Entries.AddRange(group);
                view.Groups.Add(new KeyValuePair<string, List<ModelCatalogEntry>>(
                    string.IsNullOrEmpty(api.Name) ? api.Id : api.Name, group));
            }

            return view;
        }
    }
}
