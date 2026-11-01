// tests/CoreTests/ModelTests.cs —— 模型元数据与模型目录
//
// 这组测试存在的理由：模型元数据"猜错"的代价很高 ——
// 界面告诉用户"支持图片"，用户发了图才发现不支持；或者反过来，明明支持却标成未知。
// 所以三态（true/false/未知）与"最长模式优先"这两条必须有测试钉住。

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tests
{
    public static class ModelTests
    {
        public static void Register()
        {
            // ═════════════ 内置元数据表 ═════════════
            Runner.Add("模型元数据", "最长模式优先：gpt-4o-mini 不该被 gpt-4 抢走", delegate
            {
                var mini = ModelMetadataTable.Lookup("gpt-4o-mini");
                Check.True(mini != null, "应当命中");
                Check.EqualInt(128000, mini.ContextWindow, "mini 的窗口");
                Check.Contains(mini.Origin, "gpt-4o-mini", "命中的是具体型号而不是家族");

                var plain = ModelMetadataTable.Lookup("gpt-4");
                Check.EqualInt(8192, plain.ContextWindow, "老 gpt-4 是小窗口");

                var turbo = ModelMetadataTable.Lookup("gpt-4-turbo");
                Check.EqualInt(128000, turbo.ContextWindow, "turbo 命中自己的条目");
            });

            Runner.Add("模型元数据", "带后缀的型号也能命中", delegate
            {
                Check.EqualInt(128000, ModelMetadataTable.Lookup("deepseek-chat-0324").ContextWindow, "带日期后缀");
                Check.EqualInt(200000, ModelMetadataTable.Lookup("claude-3-5-sonnet-20241022").ContextWindow, "带版本后缀");
                Check.EqualInt(1048576, ModelMetadataTable.Lookup("gemini-2.5-flash-preview-09-2025").ContextWindow, "带 preview 后缀");
            });

            Runner.Add("模型元数据", "家族兜底：不认识的变体落到家族条目", delegate
            {
                var claude = ModelMetadataTable.Lookup("claude-something-new");
                Check.True(claude != null, "应当命中 claude 家族");
                Check.EqualInt(200000, claude.ContextWindow, "家族窗口");
                Check.True(claude.Vision == true, "Anthropic 全系都支持图片");

                var llama = ModelMetadataTable.Lookup("llama-9-ultra");
                Check.True(llama != null, "应当命中 llama 家族");
            });

            Runner.Add("模型元数据", "能力是三态：未知就是 null 而不是猜 false", delegate
            {
                var doubao = ModelMetadataTable.Lookup("doubao-pro-32k");
                Check.True(doubao != null, "命中 doubao");
                Check.True(doubao.Vision == null, "模态未知应当是 null");
                Check.True(doubao.Reasoning == null, "推理未知应当是 null");
                Check.True(doubao.IsPartial, "标为部分未知");

                var gpt4o = ModelMetadataTable.Lookup("gpt-4o");
                Check.True(gpt4o.Vision == true, "4o 明确支持图片");
                Check.True(gpt4o.Tools == true, "4o 明确支持工具");
            });

            Runner.Add("模型元数据", "完全不认识时返回 unknown 而不是抛", delegate
            {
                Check.True(ModelMetadataTable.Lookup("this-model-does-not-exist-12345") == null, "表里没有");

                var resolved = ModelMetadataTable.Resolve("this-model-does-not-exist-12345");
                Check.Equal(MetadataSource.Unknown, resolved.Source, "来源未知");
                Check.True(resolved.IsFullyUnknown, "全未知");
                Check.Equal("窗口未知", resolved.WindowLabel, "窗口文案");
            });

            Runner.Add("模型元数据", "窗口与价格的显示文案", delegate
            {
                Check.Equal("128k", ModelMetadataTable.Resolve("deepseek-chat").WindowLabel, "千级");
                Check.Equal("1M", ModelMetadataTable.Resolve("gemini-1.5-flash").WindowLabel, "百万级");
                Check.Equal("8k", ModelMetadataTable.Resolve("gpt-4").WindowLabel, "8k");

                Check.Equal("$", ModelMetadataTable.Resolve("gpt-4o-mini").PriceLabel, "便宜档");
                Check.Equal("$$$", ModelMetadataTable.Resolve("gpt-4-turbo").PriceLabel, "贵档");
            });

            Runner.Add("模型元数据", "内置表规模合理", delegate
            {
                Check.True(ModelMetadataTable.Count >= 80, "至少 80 条，实际 " + ModelMetadataTable.Count);
            });

            // ═════════════ 服务端元数据解析 ═════════════
            Runner.Add("模型元数据", "OpenRouter 形态：字段全给时全部用上", delegate
            {
                var json = @"{
                  ""id"": ""anthropic/claude-3.5-sonnet"",
                  ""name"": ""Claude 3.5 Sonnet"",
                  ""context_length"": 200000,
                  ""pricing"": { ""prompt"": ""0.000003"", ""completion"": ""0.000015"" },
                  ""architecture"": { ""modality"": ""text+image->text"" },
                  ""supported_parameters"": [""tools"", ""tool_choice"", ""reasoning""]
                }";

                var metadata = ModelCatalog.ParseServerEntry(JObject.Parse(json));

                Check.True(metadata != null, "解析成功");
                Check.EqualInt(200000, metadata.ContextWindow, "窗口来自服务端");
                Check.True(metadata.Vision == true, "模态来自 architecture");
                Check.True(metadata.Tools == true, "能力来自 supported_parameters");
                Check.True(metadata.Reasoning == true, "推理来自 supported_parameters");
                Check.EqualInt(3, metadata.PriceTier, "价格档按每 token 单价推");
                Check.Equal(MetadataSource.Server, metadata.Source, "来源标为服务端");
                Check.Contains(metadata.Origin, "服务端", "来源可追溯");
            });

            Runner.Add("模型元数据", "OpenAI 形态：只有 id 就落到内置表", delegate
            {
                var metadata = ModelCatalog.ParseServerEntry(JObject.Parse(@"{""id"":""deepseek-chat"",""created"":1730000000}"));

                Check.EqualInt(128000, metadata.ContextWindow, "窗口来自内置表");
                Check.Equal(MetadataSource.BuiltIn, metadata.Source, "来源标为内置表");
            });

            Runner.Add("模型元数据", "服务端给了窗口但没给模态时，模态由内置表兜底而不是变未知", delegate
            {
                var json = @"{""id"":""gpt-4o"",""context_length"":128000}";
                var metadata = ModelCatalog.ParseServerEntry(JObject.Parse(json));

                Check.EqualInt(128000, metadata.ContextWindow, "窗口来自服务端");
                Check.True(metadata.Vision == true, "模态由内置表兜底（不能因为服务端没给就丢）");
                Check.Equal(MetadataSource.Server, metadata.Source, "但来源仍是服务端");
            });

            Runner.Add("模型元数据", "完全不认识的模型解析成未知而不是报错", delegate
            {
                var metadata = ModelCatalog.ParseServerEntry(JObject.Parse(@"{""id"":""unknown-model-abc""}"));

                Check.Equal("unknown-model-abc", metadata.ModelId, "id 保留");
                Check.True(metadata.IsFullyUnknown, "全未知");
                Check.Equal(MetadataSource.Unknown, metadata.Source, "来源未知");
            });

            Runner.Add("模型元数据", "字符串数组形态的模型列表也能解析", delegate
            {
                var metadata = ModelCatalog.ParseServerEntry(JValue.CreateString("gpt-4o-mini"));
                Check.Equal("gpt-4o-mini", metadata.ModelId, "字符串当 id");
                Check.EqualInt(128000, metadata.ContextWindow, "仍能查到内置元数据");
            });

            Runner.Add("模型元数据", "畸形条目返回 null 而不是抛", delegate
            {
                Check.True(ModelCatalog.ParseServerEntry(null) == null, "null 输入");
                Check.True(ModelCatalog.ParseServerEntry(new JObject()) == null, "没有 id");
            });

            // ═════════════ 档案侧缓存与优先级 ═════════════
            Runner.Add("模型元数据", "档案缓存优先于内置表", delegate
            {
                var api = new ApiProfile { Name = "测试" };
                api.ModelMetadataCache["deepseek-chat"] = new ModelMetadata
                {
                    ModelId = "deepseek-chat",
                    ContextWindow = 64000,
                    Vision = true,
                    Source = MetadataSource.Manual,
                    Origin = "手动"
                };

                var resolved = api.ResolveMetadata("deepseek-chat");
                Check.EqualInt(64000, resolved.ContextWindow, "用缓存里的值");
                Check.True(resolved.Vision == true, "手动标了支持图片");
                Check.Equal(MetadataSource.Manual, resolved.Source, "来源是手动");
            });

            Runner.Add("模型元数据", "缓存里只有来源、字段全空时回落到内置表", delegate
            {
                var api = new ApiProfile { Name = "测试" };
                api.ModelMetadataCache["gpt-4o"] = new ModelMetadata
                {
                    ModelId = "gpt-4o",
                    Source = MetadataSource.Server,
                    Origin = "服务端：无有效字段"
                };

                var resolved = api.ResolveMetadata("gpt-4o");
                Check.EqualInt(128000, resolved.ContextWindow, "回落到内置表的窗口");
                Check.True(resolved.Vision == true, "回落到内置表的模态");
                Check.Equal(MetadataSource.Server, resolved.Source, "但保留服务端这个来源标记");
            });

            // ═════════════ 模型目录 ═════════════
            Runner.Add("模型目录", "按 provider 分组并带上能力徽标", delegate
            {
                var profiles = new ApiProfileFile();
                var openrouter = new ApiProfile { Id = "or", Name = "OpenRouter", Provider = "openai" };
                openrouter.CachedModels.AddRange(new[] { "anthropic/claude-3.5-sonnet", "openai/gpt-4o", "deepseek/deepseek-chat" });
                profiles.Items.Add(openrouter);

                var qwen = new ApiProfile { Id = "qw", Name = "通义", Provider = "openai", Model = "qwen-max" };
                profiles.Items.Add(qwen);

                var view = ModelCatalogBuilder.Build(profiles, "or", "openai/gpt-4o");

                Check.EqualInt(4, view.Count, "3 + 1 个模型");
                Check.EqualInt(2, view.Groups.Count, "两个分组");
                Check.Equal("OpenRouter", view.Groups[0].Key, "第一个分组名");

                // 选中的那条要被标出来
                ModelCatalogEntry selected = null;
                foreach (var entry in view.Entries)
                {
                    if (entry.IsDefault) selected = entry;
                }
                Check.True(selected != null, "找到当前选中的模型");
                Check.Equal("openai/gpt-4o", selected.ModelId, "选中的就是它");
                Check.Contains(selected.Badges, "图片", "徽标里有图片能力");
                Check.Contains(selected.Badges, "128k", "徽标里有窗口");
            });

            Runner.Add("模型目录", "没拉过列表的 provider 也能显示当前在用的模型", delegate
            {
                var profiles = new ApiProfileFile();
                profiles.Items.Add(new ApiProfile { Id = "x", Name = "本地", Model = "local-model-7b", SubModel = "embed-model" });

                var view = ModelCatalogBuilder.Build(profiles, "x", "local-model-7b");

                Check.EqualInt(2, view.Count, "当前模型 + 副模型");
                Check.Equal("local-model-7b", view.Entries[0].ModelId, "第一个是当前模型");
                Check.True(view.Entries[0].IsDefault, "标为选中");
                Check.True(view.Entries[0].Metadata.IsFullyUnknown, "元数据未知，不瞎猜");
            });

            Runner.Add("模型目录", "空 provider 不产空分组", delegate
            {
                var profiles = new ApiProfileFile();
                profiles.Items.Add(new ApiProfile { Id = "empty", Name = "空配置" });

                var view = ModelCatalogBuilder.Build(profiles, "", "");
                Check.EqualInt(0, view.Count, "没有模型");
                Check.EqualInt(0, view.Groups.Count, "也没有分组");
            });

            Runner.Add("模型目录", "能力已知的排在未知的前面", delegate
            {
                var profiles = new ApiProfileFile();
                var api = new ApiProfile { Id = "p", Name = "p" };
                api.CachedModels.AddRange(new[] { "totally-unknown-xyz", "gpt-4o-mini" });
                profiles.Items.Add(api);

                var view = ModelCatalogBuilder.Build(profiles, "p", "");

                Check.EqualInt(2, view.Count, "两个模型");
                Check.Equal("gpt-4o-mini", view.Entries[0].ModelId, "已知的排前面");
                Check.Equal("totally-unknown-xyz", view.Entries[1].ModelId, "未知的沉底");
            });
        }
    }
}
