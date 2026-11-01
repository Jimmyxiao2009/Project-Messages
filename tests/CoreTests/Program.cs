// tests/CoreTests/Program.cs —— 用例集合与入口
//
// 覆盖范围刻意聚焦在"最容易出错又最难在手机上调试"的部分：
// 时间线投影、提示词装配、两家 provider 的请求/响应形态、权限判定、
// 存储原子性、SSE 解析。这些都是纯逻辑，桌面跑得飞快。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tests
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string filter = args != null && args.Length > 0 ? args[0] : null;

            RegisterText();
            RegisterStorage();
            RegisterTimeline();
            RegisterProviders();
            RegisterPrompt();
            RegisterPermissions();
            RegisterSettingsModel();
            AgentTests.Register();
            FeatureTests.Register();
            EndToEndTests.Register();
            ModelTests.Register();
            Feature2Tests.Register();
            Feature3Tests.Register();
            Feature4Tests.Register();
            Feature5Tests.Register();
            Feature6Tests.Register();
            Feature7Tests.Register();
            Feature8Tests.Register();
            Feature9Tests.Register();
            ManagementTests.Register();
            ProviderModelTests.Register();
            Feature10Tests.Register();

            return Runner.RunAll(filter);
        }

        // ═════════════════════════════════════════════════════════════════
        // 文本与检索
        // ═════════════════════════════════════════════════════════════════
        private static void RegisterText()
        {
            Runner.Add("文本", "Slug 保留中文、空格转连字符", delegate
            {
                Check.Equal("工程助理", TextUtil.Slug("工程助理"), "中文名保留");
                Check.Equal("my-profile", TextUtil.Slug("My Profile"), "空格转连字符并小写");
                Check.Equal("a-b", TextUtil.Slug("a/b"), "斜杠转连字符");
                Check.Equal("profile", TextUtil.Slug("///"), "全非法字符回退");
            });

            Runner.Add("文本", "Normalize 压空白并小写", delegate
            {
                Check.Equal("hello world", TextUtil.Normalize("  Hello   World  "), "压空白");
                Check.Equal("你好 世界", TextUtil.Normalize("你好\n\n世界"), "中文换行压成空格");
            });

            Runner.Add("文本", "Truncate 保留前 N 并加省略号", delegate
            {
                Check.Equal("abcde", TextUtil.Truncate("abcde", 10), "短串不动");
                Check.Equal("abc…", TextUtil.Truncate("abcdefgh", 3), "长串截断带省略号");
            });

            Runner.Add("文本", "Sha256 稳定且区分内容", delegate
            {
                var a = TextUtil.Sha256Hex("hello");
                var b = TextUtil.Sha256Hex("hello");
                var c = TextUtil.Sha256Hex("hellp");
                Check.Equal(a, b, "同输入同结果");
                Check.False(a == c, "不同输入不同结果");
                Check.EqualInt(64, a.Length, "十六进制长度");
            });

            Runner.Add("文本", "BM25 命中最相关的文档", delegate
            {
                var index = new Bm25Index();
                index.Add("d1", "登录失败是因为密码派生的 salt 处理错了");
                index.Add("d2", "今天天气不错适合出门散步");
                index.Add("d3", "时间线投影要把过程与最终输出分开");

                var hits = index.Search("密码派生 salt", 2);
                Check.True(hits.Count > 0, "应当有命中");
                Check.Equal("d1", hits[0].Key, "最相关文档排第一");
            });

            Runner.Add("文本", "BM25 无关键词时不返回结果", delegate
            {
                var index = new Bm25Index();
                index.Add("d1", "只有一句话");
                var hits = index.Search("完全无关的查询xyzzy", 5);
                Check.EqualInt(0, hits.Count, "不应命中");
            });

            Runner.Add("文本", "BM25 中文双字组合可召回", delegate
            {
                var index = new Bm25Index();
                index.Add("d1", "工具调用循环最大轮次是十五");
                var hits = index.Search("调用轮次", 3);
                Check.True(hits.Count > 0, "双字组合应命中");
            });

            Runner.Add("词元", "中文比拉丁更耗 token", delegate
            {
                int cjk = TokenEstimator.Estimate("这是一段中文字符串");
                int latin = TokenEstimator.Estimate("this is a latin string");
                Check.True(cjk > latin, "中文估值应高于等长拉丁");
            });

            Runner.Add("词元", "emoji 与全角标点被计入", delegate
            {
                int withEmoji = TokenEstimator.Estimate("你好🙂");
                int without = TokenEstimator.Estimate("你好");
                Check.True(withEmoji > without, "emoji 应增加估值");
            });

            Runner.Add("词元", "空串估 0、非空至少 1", delegate
            {
                Check.EqualInt(0, TokenEstimator.Estimate(""), "空串");
                Check.EqualInt(1, TokenEstimator.Estimate("a"), "单字符至少 1");
            });

            Runner.Add("词元", "模型窗口表命中与回退", delegate
            {
                Check.EqualInt(128000, ModelContextLimits.GetLimit("deepseek-chat"), "命中内置表");
                Check.EqualInt(128000, ModelContextLimits.GetLimit("deepseek-chat-0324"), "带后缀用子串命中");
                Check.EqualInt(ModelContextLimits.DefaultLimit, ModelContextLimits.GetLimit("完全不认识的模型"), "回退默认");
                Check.EqualInt(9999, ModelContextLimits.GetEffectiveLimit("deepseek-chat", 9999), "档案显式值优先");
            });
        }

        // ═════════════════════════════════════════════════════════════════
        // 工具入参与 SSE
        // ═════════════════════════════════════════════════════════════════
        private static void RegisterProviders()
        {
            Runner.Add("工具入参", "解析顶层字段并压成一行", delegate
            {
                var pairs = ToolArguments.Parse("{\"path\":\"notes.md\",\"count\":3,\"flag\":true}");
                Check.EqualInt(3, pairs.Count, "字段数");
                Check.Equal("notes.md", pairs[0].Value, "字符串值");
                Check.Equal("3", pairs[1].Value, "数字值");
                Check.Equal("true", pairs[2].Value, "布尔值");
            });

            Runner.Add("工具入参", "取值器兼容字符串型数字与布尔", delegate
            {
                var json = "{\"n\":\"42\",\"b\":\"yes\",\"s\":123}";
                Check.EqualInt(42, ToolArguments.GetInt(json, "n", 0), "字符串数字");
                Check.True(ToolArguments.GetBool(json, "b", false), "yes 当真");
                Check.Equal("123", ToolArguments.GetString(json, "s", ""), "数字转字符串");
                Check.Equal("缺省", ToolArguments.GetString(json, "missing", "缺省"), "缺字段用缺省值");
            });

            Runner.Add("工具入参", "数组支持 JSON 数组与逗号串", delegate
            {
                var fromArray = ToolArguments.GetStringArray("{\"k\":[\"a\",\"b\"]}", "k");
                Check.EqualInt(2, fromArray.Count, "数组长度");
                var fromCsv = ToolArguments.GetStringArray("{\"k\":\"a, b ,c\"}", "k");
                Check.EqualInt(3, fromCsv.Count, "逗号串长度");
                Check.Equal("b", fromCsv[1], "逗号串去空格");
            });

            Runner.Add("工具入参", "坏 JSON 不抛异常", delegate
            {
                var pairs = ToolArguments.Parse("这不是 JSON");
                Check.EqualInt(0, pairs.Count, "返回空集合");
                Check.False(ToolArguments.IsValidJson("{\"a\":"), "识别非法 JSON");
            });

            Runner.Add("工具入参", "修补被截断的 JSON", delegate
            {
                var repaired = ToolArguments.RepairTruncated("{\"path\":\"notes.md\",\"content\":\"abc");
                Check.True(ToolArguments.IsValidJson(repaired), "修补后应合法");
                Check.Contains(repaired, "notes.md", "原有字段保留");
            });

            Runner.Add("SSE", "单行 data 事件", delegate
            {
                var parser = new SseParser();
                Check.True(parser.Feed("data: {\"a\":1}") == null, "数据行本身不产出事件");
                var evt = parser.Feed("");
                Check.True(evt != null, "空行产出事件");
                Check.Equal("{\"a\":1}", evt.Data, "负载内容");
            });

            Runner.Add("SSE", "容忍粘包：连续 data 行各自成事件", delegate
            {
                var parser = new SseParser();
                var first = parser.Feed("data: {\"a\":1}");
                var second = parser.Feed("data: {\"a\":2}");
                Check.True(first == null, "第一行先缓冲");
                Check.Equal("{\"a\":1}", second.Data, "第二行到来时先把上一段吐出来");

                var third = parser.Feed("");
                Check.Equal("{\"a\":2}", third.Data, "空行吐出最后一段");
            });

            Runner.Add("SSE", "严格模式：按规范拼接多行 data", delegate
            {
                var parser = new SseParser(false);
                parser.Feed("data: line1");
                parser.Feed("data: line2");
                var evt = parser.Feed("");
                Check.Equal("line1\nline2", evt.Data, "多行拼接");
            });

            Runner.Add("SSE", "冒号后无空格也能解析", delegate
            {
                var parser = new SseParser();
                parser.Feed("data:{\"a\":1}");
                var evt = parser.Feed("");
                Check.Equal("{\"a\":1}", evt.Data, "无空格");
            });

            Runner.Add("SSE", "注释行与 [DONE] 处理", delegate
            {
                var parser = new SseParser();
                Check.True(parser.Feed(": keep-alive") == null, "注释行忽略");
                parser.Feed("data: [DONE]");
                var evt = parser.Feed("");
                Check.True(evt.IsDone, "识别 DONE");
            });

            Runner.Add("OpenAI", "请求体包含 messages / tools / 参数", delegate
            {
                var request = BuildSampleRequest(false);
                request.Tools.Add(new ToolSchema { Name = "read_file", Description = "读文件" }
                    .Param("path", "string", "路径", true));

                var body = OpenAiChatProvider.BuildBody(request, false);
                Check.Equal("deepseek-chat", body["model"].ToString(), "模型名");
                Check.False(body["stream"].Value<bool>(), "非流式");
                // system + user + assistant(带 tool_calls) + tool 结果
                Check.EqualInt(4, ((JArray)body["messages"]).Count, "消息数");
                Check.Equal("read_file", body["tools"][0]["function"]["name"].ToString(), "工具名");
                Check.True(body["tool_choice"] != null, "带 tool_choice");
            });

            Runner.Add("OpenAI", "请求体不把 reasoning 回传（默认）", delegate
            {
                var request = BuildSampleRequest(false);
                var body = OpenAiChatProvider.BuildBody(request, false);
                var messages = (JArray)body["messages"];
                var assistant = messages.First(delegate (JToken t) { return t["role"].ToString() == "assistant"; });

                Check.True(assistant["reasoning_content"] == null, "默认不回传 reasoning_content");
                Check.True(assistant["tool_calls"] != null, "但工具调用要带上");
            });

            Runner.Add("OpenAI", "打开开关后回传 reasoning（DeepSeek 中转需要）", delegate
            {
                var request = BuildSampleRequest(false);
                request.SendReasoningBack = true;
                var body = OpenAiChatProvider.BuildBody(request, false);
                var messages = (JArray)body["messages"];
                var assistant = messages.First(delegate (JToken t) { return t["role"].ToString() == "assistant"; });
                Check.Equal("先看文件", assistant["reasoning_content"].ToString(), "回传 reasoning_content");
            });

            Runner.Add("OpenAI", "非流式响应解析出内容、推理与用量", delegate
            {
                var json = "{\"choices\":[{\"message\":{\"content\":\"你好\",\"reasoning_content\":\"想了下\"," +
                           "\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"a.md\\\"}\"}}]}}]," +
                           "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"prompt_tokens_details\":{\"cached_tokens\":3}}}";
                var result = OpenAiChatProvider.ParseResponse(json);
                Check.True(result.Ok, "应当成功");
                Check.Equal("你好", result.Content, "内容");
                Check.Equal("想了下", result.Reasoning, "推理");
                Check.EqualInt(1, result.ToolCalls.Count, "工具调用数");
                Check.Equal("read_file", result.ToolCalls[0].Name, "工具名");
                Check.EqualInt(10, result.Usage.InputTokens, "输入 token");
                Check.EqualInt(3, result.Usage.CachedTokens, "缓存 token");
            });

            Runner.Add("OpenAI", "错误响应给出稳定错误码", delegate
            {
                var result = OpenAiChatProvider.ParseResponse("{\"error\":{\"message\":\"invalid api key\"}}");
                Check.False(result.Ok, "应当失败");
                Check.Equal("api_error", result.ErrorCode, "错误码");
                Check.Contains(result.ErrorMessage, "invalid api key", "带上服务端说明");
            });

            Runner.Add("OpenAI", "空响应与畸形响应不炸", delegate
            {
                Check.Equal("empty", OpenAiChatProvider.ParseResponse("").ErrorCode, "空 body");
                Check.Equal("parse", OpenAiChatProvider.ParseResponse("<html>502</html>").ErrorCode, "非 JSON");
                Check.Equal("empty", OpenAiChatProvider.ParseResponse("{\"choices\":[]}").ErrorCode, "无候选");
            });

            Runner.Add("Anthropic", "system 提为顶层字段、max_tokens 必填", delegate
            {
                var request = BuildSampleRequest(true);
                var body = AnthropicChatProvider.BuildBody(request, false);
                Check.Equal("你是助手", body["system"].ToString(), "system 顶层");
                Check.EqualInt(AnthropicChatProvider.DefaultMaxTokens, body["max_tokens"].Value<int>(), "默认 max_tokens");
                var messages = (JArray)body["messages"];
                foreach (var m in messages)
                {
                    Check.False(m["role"].ToString() == "system", "消息里不应再有 system 角色");
                }
            });

            Runner.Add("Anthropic", "工具结果合并进同一条 user 消息", delegate
            {
                var request = new ChatRequest { Api = MakeApi(true) };
                request.Turns.Add(ChatTurn.System("sys"));
                request.Turns.Add(ChatTurn.User("帮我读两个文件"));
                var assistant = ChatTurn.Assistant("");
                assistant.ToolCalls.Add(new ToolCallRequest { Id = "t1", Name = "read_file", ArgumentsJson = "{\"path\":\"a\"}" });
                assistant.ToolCalls.Add(new ToolCallRequest { Id = "t2", Name = "read_file", ArgumentsJson = "{\"path\":\"b\"}" });
                request.Turns.Add(assistant);
                request.Turns.Add(ChatTurn.ToolResult("t1", "read_file", "A 的内容"));
                request.Turns.Add(ChatTurn.ToolResult("t2", "read_file", "B 的内容"));

                var messages = AnthropicChatProvider.BuildMessages(request);
                // user + assistant(两个 tool_use) + user(两个 tool_result 合并)
                Check.EqualInt(3, messages.Count, "两条工具结果应合并，不额外增条");

                var last = messages[2];
                Check.Equal("user", last["role"].ToString(), "最后一条是 user");
                var blocks = (JArray)last["content"];
                Check.EqualInt(2, blocks.Count, "两个 tool_result 合并在一条消息里");
                Check.Equal("tool_result", blocks[0]["type"].ToString(), "块类型");
                Check.Equal("t1", blocks[0]["tool_use_id"].ToString(), "对应调用 id");
                Check.Equal("A 的内容", blocks[0]["content"].ToString(), "结果内容");
            });

            Runner.Add("Anthropic", "助手消息里的 tool_use 参数是对象不是字符串", delegate
            {
                var request = new ChatRequest { Api = MakeApi(true) };
                request.Turns.Add(ChatTurn.User("读文件"));
                var assistant = ChatTurn.Assistant("");
                assistant.ToolCalls.Add(new ToolCallRequest { Id = "t1", Name = "read_file", ArgumentsJson = "{\"path\":\"a.md\"}" });
                request.Turns.Add(assistant);

                var messages = AnthropicChatProvider.BuildMessages(request);
                var blocks = (JArray)messages[1]["content"];
                var toolUse = blocks.First(delegate (JToken t) { return t["type"].ToString() == "tool_use"; });
                Check.True(toolUse["input"] is JObject, "input 应当是对象");
                Check.Equal("a.md", toolUse["input"]["path"].ToString(), "参数值");
            });

            Runner.Add("Anthropic", "响应解析 text / thinking / tool_use", delegate
            {
                var json = "{\"content\":[{\"type\":\"thinking\",\"thinking\":\"先想想\"}," +
                           "{\"type\":\"text\",\"text\":\"答案\"}," +
                           "{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"read_file\",\"input\":{\"path\":\"a.md\"}}]," +
                           "\"usage\":{\"input_tokens\":20,\"output_tokens\":8,\"cache_read_input_tokens\":6}}";
                var result = AnthropicChatProvider.ParseResponse(json);
                Check.True(result.Ok, "应当成功");
                Check.Equal("答案", result.Content, "文本块");
                Check.Equal("先想想", result.Reasoning, "思考块");
                Check.EqualInt(1, result.ToolCalls.Count, "工具调用");
                Check.Contains(result.ToolCalls[0].ArgumentsJson, "a.md", "参数序列化成字符串");
                Check.EqualInt(6, result.Usage.CachedTokens, "缓存 token");
            });

            Runner.Add("流式", "OpenAI SSE 增量被正确拼接", delegate
            {
                var http = new FakeHttpTransport();
                http.NextStreamLines = new List<string>
                {
                    "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"思考1\"}}]}",
                    "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}",
                    "data: {\"choices\":[{\"delta\":{\"content\":\"，世界\"}}]}",
                    "data: [DONE]"
                };

                var provider = new OpenAiChatProvider(http, NullLogger.Instance);
                var deltas = new List<string>();
                var result = provider.StreamAsync(BuildSampleRequest(false),
                    delegate (StreamChunk c) { if (c.ContentDelta != null) deltas.Add(c.ContentDelta); },
                    CancellationToken.None).Result;

                Check.True(result.Ok, "应当成功");
                Check.Equal("你好，世界", result.Content, "拼接后的正文");
                Check.Equal("思考1", result.Reasoning, "推理内容");
                Check.EqualInt(2, deltas.Count, "回调次数");
            });

            Runner.Add("流式", "坏帧跳过、不中断整条流", delegate
            {
                var http = new FakeHttpTransport();
                http.NextStreamLines = new List<string>
                {
                    "data: {坏掉的 JSON",
                    "data: {\"choices\":[{\"delta\":{\"content\":\"仍然到达\"}}]}",
                    "data: [DONE]"
                };
                var provider = new OpenAiChatProvider(http, NullLogger.Instance);
                var result = provider.StreamAsync(BuildSampleRequest(false), null, CancellationToken.None).Result;
                Check.True(result.Ok, "应当成功");
                Check.Equal("仍然到达", result.Content, "坏帧被跳过");
            });

            Runner.Add("重试", "401 不重试、5xx 重试", delegate
            {
                var http = new FakeHttpTransport();
                http.NextStatus = 401;
                var provider = new OpenAiChatProvider(http, NullLogger.Instance);
                var result = provider.CompleteAsync(BuildSampleRequest(false), CancellationToken.None).Result;
                Check.Equal("http_401", result.ErrorCode, "401 错误码");
                Check.EqualInt(1, http.Requests.Count, "401 只请求一次");
                Check.Equal("http_429", ProviderErrors.FromStatus(429, "").ErrorCode, "429 映射");
                Check.True(ProviderErrors.IsRetryable(503), "503 可重试");
                Check.False(ProviderErrors.IsRetryable(400), "400 不可重试");
            });
        }

        // ═════════════════════════════════════════════════════════════════
        // 存储
        // ═════════════════════════════════════════════════════════════════
        private static void RegisterStorage()
        {
            Runner.Add("存储", "原子写：先写 .tmp 再替换，不留临时文件", delegate
            {
                var store = new InMemoryFileStore();
                AtomicFile.WriteAsync(store, "a/b.json", "{\"x\":1}").Wait();

                Check.Equal("{\"x\":1}", store.Peek("a/b.json"), "目标文件内容");
                Check.True(store.Peek("a/b.json.tmp") == null, "不应残留 .tmp");
                Check.EqualInt(1, store.MoveCount, "发生一次替换");
            });

            Runner.Add("存储", "保存合并：同路径连续登记只落盘最后一次", delegate
            {
                var store = new InMemoryFileStore();
                var scheduler = new SaveScheduler(store, NullLogger.Instance, 30);

                scheduler.Schedule("c.json", delegate { return "{\"v\":1}"; });
                scheduler.Schedule("c.json", delegate { return "{\"v\":2}"; });
                scheduler.FlushAsync().Wait();

                Check.Equal("{\"v\":2}", store.Peek("c.json"), "最后一次生效");
                Check.EqualInt(1, store.WriteCount, "只写了一次");
                scheduler.Dispose();
            });

            Runner.Add("存储", "SaveNowAsync 绕过合并立即落盘", delegate
            {
                var store = new InMemoryFileStore();
                var scheduler = new SaveScheduler(store, NullLogger.Instance, 5000);
                scheduler.Schedule("d.json", delegate { return "{\"v\":\"scheduled\"}"; });
                scheduler.SaveNowAsync("d.json", delegate { return "{\"v\":\"now\"}"; }).Wait();
                Check.Equal("{\"v\":\"now\"}", store.Peek("d.json"), "立即写入生效");
                scheduler.Dispose();
            });

            Runner.Add("存储", "JSON 往返不丢字段", delegate
            {
                var profile = new Profile { Name = "工程助理", Slug = "eng" };
                profile.Persona.Description = "描述内容";
                profile.Appearance.QuoteColor = "#DCA03C";
                profile.Memory.MaxMemories = 123;

                var json = JsonStore.Serialize(profile);
                var back = JsonStore.TryDeserialize<Profile>(json, NullLogger.Instance, "test");

                Check.Equal("工程助理", back.Name, "名称");
                Check.Equal("描述内容", back.Persona.Description, "人设描述");
                Check.Equal("#DCA03C", back.Appearance.QuoteColor, "引号色");
                Check.EqualInt(123, back.Memory.MaxMemories, "记忆上限");
            });

            Runner.Add("存储", "损坏的 JSON 返回 null 而不抛", delegate
            {
                var back = JsonStore.TryDeserialize<Profile>("{ 这不是合法 json", NullLogger.Instance, "broken");
                Check.True(back == null, "应当返回 null");
            });

            Runner.Add("存储", "未知字段忽略、显式 null 不覆盖默认子对象", delegate
            {
                var json = "{\"name\":\"x\",\"futureField\":\"忽略我\",\"memory\":null}";
                var profile = JsonStore.TryDeserialize<Profile>(json, NullLogger.Instance, "tolerant");
                Check.Equal("x", profile.Name, "已知字段读到");
                Check.True(profile.Memory != null, "显式 null 不该清掉默认子对象（NullValueHandling.Ignore 的正面作用）");
                Check.True(profile.Tools.Enabled != null, "集合有默认值");

                profile.Repair();
                Check.True(profile.Memory != null, "Repair 后仍然完整");
            });

            Runner.Add("存储", "Profile.Repair 兜底消息模板", delegate
            {
                var profile = new Profile();
                profile.Generation.MessageTemplate = "没有占位符";
                profile.Repair();
                Check.Equal("{{message}}", profile.Generation.MessageTemplate, "缺 {{message}} 时回退");

                var profile2 = new Profile();
                profile2.Name = "带中文的名字";
                profile2.Slug = "";
                profile2.Repair();
                Check.Equal("带中文的名字", profile2.Slug, "Slug 自动生成");
            });

            Runner.Add("存储", "会话索引：写入/更新/排序/删除", delegate
            {
                var index = new ConversationIndex();
                index.Upsert(new ConversationIndexEntry { Id = "a", Title = "A", UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-2) });
                index.Upsert(new ConversationIndexEntry { Id = "b", Title = "B", UpdatedAtUtc = DateTimeOffset.UtcNow });
                index.Upsert(new ConversationIndexEntry { Id = "c", Title = "C", Pinned = true, UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-5) });

                var sorted = index.Sorted(false);
                Check.Equal("c", sorted[0].Id, "置顶优先");
                Check.Equal("b", sorted[1].Id, "然后按时间倒序");

                index.Upsert(new ConversationIndexEntry { Id = "a", Title = "A2" });
                Check.Equal("A2", index.Find("a").Title, "更新生效");
                Check.EqualInt(3, index.Entries.Count, "更新不新增");

                Check.True(index.Remove("b"), "删除成功");
                Check.EqualInt(2, index.Entries.Count, "删除后数量");
            });

            Runner.Add("存储", "归档会话默认不出现在列表", delegate
            {
                var index = new ConversationIndex();
                index.Upsert(new ConversationIndexEntry { Id = "a", Archived = true });
                index.Upsert(new ConversationIndexEntry { Id = "b" });
                Check.EqualInt(1, index.Sorted(false).Count, "不含归档");
                Check.EqualInt(2, index.Sorted(true).Count, "含归档");
            });
        }

        // ═════════════════════════════════════════════════════════════════
        // 时间线投影
        // ═════════════════════════════════════════════════════════════════
        private static void RegisterTimeline()
        {
            Runner.Add("时间线", "一轮拆成 过程 + 最终输出 两行", delegate
            {
                var conv = BuildConversationWithTurn();
                var rows = TimelineProjector.Project(conv, false, null);

                Check.EqualInt(3, rows.Count, "行数：用户 + 过程 + 输出");
                Check.Equal(RowKind.User, rows[0].Kind, "第一行是用户");
                Check.Equal(RowKind.Process, rows[1].Kind, "第二行是过程");
                Check.Equal(RowKind.Output, rows[2].Kind, "第三行是输出");
                Check.Equal("最终答案", rows[2].Text, "输出内容");
            });

            Runner.Add("时间线", "过程行包含思考与工具两个步骤", delegate
            {
                var conv = BuildConversationWithTurn();
                var rows = TimelineProjector.Project(conv, false, null);
                var steps = rows[1].Steps;

                Check.EqualInt(2, steps.Count, "两个步骤");
                Check.Equal(StepKind.Reasoning, steps[0].Kind, "第一个是思考");
                Check.Equal(StepKind.Tool, steps[1].Kind, "第二个是工具");
                Check.Equal("读取文件", steps[1].Title, "工具名转中文");
            });

            Runner.Add("时间线", "工具结果回填到对应步骤", delegate
            {
                var conv = BuildConversationWithTurn();
                var rows = TimelineProjector.Project(conv, false, null);
                var toolStep = rows[1].Steps[1];

                Check.Equal("文件内容在此", toolStep.Body, "结果回填");
                Check.Equal(StepState.Done, toolStep.State, "状态为完成");
                Check.Contains(toolStep.Subtitle, "notes.md", "副标题带参数摘要");
            });

            Runner.Add("时间线", "失败的轮次整体标红", delegate
            {
                var conv = BuildConversationWithTurn();
                conv.Messages[2].Failed = true;   // tool 消息失败
                var rows = TimelineProjector.Project(conv, false, null);
                Check.Equal(StepState.Failed, rows[1].State, "过程行状态");
                Check.Contains(rows[1].Summary, "1 失败", "摘要带失败计数");
            });

            Runner.Add("时间线", "运行中的轮次追加实时步骤且默认展开", delegate
            {
                var conv = BuildConversationWithTurn();
                conv.Messages.RemoveAt(conv.Messages.Count - 1);   // 去掉最终输出

                var live = new List<StepRecord>
                {
                    new StepRecord { Kind = StepKind.Tool, State = StepState.Running, Title = "联网搜索" }
                };
                var rows = TimelineProjector.Project(conv, true, live);

                Check.Equal(RowKind.Process, rows[rows.Count - 1].Kind, "最后一行是过程");
                Check.Equal(StepState.Running, rows[rows.Count - 1].State, "状态运行中");
                Check.False(rows[rows.Count - 1].Collapsed, "运行中默认展开");
            });

            Runner.Add("时间线", "步骤过多时折叠成「显示前面 N 步」", delegate
            {
                var steps = new List<StepRecord>();
                for (int i = 0; i < 9; i++)
                {
                    steps.Add(new StepRecord { Kind = StepKind.Tool, State = StepState.Done, Title = "工具" + i });
                }

                var visible = TimelineProjector.VisibleSteps(steps, true);
                Check.EqualInt(Limits.RunningVisibleSteps + 1, visible.Count, "折叠节点 + 最近 5 步");
                Check.Equal(StepKind.More, visible[0].Kind, "首项是折叠节点");
                Check.Contains(visible[0].Title, "显示前面 4 步", "折叠数量正确");
            });

            Runner.Add("时间线", "非运行状态不折叠", delegate
            {
                var steps = new List<StepRecord>();
                for (int i = 0; i < 9; i++) steps.Add(new StepRecord { Kind = StepKind.Tool, Title = "t" + i });
                var visible = TimelineProjector.VisibleSteps(steps, false);
                Check.EqualInt(9, visible.Count, "全部露出");
            });

            Runner.Add("时间线", "系统消息变成居中小字条", delegate
            {
                var conv = new Conversation();
                conv.Messages.Add(new Message { Role = MessageRole.System, Content = "上下文已压缩" });
                var rows = TimelineProjector.Project(conv, false, null);
                Check.Equal(RowKind.Marker, rows[0].Kind, "标记行");
                Check.Equal("上下文已压缩", rows[0].Text, "文案");
            });

            Runner.Add("时间线", "参数摘要压成一行", delegate
            {
                var summary = TimelineProjector.SummarizeArguments("{\"path\":\"notes.md\",\"mode\":\"overwrite\"}");
                Check.Contains(summary, "path=notes.md", "含键值");
                Check.Contains(summary, "mode=overwrite", "含第二个键值");
            });

            Runner.Add("时间线", "定稿消息自带的思考也要进过程轨道", delegate
            {
                // 不开工具时回复是直接定稿的，思考就挂在这条定稿消息上。
                // 这里曾经在 IsFinal 分支提前 continue，导致定稿后思考整段消失：
                // 流式生成时能看到"思考"，回复一结束、或重新打开会话就再也找不到。
                var conv = new Conversation { Id = "conv2", ProfileId = "p1" };
                conv.Messages.Add(new Message { Role = MessageRole.User, Content = "你好" });
                conv.Messages.Add(new Message
                {
                    Role = MessageRole.Assistant,
                    Content = "你好呀",
                    Reasoning = "先打个招呼",
                    IsFinal = true
                });

                var rows = TimelineProjector.Project(conv, false, null);

                Check.EqualInt(3, rows.Count, "行数：用户 + 过程 + 输出");
                Check.Equal(RowKind.Process, rows[1].Kind, "第二行是过程");
                Check.EqualInt(1, rows[1].Steps.Count, "过程里有一个步骤");
                Check.Equal(StepKind.Reasoning, rows[1].Steps[0].Kind, "该步骤是思考");
                Check.Equal("先打个招呼", rows[1].Steps[0].Body, "思考正文保留");
                Check.Contains(rows[1].Summary, "思考 1", "摘要带思考计数");
                Check.Equal(RowKind.Output, rows[2].Kind, "第三行是输出");
                Check.Equal("你好呀", rows[2].Text, "输出内容");
            });

            Runner.Add("时间线", "定稿消息的思考与工具一起进轨道", delegate
            {
                var conv = new Conversation { Id = "conv3", ProfileId = "p1" };
                conv.Messages.Add(new Message { Role = MessageRole.User, Content = "查一下" });

                var assistant = new Message
                {
                    Role = MessageRole.Assistant,
                    Content = "答案是 42",
                    Reasoning = "先搜索",
                    IsFinal = true
                };
                assistant.ToolCalls.Add(new ToolCallRecord
                {
                    Id = "c1",
                    Name = "web_search",
                    ArgumentsJson = "{\"q\":\"42\"}"
                });
                conv.Messages.Add(assistant);

                var rows = TimelineProjector.Project(conv, false, null);

                Check.EqualInt(3, rows.Count, "行数：用户 + 过程 + 输出");
                Check.EqualInt(2, rows[1].Steps.Count, "思考 + 工具两步");
                Check.Equal(StepKind.Reasoning, rows[1].Steps[0].Kind, "第一步是思考");
                Check.Equal(StepKind.Tool, rows[1].Steps[1].Kind, "第二步是工具");
                Check.Equal(RowKind.Output, rows[2].Kind, "输出行仍在");
            });
        }

        // ═════════════════════════════════════════════════════════════════
        // 提示词装配
        // ═════════════════════════════════════════════════════════════════
        private static void RegisterPrompt()
        {
            Runner.Add("占位符", "已知占位符被替换、未知原样保留", delegate
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                values["user"] = "小明";
                values["char"] = "工程助理";

                var text = Placeholders.Resolve("你好 {{user}}，我是 {{char}}，还有 {unknown}", values);
                Check.Contains(text, "小明", "双花括号替换");
                Check.Contains(text, "工程助理", "角色名替换");
                Check.Contains(text, "{unknown}", "未知占位符保留原样");
            });

            Runner.Add("占位符", "单花括号写法同样支持", delegate
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                values["char"] = "助手";
                Check.Equal("我是助手", Placeholders.Resolve("我是{char}", values), "单花括号");
            });

            Runner.Add("提示词", "系统提示词为空时按人设字段拼装", delegate
            {
                var ctx = new PromptContext { Profile = new Profile() };
                ctx.Profile.Persona.Name = "林深";
                ctx.Profile.Persona.Description = "冷静的工程师";
                ctx.Profile.Persona.Personality = "话少";

                var assembled = PromptAssembler.Build(ctx);
                Check.Contains(assembled.SystemPrompt, "你是 林深", "带上名字");
                Check.Contains(assembled.SystemPrompt, "[描述] 冷静的工程师", "带描述段");
                Check.Contains(assembled.SystemPrompt, "[性格] 话少", "带性格段");
            });

            Runner.Add("提示词", "空字段不留空标题", delegate
            {
                var ctx = new PromptContext { Profile = new Profile() };
                ctx.Profile.Persona.Name = "只有名字";
                var assembled = PromptAssembler.Build(ctx);
                Check.NotContains(assembled.SystemPrompt, "[描述]", "没有描述就不出现标题");
            });

            Runner.Add("提示词", "模型元数据说支持图片时图片进请求", delegate
            {
                // 真实故障的复现：Provider 上的 vision 是 false，模型元数据是 true（手动设"支持"）。
                // 装配层曾经只看裸布尔量，于是图片被静默丢掉 ——
                // 请求声明支持图片、消息里却没有图，模型回答"我没收到图片"，界面上不报错也没日志。
                var profile = new Profile();
                profile.Api.VisionEnabled = null;                                          // 档案没表态
                var api = new ApiProfile { Model = "vision-model", VisionEnabled = false }; // Provider 级是 false
                api.ModelOverrides["vision-model"] = new ModelMetadata
                {
                    ModelId = "vision-model", Vision = true, Source = MetadataSource.Manual
                };

                var conversation = new Conversation { Id = "c1" };
                var message = new Message { Role = MessageRole.User, Content = "看看这张图" };
                message.Attachments.Add(new Attachment
                {
                    Kind = "image", Name = "p.jpg", Path = "p.jpg",
                    MimeType = "image/jpeg", ImageBase64 = "QUJD"
                });
                conversation.Messages.Add(message);

                var ctx = new PromptContext { Profile = profile, Api = api, Conversation = conversation };
                var assembled = PromptAssembler.Build(ctx);

                var userTurn = assembled.Turns.First(t => t.Role == "user");
                Check.EqualInt(1, userTurn.Images.Count, "图片进请求");
                Check.Equal("QUJD", userTurn.Images[0].Base64, "图片内容");
                Check.Equal("image/jpeg", userTurn.Images[0].MimeType, "图片类型");
            });

            Runner.Add("提示词", "模型元数据说不支持图片时不塞图", delegate
            {
                var profile = new Profile();
                var api = new ApiProfile { Model = "text-only", VisionEnabled = true };     // Provider 级是 true
                api.ModelOverrides["text-only"] = new ModelMetadata
                {
                    ModelId = "text-only", Vision = false, Source = MetadataSource.Manual
                };

                var conversation = new Conversation { Id = "c2" };
                var message = new Message { Role = MessageRole.User, Content = "看看这张图" };
                message.Attachments.Add(new Attachment
                {
                    Kind = "image", Path = "p.jpg", MimeType = "image/jpeg", ImageBase64 = "QUJD"
                });
                conversation.Messages.Add(message);

                var ctx = new PromptContext { Profile = profile, Api = api, Conversation = conversation };
                var assembled = PromptAssembler.Build(ctx);

                var userTurn = assembled.Turns.First(t => t.Role == "user");
                Check.EqualInt(0, userTurn.Images.Count, "元数据优先，不塞图");
            });

            Runner.Add("提示词", "注入位置：系统提示前/后分别落位", delegate
            {
                var ctx = new PromptContext { Profile = new Profile(), Library = new PromptLibrary() };
                ctx.Profile.Persona.Name = "助手";

                var before = new ModeInjection { Id = "i1", Name = "前置", Content = "前置内容", Position = InjectionPosition.BeforeSystem };
                var after = new ModeInjection { Id = "i2", Name = "后置", Content = "后置内容", Position = InjectionPosition.AfterSystem };
                ctx.Library.Injections.Add(before);
                ctx.Library.Injections.Add(after);
                ctx.Profile.Prompts.InjectionIds.Add("i1");
                ctx.Profile.Prompts.InjectionIds.Add("i2");

                var assembled = PromptAssembler.Build(ctx);
                int beforeIdx = assembled.SystemPrompt.IndexOf("前置内容", StringComparison.Ordinal);
                int afterIdx = assembled.SystemPrompt.IndexOf("后置内容", StringComparison.Ordinal);
                int personaIdx = assembled.SystemPrompt.IndexOf("你是 助手", StringComparison.Ordinal);

                Check.True(beforeIdx >= 0 && afterIdx >= 0 && personaIdx >= 0, "三段都在");
                Check.True(beforeIdx < personaIdx, "前置在人设之前");
                Check.True(afterIdx > personaIdx, "后置在人设之后");
            });

            Runner.Add("提示词", "世界书按关键词命中", delegate
            {
                var entry = new LorebookEntry { Name = "世界观", Content = "这是一个蒸汽朋克世界" };
                entry.Keywords.Add("蒸汽");
                Check.True(PromptAssembler.MatchesKeywords(entry, "你见过蒸汽机吗"), "命中关键词");
                Check.False(PromptAssembler.MatchesKeywords(entry, "今天天气不错"), "未命中");

                var always = new LorebookEntry { Name = "常驻", Content = "x", AlwaysActive = true };
                Check.True(PromptAssembler.MatchesKeywords(always, ""), "常驻条目无条件命中");
            });

            Runner.Add("提示词", "英文关键词用词边界避免误命中", delegate
            {
                var entry = new LorebookEntry { Name = "e", Content = "x" };
                entry.Keywords.Add("art");
                Check.True(PromptAssembler.MatchesKeywords(entry, "this is art"), "独立单词命中");
                Check.False(PromptAssembler.MatchesKeywords(entry, "start the engine"), "start 里的 art 不该命中");
            });

            Runner.Add("提示词", "工具与结果的相邻关系不被注入破坏", delegate
            {
                var ctx = new PromptContext { Profile = new Profile(), Library = new PromptLibrary() };
                ctx.Profile.Persona.Name = "助手";
                ctx.Conversation = BuildConversationWithTurn();

                var bottom = new ModeInjection { Id = "b1", Content = "底部提醒", Position = InjectionPosition.BottomOfChat };
                ctx.Library.Injections.Add(bottom);
                ctx.Profile.Prompts.InjectionIds.Add("b1");

                var assembled = PromptAssembler.Build(ctx);

                for (int i = 0; i < assembled.Turns.Count; i++)
                {
                    var turn = assembled.Turns[i];
                    if (!string.Equals(turn.Role, "assistant", StringComparison.Ordinal)) continue;
                    if (turn.ToolCalls == null || turn.ToolCalls.Count == 0) continue;

                    Check.True(i + 1 < assembled.Turns.Count, "工具调用后面应当紧跟结果");
                    Check.Equal("tool", assembled.Turns[i + 1].Role, "紧跟的必须是 tool 角色");
                }
            });

            Runner.Add("提示词", "消息模板包裹用户输入", delegate
            {
                var ctx = new PromptContext { Profile = new Profile() };
                ctx.Profile.Generation.MessageTemplate = "【用户说】{{message}}";
                ctx.Conversation = new Conversation();
                ctx.Conversation.Messages.Add(new Message { Role = MessageRole.User, Content = "你好" });

                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                var turns = PromptAssembler.BuildHistoryTurns(ctx, values);
                Check.Equal("【用户说】你好", turns[0].Content, "模板生效");
            });

            Runner.Add("提示词", "历史裁剪跳过已压缩区间", delegate
            {
                var ctx = new PromptContext { Profile = new Profile() };
                ctx.Conversation = new Conversation();
                for (int i = 0; i < 6; i++)
                {
                    ctx.Conversation.Messages.Add(new Message { Role = MessageRole.User, Content = "消息" + i });
                }
                ctx.Conversation.SummarizedUpTo = 3;   // 前四条已进摘要

                var turns = PromptAssembler.BuildHistoryTurns(ctx, new Dictionary<string, string>());
                Check.EqualInt(2, turns.Count, "只剩两条");
                Check.Equal("消息4", turns[0].Content, "从第 5 条开始");
            });

            Runner.Add("提示词", "工具结果按新旧截断", delegate
            {
                var ctx = new PromptContext { Profile = new Profile() };
                ctx.Conversation = new Conversation();

                ctx.Conversation.Messages.Add(new Message { Role = MessageRole.User, Content = "开始" });
                for (int i = 0; i < Limits.KeepFullToolResults + 2; i++)
                {
                    var a = new Message { Role = MessageRole.Assistant, Content = "" };
                    a.ToolCalls.Add(new ToolCallRecord { Id = "c" + i, Name = "read_file" });
                    ctx.Conversation.Messages.Add(a);
                    ctx.Conversation.Messages.Add(new Message
                    {
                        Role = MessageRole.Tool,
                        ToolCallId = "c" + i,
                        Content = new string('x', 2000)
                    });
                }

                var turns = PromptAssembler.BuildHistoryTurns(ctx, new Dictionary<string, string>());
                var toolTurns = turns.Where(delegate (ChatTurn t) { return t.Role == "tool"; }).ToList();

                Check.True(toolTurns[0].Content.Length <= Limits.TrimmedToolResultChars + 1, "最早的工具结果被截断");
                Check.EqualInt(2000, toolTurns[toolTurns.Count - 1].Content.Length, "最近的保持完整");
            });
        }

        // ═════════════════════════════════════════════════════════════════
        // 权限
        // ═════════════════════════════════════════════════════════════════
        private static void RegisterPermissions()
        {
            Runner.Add("权限", "敏感工具清单正确", delegate
            {
                Check.True(SensitiveTools.IsSensitive("write_file"), "写文件敏感");
                Check.True(SensitiveTools.IsSensitive("send_sms"), "发短信敏感");
                Check.False(SensitiveTools.IsSensitive("read_file"), "读文件不敏感");
                Check.False(SensitiveTools.IsSensitive("web_search"), "搜索不敏感");
            });

            Runner.Add("权限", "权限键按参数细分", delegate
            {
                var key1 = PermissionService.BuildKey("write_file", "{\"path\":\"notes.md\"}");
                var key2 = PermissionService.BuildKey("write_file", "{\"path\":\"other.md\"}");
                Check.Equal("write_file:notes.md", key1, "键格式");
                Check.False(key1 == key2, "不同文件不同键");

                Check.Equal("make_call:10086", PermissionService.BuildKey("make_call", "{\"number\":\"10086\"}"), "拨号按号码");
            });

            Runner.Add("权限", "非敏感工具直接放行、不弹框", delegate
            {
                var store = new InMemoryFileStore();
                var service = new PermissionService(store, NullLogger.Instance);
                service.LoadAsync().Wait();

                var ui = new FakeInteraction();
                var denied = service.CheckAsync("read_file", "read_file:a.md", null, ui, CancellationToken.None).Result;
                Check.True(denied == null, "应当放行");
                Check.EqualInt(0, ui.ConfirmCalls, "不应弹框");
            });

            Runner.Add("权限", "敏感工具首次询问、批准后记住", delegate
            {
                var store = new InMemoryFileStore();
                var service = new PermissionService(store, NullLogger.Instance);
                service.LoadAsync().Wait();

                var ui = new FakeInteraction { ApproveResult = true };
                var request = PermissionService.BuildRequest("write_file", "write_file:notes.md", "{\"path\":\"notes.md\"}");

                var first = service.CheckAsync("write_file", "write_file:notes.md", request, ui, CancellationToken.None).Result;
                Check.True(first == null, "批准后放行");
                Check.EqualInt(1, ui.ConfirmCalls, "弹了一次");

                var second = service.CheckAsync("write_file", "write_file:notes.md", request, ui, CancellationToken.None).Result;
                Check.True(second == null, "仍然放行");
                Check.EqualInt(1, ui.ConfirmCalls, "第二次不再弹框");

                Check.True(store.Peek(WorkspaceLayout.SharedPermissions) != null, "审批记录已落盘");
            });

            Runner.Add("权限", "拒绝时返回给模型的可读说明", delegate
            {
                var store = new InMemoryFileStore();
                var service = new PermissionService(store, NullLogger.Instance);
                service.LoadAsync().Wait();

                var ui = new FakeInteraction { ApproveResult = false };
                var request = PermissionService.BuildRequest("send_sms", "send_sms:10086", "{\"number\":\"10086\",\"message\":\"hi\"}");

                var denied = service.CheckAsync("send_sms", "send_sms:10086", request, ui, CancellationToken.None).Result;
                Check.True(denied != null, "应当返回说明");
                Check.Contains(denied, "拒绝", "说明里含拒绝字样");
                Check.False(service.IsGranted("send_sms:10086"), "不应被记住");
            });

            Runner.Add("权限", "FullTrust 跳过所有审批", delegate
            {
                var service = new PermissionService(new InMemoryFileStore(), NullLogger.Instance);
                service.LoadAsync().Wait();
                service.FullTrust = true;

                var ui = new FakeInteraction();
                var request = PermissionService.BuildRequest("write_file", "k", "{}");
                var result = service.CheckAsync("write_file", "k", request, ui, CancellationToken.None).Result;

                Check.True(result == null, "直接放行");
                Check.EqualInt(0, ui.ConfirmCalls, "不弹框");
            });

            Runner.Add("权限", "审批说明包含关键参数", delegate
            {
                var request = PermissionService.BuildRequest("write_file", "k", "{\"path\":\"TimelineProjection.cs\"}");
                Check.Contains(request.Title, "写入文件", "标题");
                Check.EqualInt(1, request.Fields.Count, "一个字段");
                Check.Equal("TimelineProjection.cs", request.Fields[0].Value, "字段值");
            });

            Runner.Add("权限", "撤销全部授权", delegate
            {
                var store = new InMemoryFileStore();
                var service = new PermissionService(store, NullLogger.Instance);
                service.LoadAsync().Wait();
                service.GrantAsync("write_file:a.md").Wait();
                Check.True(service.IsGranted("write_file:a.md"), "已授权");
                service.RevokeAllAsync().Wait();
                Check.False(service.IsGranted("write_file:a.md"), "已撤销");
            });
        }

        // ═════════════════════════════════════════════════════════════════
        // 设置模型
        // ═════════════════════════════════════════════════════════════════
        private static void RegisterSettingsModel()
        {
            Runner.Add("设置", "默认值合理且 Repair 修坏值", delegate
            {
                var settings = new GlobalSettings();
                Check.Equal("dark", settings.Theme, "默认暗色");
                Check.Equal("send", settings.EnterBehavior, "默认回车发送");
                Check.EqualInt(5, settings.Search.MaxResults, "默认结果数");

                settings.Search.MaxResults = 0;
                settings.HistoryPageSize = -3;
                settings.Repair();
                Check.EqualInt(5, settings.Search.MaxResults, "坏值修复");
                Check.EqualInt(20, settings.HistoryPageSize, "坏值修复");
            });

            Runner.Add("设置", "模型档案缺 URL 时补默认端点", delegate
            {
                var openai = new ApiProfile { Provider = "openai" };
                Check.Equal("https://api.openai.com/v1/chat/completions", openai.EffectiveUrl, "OpenAI 默认");
                var claude = new ApiProfile { Provider = "anthropic" };
                Check.Equal("https://api.anthropic.com/v1/messages", claude.EffectiveUrl, "Anthropic 默认");
                Check.True(claude.IsAnthropic, "识别为 Anthropic");
            });

            Runner.Add("设置", "模型档案三级解析", delegate
            {
                var file = new ApiProfileFile();
                file.Items.Add(new ApiProfile { Id = "a", Name = "A" });
                file.Items.Add(new ApiProfile { Id = "b", Name = "B" });
                file.DefaultId = "b";

                Check.Equal("A", file.Resolve("a").Name, "档案指定优先");
                Check.Equal("B", file.Resolve("missing").Name, "找不到指定项时回退全局默认");

                file.DefaultId = "";
                Check.Equal("A", file.Resolve("").Name, "没有默认项时回退第一个");

                var empty = new ApiProfileFile();
                Check.True(empty.Resolve("") == null, "空集合返回 null");
            });

            Runner.Add("设置", "密钥不落盘在配置文件里", delegate
            {
                var profile = new ApiProfile { Name = "n" };
                var json = JsonStore.Serialize(profile);
                Check.NotContains(json, "apiKey", "不应有 apiKey 字段");
                Check.Equal("api." + profile.Id, profile.SecretKey, "凭据键规则");
            });

            Runner.Add("设置", "好感度钳制在 0–100", delegate
            {
                var state = new AffinityState { Value = 98 };
                state.Apply(10);
                Check.EqualInt(100, state.Value, "上限钳制");
                Check.Equal("up", state.Trend, "趋势向上");

                state.Apply(-999);
                Check.EqualInt(0, state.Value, "下限钳制");
                Check.Equal("down", state.Trend, "趋势向下");
            });
        }

        // ═════════════════════════════════════════════════════════════════
        // 样例构造
        // ═════════════════════════════════════════════════════════════════
        private static Conversation BuildConversationWithTurn()
        {
            var conv = new Conversation { Id = "conv1", ProfileId = "p1" };
            conv.Messages.Add(new Message { Role = MessageRole.User, Content = "帮我看看 notes.md" });

            var assistant = new Message
            {
                Role = MessageRole.Assistant,
                Content = "",
                Reasoning = "先读文件",
                IsFinal = false
            };
            assistant.ToolCalls.Add(new ToolCallRecord
            {
                Id = "call1",
                Name = "read_file",
                ArgumentsJson = "{\"path\":\"notes.md\"}"
            });
            conv.Messages.Add(assistant);

            conv.Messages.Add(new Message
            {
                Role = MessageRole.Tool,
                ToolCallId = "call1",
                Content = "文件内容在此"
            });

            conv.Messages.Add(new Message
            {
                Role = MessageRole.Assistant,
                Content = "最终答案",
                IsFinal = true
            });

            return conv;
        }

        private static ApiProfile MakeApi(bool anthropic)
        {
            return new ApiProfile
            {
                Id = "api1",
                Name = "测试",
                Provider = anthropic ? "anthropic" : "openai",
                Url = anthropic ? "https://api.anthropic.com/v1/messages" : "https://api.deepseek.com/v1/chat/completions",
                Model = anthropic ? "claude-sonnet-4-20250514" : "deepseek-chat"
            };
        }

        private static ChatRequest BuildSampleRequest(bool anthropic)
        {
            var request = new ChatRequest
            {
                Api = MakeApi(anthropic),
                ApiKey = "sk-test",
                Temperature = 0.7
            };

            request.Turns.Add(ChatTurn.System("你是助手"));
            request.Turns.Add(ChatTurn.User("帮我看看 notes.md"));

            var assistant = ChatTurn.Assistant("");
            assistant.Reasoning = "先看文件";
            assistant.ToolCalls.Add(new ToolCallRequest { Id = "call1", Name = "read_file", ArgumentsJson = "{\"path\":\"notes.md\"}" });
            request.Turns.Add(assistant);

            request.Turns.Add(ChatTurn.ToolResult("call1", "read_file", "文件内容"));
            return request;
        }
    }
}
