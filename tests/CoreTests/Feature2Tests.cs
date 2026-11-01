// tests/CoreTests/Feature2Tests.cs —— v1.1 新增能力
//
// 覆盖：会话分支、正则转换器、元数据排除词匹配、Gemini 原生 provider、群聊决策。

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tests
{
    public static class Feature2Tests
    {
        public static void Register()
        {
            RegisterBranching();
            RegisterRegex();
            RegisterExclusion();
            RegisterGemini();
            RegisterGroupChat();
        }

        // ═════════════ 会话分支 ═════════════
        private static void RegisterBranching()
        {
            Runner.Add("分支", "未产生分支时一切照旧", delegate
            {
                var conversation = MakeConversation();
                Check.EqualInt(1, conversation.VariantCount(0), "默认一条候选");
                Check.EqualInt(0, conversation.VariantIndex(0), "默认下标 0");
                Check.EqualInt(0, conversation.Variants.Count, "候选池为空");
            });

            Runner.Add("分支", "重新生成产生新候选并自动选中", delegate
            {
                var conversation = MakeConversation();
                var original = conversation.Messages[0];

                var regenerated = new Message { Role = MessageRole.Assistant, Content = "第二版" };
                int index = conversation.AddVariant(0, regenerated);

                Check.EqualInt(1, index, "新候选下标为 1");
                Check.EqualInt(2, conversation.VariantCount(0), "两条候选");
                Check.EqualInt(1, conversation.VariantIndex(0), "当前选中新的那条");
                Check.Equal("第二版", conversation.Messages[0].Content, "Messages 里已换成新的");
                Check.Equal(original.Id, conversation.Variants[original.NodeId][0].Id, "原消息仍在候选池第 0 位");
                Check.Equal(original.NodeId, regenerated.NodeId, "共享同一个 NodeId");
            });

            Runner.Add("分支", "可以在候选之间来回切换", delegate
            {
                var conversation = MakeConversation();
                conversation.AddVariant(0, new Message { Role = MessageRole.Assistant, Content = "第二版" });

                Check.True(conversation.SelectVariant(0, 0), "切回第一版");
                Check.Equal("第一版", conversation.Messages[0].Content, "内容切回");
                Check.EqualInt(0, conversation.VariantIndex(0), "下标归零");

                Check.True(conversation.SelectVariant(0, 1), "再切到第二版");
                Check.Equal("第二版", conversation.Messages[0].Content, "内容再切");
            });

            Runner.Add("分支", "连续重生成会累积候选", delegate
            {
                var conversation = MakeConversation();
                conversation.AddVariant(0, new Message { Role = MessageRole.Assistant, Content = "第二版" });
                conversation.AddVariant(0, new Message { Role = MessageRole.Assistant, Content = "第三版" });

                Check.EqualInt(3, conversation.VariantCount(0), "三条候选");
                Check.EqualInt(2, conversation.VariantIndex(0), "选中最新的");
                Check.Equal("第三版", conversation.Messages[0].Content, "内容是最新的");
            });

            Runner.Add("分支", "非法下标不改变任何状态", delegate
            {
                var conversation = MakeConversation();
                Check.False(conversation.SelectVariant(0, 5), "越界候选");
                Check.False(conversation.SelectVariant(99, 0), "越界位置");
                Check.EqualInt(-1, conversation.AddVariant(99, new Message()), "越界追加返回 -1");
                Check.Equal("第一版", conversation.Messages[0].Content, "内容未变");
            });

            Runner.Add("分支", "分叉出独立会话且候选池不继承", delegate
            {
                var conversation = MakeConversation();
                conversation.AddVariant(0, new Message { Role = MessageRole.Assistant, Content = "第二版" });
                conversation.Messages.Add(new Message { Role = MessageRole.User, Content = "后续" });

                var fork = conversation.ForkAt(1, "分支会话");

                Check.EqualInt(2, fork.Messages.Count, "复制到该位置为止");
                Check.Equal("分支会话", fork.Title, "标题");
                Check.EqualInt(0, fork.Variants.Count, "候选池不继承（重新开始）");
                Check.EqualInt(1, fork.VariantCount(0), "分叉后每个位置只有一条");

                // Id 必须换新，否则两条会话共用消息 id 会互相干扰
                Check.False(fork.Messages[0].Id == conversation.Messages[0].Id, "消息 Id 换新");
            });

            Runner.Add("分支", "分叉标题自动去重", delegate
            {
                var existing = new List<string> { "对话", "对话(1)", "对话(2)" };
                Check.Equal("对话(3)", Conversation.ForkTitle("对话", existing), "接着编号");
                Check.Equal("新的", Conversation.ForkTitle("新的", existing), "没冲突就用原名");
                Check.Equal("对话", Conversation.ForkTitle("", new List<string>()), "空标题兜底");
            });

            Runner.Add("分支", "Repair 补齐老数据的 NodeId", delegate
            {
                var conversation = new Conversation();
                var message = new Message { Id = "m1", Role = MessageRole.User, Content = "老数据" };
                conversation.Messages.Add(message);
                conversation.Variants = null;

                conversation.Repair();

                Check.True(conversation.Variants != null, "候选池补齐");
                Check.Equal("m1", message.NodeId, "NodeId 用自身 Id 兜底");
            });
        }

        // ═════════════ 正则转换器 ═════════════
        private static void RegisterRegex()
        {
            Runner.Add("正则", "发送前替换生效", delegate
            {
                var transformer = new RegexTransformer();
                transformer.Load(new[]
                {
                    new RegexTransform { Find = @"\s*\[END\]$", Replace = "", Scope = "assistant" }
                });

                Check.Equal("正文", transformer.ApplyOutgoing("正文 [END]", false), "助手消息被清理");
                Check.Equal("正文 [END]", transformer.ApplyOutgoing("正文 [END]", true), "用户消息不受影响（作用域限定）");
            });

            Runner.Add("正则", "visualOnly 只改显示不改发送", delegate
            {
                var transformer = new RegexTransformer();
                transformer.Load(new[]
                {
                    new RegexTransform { Find = @"\*\*", Replace = "", VisualOnly = true }
                });

                Check.Equal("加粗", transformer.ApplyVisual("**加粗**", false), "显示时去掉标记");
                Check.Equal("**加粗**", transformer.ApplyOutgoing("**加粗**", false), "发送时保留原样");
            });

            Runner.Add("正则", "捕获组引用可用", delegate
            {
                var transformer = new RegexTransformer();
                transformer.Load(new[]
                {
                    new RegexTransform { Find = @"\{char\}", Replace = "林深" }
                });

                Check.Equal("你好林深", transformer.ApplyOutgoing("你好{char}", true), "替换占位符");
            });

            Runner.Add("正则", "写错的正则被跳过，不影响其它规则", delegate
            {
                var transformer = new RegexTransformer();
                transformer.Load(new[]
                {
                    new RegexTransform { Find = "([unclosed", Replace = "x" },
                    new RegexTransform { Find = "abc", Replace = "XYZ" }
                });

                Check.Equal("XYZ", transformer.ApplyOutgoing("abc", true), "好的规则仍然生效");
            });

            Runner.Add("正则", "校验能指出问题", delegate
            {
                Check.True(RegexTransformer.Validate(new RegexTransform { Find = "" }) != null, "空模式");
                Check.True(RegexTransformer.Validate(new RegexTransform { Find = "([bad" }) != null, "坏正则");
                Check.True(RegexTransformer.Validate(new RegexTransform { Find = "ok", Scope = "nobody" }) != null, "非法作用域");
                Check.True(RegexTransformer.Validate(new RegexTransform { Find = "ok", Scope = "both" }) == null, "合法规则");
            });

            Runner.Add("正则", "未启用与无规则的转换器是零成本", delegate
            {
                var transformer = new RegexTransformer();
                transformer.Load(new[] { new RegexTransform { Find = "a", Replace = "b", Enabled = false } });

                Check.EqualInt(0, transformer.RuleCount, "禁用的不加载");
                Check.False(transformer.HasVisualRules, "没有 visual 规则");
                Check.Equal("abc", transformer.ApplyVisual("abc", true), "直接返回原串");
            });
        }

        // ═════════════ 元数据排除词 ═════════════
        private static void RegisterExclusion()
        {
            Runner.Add("元数据排除词", "蒸馏版不被父模型抢走", delegate
            {
                var distill = ModelMetadataTable.Lookup("deepseek-r1-distill-qwen-7b");
                Check.True(distill != null, "命中");
                Check.True(distill.Reasoning == true, "蒸馏版仍是推理模型");
                Check.True(distill.Vision == false, "不支持图片");
                Check.Contains(distill.Origin, "distill", "命中的是蒸馏条目");
            });

            Runner.Add("元数据排除词", "视觉专用模型不被文本家族抢走", delegate
            {
                var vl = ModelMetadataTable.Lookup("qwen2.5-vl-72b");
                Check.True(vl.Vision == true, "识别为视觉模型");

                var qwenText = ModelMetadataTable.Lookup("qwen2.5-72b");
                Check.True(qwenText.Vision == false, "纯文本版不支持图片");
            });

            Runner.Add("元数据排除词", "带 thinking 的变体识别为推理模型", delegate
            {
                var thinking = ModelMetadataTable.Lookup("qwen3-32b-thinking");
                Check.True(thinking.Reasoning == true, "推理能力");

                var plain = ModelMetadataTable.Lookup("qwen3-32b");
                Check.True(plain.Reasoning == true, "qwen3 本身就带推理");
            });
        }

        // ═════════════ Gemini ═════════════
        private static void RegisterGemini()
        {
            Runner.Add("Gemini", "端点推导：base / 占位符 / 完整 URL 都能认", delegate
            {
                Check.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent",
                    GeminiChatProvider.BuildEndpoint(null, "gemini-2.5-flash", false), "从默认 base 拼");

                Check.Equal("https://x.com/v1beta/models/gemini-2.5-pro:generateContent",
                    GeminiChatProvider.BuildEndpoint("https://x.com/v1beta", "gemini-2.5-pro", false), "自定义 base");

                Check.Equal("https://x.com/v1beta/models/gemini-2.5-pro:streamGenerateContent",
                    GeminiChatProvider.BuildEndpoint("https://x.com/v1beta", "gemini-2.5-pro", true), "流式方法名");

                Check.Equal("https://x.com/v1beta/models/gemini-2.5-pro:generateContent",
                    GeminiChatProvider.BuildEndpoint("https://x.com/v1beta/models/{model}:generateContent", "gemini-2.5-pro", false),
                    "完整 URL 里的 {model} 要替换成真实模型名");

                Check.Equal("https://x.com/v1beta/models/gemini-2.5-pro:generateContent",
                    GeminiChatProvider.BuildEndpoint("https://x.com/v1beta/models/gemini-2.5-pro:generateContent", "gemini-2.5-pro", false),
                    "已经写死模型名的完整 URL 原样使用");

                Check.Equal("https://x.com/v1beta/models/gemini-2.5-pro:generateContent",
                    GeminiChatProvider.BuildEndpoint("https://x.com/v1beta/models/{model}", "gemini-2.5-pro", false), "占位符替换");
            });

            Runner.Add("Gemini", "模型列表端点去掉方法后缀", delegate
            {
                Check.Equal("https://generativelanguage.googleapis.com/v1beta/models",
                    GeminiChatProvider.BuildModelsEndpoint("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent"),
                    "从完整 URL 反推");
            });

            Runner.Add("Gemini", "请求体：system 顶层、角色用 model、图片 inlineData", delegate
            {
                var request = new ChatRequest { Api = MakeGeminiApi(), VisionEnabled = true };
                request.Turns.Add(ChatTurn.System("你是助手"));
                var userTurn = ChatTurn.User("看这张图");
                userTurn.Images.Add(new ImagePart { MimeType = "image/png", Base64 = "QUJD" });
                request.Turns.Add(userTurn);

                var body = GeminiChatProvider.BuildBody(request, false);

                Check.Equal("你是助手", body["systemInstruction"]["parts"][0]["text"].ToString(), "system 提到顶层");

                var contents = (JArray)body["contents"];
                Check.EqualInt(1, contents.Count, "只有一条用户消息");
                Check.Equal("user", contents[0]["role"].ToString(), "角色是 user");
                Check.Equal("QUJD", contents[0]["parts"][1]["inlineData"]["data"].ToString(), "图片走 inlineData");
                Check.Equal("image/png", contents[0]["parts"][1]["inlineData"]["mimeType"].ToString(), "MIME 正确");

                foreach (var item in contents)
                {
                    Check.False(item["role"].ToString() == "system", "消息里不能有 system 角色");
                    Check.False(item["role"].ToString() == "assistant", "Gemini 不用 assistant，用 model");
                }
            });

            Runner.Add("Gemini", "助手消息角色是 model、工具声明用 functionDeclarations", delegate
            {
                var request = new ChatRequest { Api = MakeGeminiApi() };
                request.Turns.Add(ChatTurn.User("几点了"));
                var assistant = ChatTurn.Assistant("");
                assistant.ToolCalls.Add(new ToolCallRequest { Id = "c1", Name = "get_time", ArgumentsJson = "{}" });
                request.Turns.Add(assistant);
                request.Tools.Add(new ToolSchema { Name = "get_time", Description = "查时间" }
                    .Param("tz", "string", "时区", false));

                var body = GeminiChatProvider.BuildBody(request, false);
                var contents = (JArray)body["contents"];

                Check.Equal("model", contents[1]["role"].ToString(), "助手角色是 model");
                Check.Equal("get_time", contents[1]["parts"][0]["functionCall"]["name"].ToString(), "工具名");
                Check.True(contents[1]["parts"][0]["functionCall"]["args"] is JObject, "参数是对象");

                Check.Equal("get_time", body["tools"][0]["functionDeclarations"][0]["name"].ToString(), "工具声明");
            });

            Runner.Add("Gemini", "工具结果用 functionResponse 且响应体是对象", delegate
            {
                var request = new ChatRequest { Api = MakeGeminiApi() };
                request.Turns.Add(ChatTurn.User("几点了"));
                var assistant = ChatTurn.Assistant("");
                assistant.ToolCalls.Add(new ToolCallRequest { Id = "c1", Name = "get_time", ArgumentsJson = "{}" });
                request.Turns.Add(assistant);
                request.Turns.Add(ChatTurn.ToolResult("c1", "get_time", "现在是 15:00"));

                var contents = GeminiChatProvider.BuildContents(request);
                var resultTurn = contents[2];

                Check.Equal("user", resultTurn["role"].ToString(), "工具结果走 user 角色");
                var functionResponse = resultTurn["parts"][0]["functionResponse"];
                Check.Equal("get_time", functionResponse["name"].ToString(), "按工具名对应（Gemini 没有 call id）");
                Check.True(functionResponse["response"] is JObject, "response 必须是对象，传字符串会被 400");
                Check.Equal("现在是 15:00", functionResponse["response"]["result"].ToString(), "结果内容");
            });

            Runner.Add("Gemini", "响应解析：text / thought / functionCall / 用量", delegate
            {
                var json = @"{
                  ""candidates"": [ { ""content"": { ""parts"": [
                      { ""text"": ""先想想"", ""thought"": true },
                      { ""text"": ""现在是下午三点。"" },
                      { ""functionCall"": { ""name"": ""get_time"", ""args"": { ""tz"": ""+8"" } } }
                  ] } } ],
                  ""usageMetadata"": { ""promptTokenCount"": 20, ""candidatesTokenCount"": 9, ""cachedContentTokenCount"": 4, ""thoughtsTokenCount"": 6 }
                }";

                var result = GeminiChatProvider.ParseResponse(json);

                Check.True(result.Ok, "解析成功");
                Check.Equal("现在是下午三点。", result.Content, "正文（不含 thought）");
                Check.Equal("先想想", result.Reasoning, "思考单独归集");
                Check.EqualInt(1, result.ToolCalls.Count, "工具调用");
                Check.Equal("get_time", result.ToolCalls[0].Name, "工具名");
                Check.Contains(result.ToolCalls[0].ArgumentsJson, "+8", "参数序列化");
                Check.EqualInt(20, result.Usage.InputTokens, "输入 token");
                Check.EqualInt(15, result.Usage.OutputTokens, "输出 token 含思考的 6");
                Check.EqualInt(4, result.Usage.CachedTokens, "缓存 token");
            });

            Runner.Add("Gemini", "安全策略拦截给出可读原因", delegate
            {
                var blocked = GeminiChatProvider.ParseResponse(
                    @"{""promptFeedback"":{""blockReason"":""SAFETY""}}");
                Check.False(blocked.Ok, "应当失败");
                Check.Equal("blocked", blocked.ErrorCode, "错误码");
                Check.Contains(blocked.ErrorMessage, "安全策略", "说明");

                var empty = GeminiChatProvider.ParseResponse(@"{""candidates"":[]}");
                Check.Equal("empty", empty.ErrorCode, "空候选");

                var bad = GeminiChatProvider.ParseResponse("<html>502</html>");
                Check.Equal("parse", bad.ErrorCode, "非 JSON");
            });

            Runner.Add("Gemini", "模型列表解析：过滤掉不支持对话的模型", delegate
            {
                var json = @"{
                  ""models"": [
                    { ""name"": ""models/gemini-2.5-flash"", ""displayName"": ""Gemini 2.5 Flash"",
                      ""inputTokenLimit"": 1048576, ""supportedGenerationMethods"": [""generateContent""] },
                    { ""name"": ""models/text-embedding-004"",
                      ""supportedGenerationMethods"": [""embedContent""] }
                  ]
                }";

                var models = GeminiChatProvider.ParseModelList(json);

                Check.EqualInt(1, models.Count, "只留支持 generateContent 的");
                Check.Equal("gemini-2.5-flash", models[0].ModelId, "去掉 models/ 前缀");
                Check.Equal("Gemini 2.5 Flash", models[0].DisplayName, "显示名");
                Check.EqualInt(1048576, models[0].ContextWindow, "窗口来自服务端");
            });

            Runner.Add("Gemini", "provider 工厂按类型分派", delegate
            {
                Check.True(ProviderFactory.Create(MakeGeminiApi(), null, NullLogger.Instance) is GeminiChatProvider,
                    "gemini 走 GeminiChatProvider");
                Check.True(ProviderFactory.Create(new ApiProfile { Provider = "google" }, null, NullLogger.Instance) is GeminiChatProvider,
                    "google 别名同样识别");
                Check.True(ProviderFactory.Create(new ApiProfile { Provider = "anthropic" }, null, NullLogger.Instance) is AnthropicChatProvider,
                    "anthropic 不受影响");
                Check.True(ProviderFactory.Create(new ApiProfile { Provider = "openai" }, null, NullLogger.Instance) is OpenAiChatProvider,
                    "openai 不受影响");
            });

            Runner.Add("Gemini", "鉴权用请求头而不是 URL 参数", delegate
            {
                var spec = GeminiChatProvider.BuildRequest(
                    new ChatRequest { Api = MakeGeminiApi(), ApiKey = "AIza-test" }, false);

                Check.False(spec.Url.IndexOf("key=", StringComparison.Ordinal) >= 0, "URL 里不能出现密钥");
                Check.Equal("AIza-test", spec.Headers["x-goog-api-key"], "走请求头");
            });
        }

        // ═════════════ 群聊 ═════════════
        private static void RegisterGroupChat()
        {
            Runner.Add("群聊", "单成员不算群聊", delegate
            {
                var conversation = MakeGroupConversation(1);
                Check.False(GroupChat.IsGroup(conversation), "一个成员不是群聊");
                Check.EqualInt(1, GroupChat.ActiveMembers(conversation).Count, "成员数");
            });

            Runner.Add("群聊", "轮流模式按顺序推进", delegate
            {
                var conversation = MakeGroupConversation(3);

                var first = GroupChat.DecideSpeakers(conversation, "你们好", 0);
                Check.EqualInt(1, first.Speakers.Count, "一个发言人");
                Check.Equal("甲", first.Speakers[0].Name, "第一个");

                var second = GroupChat.DecideSpeakers(conversation, "继续", 1);
                Check.Equal("乙", second.Speakers[0].Name, "第二个");

                var fourth = GroupChat.DecideSpeakers(conversation, "继续", 3);
                Check.Equal("甲", fourth.Speakers[0].Name, "循环回第一个");
            });

            Runner.Add("群聊", "点名模式只让被 @ 的人说", delegate
            {
                var conversation = MakeGroupConversation(3);
                conversation.GroupReplyMode = GroupReplyModes.Mention;

                var decision = GroupChat.DecideSpeakers(conversation, "@丙 你怎么看", 0);
                Check.EqualInt(1, decision.Speakers.Count, "只有一个人");
                Check.Equal("丙", decision.Speakers[0].Name, "被点名的人");

                var none = GroupChat.DecideSpeakers(conversation, "没人被点名", 0);
                Check.EqualInt(1, none.Speakers.Count, "没点名就退化为轮流");
                Check.Equal("甲", none.Speakers[0].Name, "轮到第一个");
            });

            Runner.Add("群聊", "@名字前缀不误伤更长的名字", delegate
            {
                var members = new List<GroupMember>
                {
                    new GroupMember { ProfileId = "p1", Name = "林" },
                    new GroupMember { ProfileId = "p2", Name = "林深" }
                };

                var hit = GroupChat.FindMentioned(members, "@林深 你好");
                Check.EqualInt(1, hit.Count, "只命中一个");
                Check.Equal("林深", hit[0].Name, "@林 不该命中 @林深");

                var shortHit = GroupChat.FindMentioned(members, "@林 你好");
                Check.EqualInt(1, shortHit.Count, "短名字正常命中");
                Check.Equal("林", shortHit[0].Name, "命中短名字");
            });

            Runner.Add("群聊", "提示词块列出所有成员并指定发言人", delegate
            {
                var conversation = MakeGroupConversation(2);
                var speakers = new List<GroupMember> { conversation.Members[1] };

                var block = GroupChat.BuildGroupPersonaBlock(conversation, delegate (string id) { return null; }, speakers);

                Check.Contains(block, "群聊", "标明是群聊");
                Check.Contains(block, "甲", "列出成员甲");
                Check.Contains(block, "乙", "列出成员乙");
                Check.Contains(block, "乙 发言", "指定了发言人");
                Check.Contains(block, "角色名：", "要求标注说话人");
            });

            Runner.Add("群聊", "禁用成员被排除", delegate
            {
                var conversation = MakeGroupConversation(3);
                conversation.Members[1].Enabled = false;

                var members = GroupChat.ActiveMembers(conversation);
                Check.EqualInt(2, members.Count, "禁用成员不计入");
                Check.Equal("甲", members[0].Name, "剩下的按 Order 排");
                Check.Equal("丙", members[1].Name, "第二个是丙");
            });

            Runner.Add("群聊", "分叉会带走成员清单", delegate
            {
                var conversation = MakeGroupConversation(2);
                var fork = conversation.ForkAt(-1, "群聊分叉");
                Check.EqualInt(2, fork.Members.Count, "成员被复制");
                Check.Equal("轮流发言", GroupReplyModes.Label(fork.GroupReplyMode), "模式也带走");
            });
        }

        // ═════════════ 辅助 ═════════════
        private static Conversation MakeConversation()
        {
            var conversation = new Conversation { ProfileId = "p1" };
            conversation.Messages.Add(new Message { Role = MessageRole.Assistant, Content = "第一版", IsFinal = true });
            return conversation;
        }

        private static Conversation MakeGroupConversation(int memberCount)
        {
            var conversation = new Conversation { ProfileId = "p1" };
            var names = new string[] { "甲", "乙", "丙", "丁" };

            for (int i = 0; i < memberCount && i < names.Length; i++)
            {
                conversation.Members.Add(new GroupMember
                {
                    ProfileId = "profile-" + i,
                    Name = names[i],
                    Order = i,
                    Enabled = true
                });
            }
            return conversation;
        }

        private static ApiProfile MakeGeminiApi()
        {
            return new ApiProfile
            {
                Id = "gem",
                Name = "Gemini",
                Provider = "gemini",
                Model = "gemini-2.5-flash"
            };
        }
    }
}
