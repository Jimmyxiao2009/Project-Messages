// tests/CoreTests/Feature10Tests.cs —— 副手模型、回复建议、AI 标题、朗读模型
//
// 副手模型的输出格式最不可靠（小模型爱加编号、前言、代码块），解析要宽进严出；
// 朗读模型要验证三件事：请求形状对、出错能读懂、失败时退回系统语音而不是静默。

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tests
{
    public static class Feature10Tests
    {
        public static void Register()
        {
            RegisterParsing();
            RegisterAssistantModel();
            RegisterFollowUps();
            RegisterCloudSpeech();
        }

        // ═════════════ 解析 ═════════════
        private static void RegisterParsing()
        {
            Runner.Add("回复建议", "解析 JSON 数组，忽略前言与代码块", delegate
            {
                var output = "好的，以下是建议：\n```json\n[\"能举个例子吗\", \"换个更简单的说法\", \"下一步做什么\"]\n```";
                var list = AssistPrompts.ParseSuggestions(output);
                Check.EqualInt(3, list.Count, "三条");
                Check.Equal("能举个例子吗", list[0], "第一条");
            });

            Runner.Add("回复建议", "不是 JSON 时按行解析并去掉编号", delegate
            {
                var list = AssistPrompts.ParseSuggestions("建议：\n1. 继续说\n2) 太长了，精简一下\n- 用英文再说一遍");
                Check.EqualInt(3, list.Count, "三条");
                Check.Equal("继续说", list[0], "去掉 1.");
                Check.Equal("太长了，精简一下", list[1], "去掉 2)");
                Check.Equal("用英文再说一遍", list[2], "去掉 -");
            });

            Runner.Add("回复建议", "去重、丢掉过长的、最多三条", delegate
            {
                var tooLong = new string('长', AssistPrompts.SuggestionMaxChars + 1);
                var list = AssistPrompts.ParseSuggestions("[\"好\", \"好\", \"" + tooLong + "\", \"一\", \"二\", \"三\"]");
                Check.EqualInt(3, list.Count, "截到三条");
                Check.Equal("好", list[0], "去重后保留第一个");
                Check.Equal("一", list[1], "过长的被跳过");
            });

            Runner.Add("回复建议", "空输出返回空表", delegate
            {
                Check.EqualInt(0, AssistPrompts.ParseSuggestions(null).Count, "null");
                Check.EqualInt(0, AssistPrompts.ParseSuggestions("   ").Count, "空白");
            });

            Runner.Add("AI 标题", "去掉标签、引号、书名号和句末标点", delegate
            {
                Check.Equal("周报改写建议", AssistPrompts.CleanTitle("标题：《周报改写建议》。"), "中文");
                Check.Equal("Weekly report", AssistPrompts.CleanTitle("\"Weekly report\"\n\n解释……"), "只取第一行");
                Check.True(AssistPrompts.CleanTitle(new string('字', 50)).Length <= AssistPrompts.TitleMaxChars, "截断");
            });

            Runner.Add("回复建议", "提示词只带最近几条对话", delegate
            {
                var messages = new List<Message>();
                for (int i = 0; i < 10; i++)
                    messages.Add(new Message { Role = i % 2 == 0 ? MessageRole.User : MessageRole.Assistant, Content = "第" + i + "条" });
                var prompt = AssistPrompts.BuildSuggestionPrompt(messages, "小满", "阿杰");
                Check.Contains(prompt, "第9条", "最后一条在");
                Check.False(prompt.Contains("第0条"), "太早的不带");
                Check.Contains(prompt, "阿杰", "带用户名");
                Check.Contains(prompt, "JSON", "要求 JSON");
            });
        }

        // ═════════════ 副手模型解析 ═════════════
        private static void RegisterAssistantModel()
        {
            Runner.Add("副手模型", "全局指定优先，且不改动存储里的 Provider", delegate
            {
                var providers = Providers();
                var assistant = new ModelRef { ApiProfileId = "cheap", Model = "mini" };
                var api = ModelSelection.ResolveAssistant(providers, assistant, null, null);
                Check.Equal("cheap", api.Id, "用指定的 Provider");
                Check.Equal("mini", api.Model, "用指定的模型");
                Check.Equal("small-default", providers.Find("cheap").Model, "存储里的默认模型没变");
            });

            Runner.Add("副手模型", "未指定时退回旧的 subModel，再退回对话模型", delegate
            {
                var providers = Providers();
                var api = ModelSelection.ResolveAssistant(providers, new ModelRef(), null, null);
                Check.Equal("main", api.Id, "跟随默认 Provider");
                Check.Equal("main-sub", api.Model, "用 Provider 的 subModel");

                providers.Find("main").SubModel = "";
                api = ModelSelection.ResolveAssistant(providers, new ModelRef(), null, null);
                Check.Equal("big", api.Model, "退回对话模型");
            });

            Runner.Add("副手模型", "指定的 Provider 被删后退回对话模型", delegate
            {
                var providers = Providers();
                providers.Find("main").SubModel = "";
                var api = ModelSelection.ResolveAssistant(providers, new ModelRef { ApiProfileId = "gone", Model = "x" }, null, null);
                Check.Equal("big", api.Model, "不报错，退回对话模型");
            });

            Runner.Add("副手模型", "旧设置文件没有新字段也能修复", delegate
            {
                var settings = JsonBridge.Deserialize<GlobalSettings>("{\"theme\":\"dark\"}");
                settings.Repair();
                Check.True(settings.AssistantModel != null && !settings.AssistantModel.IsSet, "副手模型为空引用");
                Check.Equal(SpeechEngines.System, settings.SpeechEngine, "默认系统语音");
                Check.True(settings.ReplySuggestions, "默认开启回复建议");
            });
        }

        // ═════════════ 回合后任务 ═════════════
        private static void RegisterFollowUps()
        {
            Runner.Add("回复建议", "回合后用副手模型起标题并生成建议，落盘", delegate
            {
                Workspace workspace; Profile profile; InMemoryFileStore store;
                var http = new ScriptedHttp();
                http.Enqueue(Completion("周报写法"));
                http.Enqueue(Completion("[\"能再短点吗\",\"给个模板\",\"英文怎么说\"]"));
                var chat = BuildChat(http, out workspace, out profile, out store);
                workspace.Settings.AssistantModel = new ModelRef { ApiProfileId = "cheap", Model = "mini" };

                var conversation = workspace.CreateConversation(profile, "帮我改周报");
                conversation.TitleSource = "auto";
                conversation.Messages.Add(new Message { Role = MessageRole.User, Content = "帮我改周报" });
                var reply = new Message { Role = MessageRole.Assistant, Content = "好的，先说结论。", IsFinal = true };
                conversation.Messages.Add(reply);
                chat.OpenProfileAsync(profile).Wait();
                chat.OpenConversationAsync(conversation).Wait();

                var result = chat.RunFollowUpsAsync(CancellationToken.None).Result;

                Check.Equal("周报写法", result.Title, "AI 标题");
                Check.Equal("ai", conversation.TitleSource, "标题来源改成 ai");
                Check.EqualInt(3, conversation.CurrentSuggestions().Count, "三条建议");
                Check.Equal(reply.Id, conversation.SuggestionsFor, "建议绑定到这条回复");
                Check.EqualInt(2, http.Requests.Count, "两次请求");
                Check.Contains(http.Requests[0].Url, "cheap.example", "走副手模型的 Provider");
                Check.Equal("mini", (string)JObject.Parse(http.Requests[0].Body)["model"], "用副手模型");

                var saved = workspace.LoadConversationAsync(profile.Slug, conversation.Id).Result;
                Check.Equal("周报写法", saved.Title, "标题已落盘");
                Check.EqualInt(3, saved.CurrentSuggestions().Count, "建议已落盘");
            });

            Runner.Add("回复建议", "用户起的标题不会被 AI 覆盖", delegate
            {
                Workspace workspace; Profile profile; InMemoryFileStore store;
                var http = new ScriptedHttp();
                http.Enqueue(Completion("[\"好\"]"));
                var chat = BuildChat(http, out workspace, out profile, out store);
                var conversation = workspace.CreateConversation(profile, "我自己起的");
                conversation.Messages.Add(new Message { Role = MessageRole.Assistant, Content = "嗨", IsFinal = true });
                chat.OpenProfileAsync(profile).Wait();
                chat.OpenConversationAsync(conversation).Wait();

                chat.RunFollowUpsAsync(CancellationToken.None).Wait();
                Check.Equal("我自己起的", conversation.Title, "标题不变");
                Check.EqualInt(1, http.Requests.Count, "只请求了建议");
            });

            Runner.Add("回复建议", "最后一条不是对应的回复时不显示旧建议", delegate
            {
                var conversation = new Conversation();
                var reply = new Message { Role = MessageRole.Assistant, Content = "a" };
                conversation.Messages = new List<Message> { reply };
                conversation.Suggestions = new List<string> { "继续" };
                conversation.SuggestionsFor = reply.Id;
                Check.EqualInt(1, conversation.CurrentSuggestions().Count, "匹配时显示");
                conversation.Messages.Add(new Message { Role = MessageRole.User, Content = "继续" });
                Check.EqualInt(0, conversation.CurrentSuggestions().Count, "用户已回复后不显示");
            });

            Runner.Add("回复建议", "关掉开关后不请求", delegate
            {
                Workspace workspace; Profile profile; InMemoryFileStore store;
                var http = new ScriptedHttp();
                var chat = BuildChat(http, out workspace, out profile, out store);
                workspace.Settings.ReplySuggestions = false;
                workspace.Settings.AiTitles = false;
                var conversation = workspace.CreateConversation(profile, "x");
                conversation.TitleSource = "auto";
                conversation.Messages.Add(new Message { Role = MessageRole.Assistant, Content = "嗨", IsFinal = true });
                chat.OpenProfileAsync(profile).Wait();
                chat.OpenConversationAsync(conversation).Wait();
                chat.RunFollowUpsAsync(CancellationToken.None).Wait();
                Check.EqualInt(0, http.Requests.Count, "没有请求");
            });
        }

        // ═════════════ 朗读模型 ═════════════
        private static void RegisterCloudSpeech()
        {
            Runner.Add("朗读模型", "端点由 Provider 地址推出", delegate
            {
                Check.Equal("https://api.openai.com/v1/audio/speech", ProviderEndpoints.SpeechUrl(""), "默认官方");
                Check.Equal("https://x.example/v1/audio/speech", ProviderEndpoints.SpeechUrl("https://x.example/v1/chat/completions"), "去掉 chat/completions");
                Check.Equal("https://x.example/v1/audio/speech", ProviderEndpoints.SpeechUrl("https://x.example"), "补 /v1");
            });

            Runner.Add("朗读模型", "请求体：模型、文字、音色、mp3；正常语速不发 speed", delegate
            {
                var target = new CloudSpeechTarget { Api = new ApiProfile { Url = "https://x.example/v1", Model = "tts-1" }, ApiKey = "k", Voice = "nova" };
                var spec = CloudSpeech.BuildRequest(target, "你好", 1.0);
                var body = JObject.Parse(spec.Body);
                Check.Equal("tts-1", (string)body["model"], "模型");
                Check.Equal("你好", (string)body["input"], "文字");
                Check.Equal("nova", (string)body["voice"], "音色");
                Check.Equal("mp3", (string)body["response_format"], "格式");
                Check.True(body["speed"] == null, "正常语速不发");
                Check.True(spec.ExpectBinary, "要二进制响应");
                Check.Equal("Bearer k", spec.Headers["Authorization"], "鉴权头");

                body = JObject.Parse(CloudSpeech.BuildRequest(target, "x", 1.5).Body);
                Check.True(Math.Abs((double)body["speed"] - 1.5) < 0.001, "语速");
            });

            Runner.Add("朗读模型", "错误响应读出服务端的说明", delegate
            {
                var result = CloudSpeech.Interpret(new HttpResponseSpec { StatusCode = 404, Body = "{\"error\":{\"message\":\"model not found\"}}" });
                Check.False(result.Ok, "失败");
                Check.Contains(result.Error, "404", "状态码");
                Check.Contains(result.Error, "model not found", "服务端说明");

                result = CloudSpeech.Interpret(new HttpResponseSpec { StatusCode = 200, Body = "{\"message\":\"余额不足\"}" });
                Check.False(result.Ok, "200 但没有音频也算失败");
                Check.Contains(result.Error, "余额不足", "读出说明");
            });

            Runner.Add("朗读模型", "Anthropic / Gemini 明确说不支持", delegate
            {
                Check.True(CloudSpeech.Unsupported(new ApiProfile { Provider = "anthropic", Model = "x" }).Length > 0, "anthropic");
                Check.True(CloudSpeech.Unsupported(new ApiProfile { Provider = "gemini", Model = "x" }).Length > 0, "gemini");
                Check.Equal("", CloudSpeech.Unsupported(new ApiProfile { Provider = "openai", Model = "tts-1" }), "openai 可以");
            });

            Runner.Add("朗读模型", "每段都走朗读模型播放，不碰系统语音", delegate
            {
                var http = new ScriptedHttp();
                for (int i = 0; i < 10; i++) http.Enqueue(Audio());
                var system = new RecordingSynth();
                var player = new RecordingPlayer();
                var tts = new TtsService(system, NullLogger.Instance, http, player);
                var settings = CloudSettings();
                settings.MaxChunkChars = 20;

                tts.SpeakAsync(LongText(), settings, CancellationToken.None).Wait();

                Check.True(player.Played >= 2, "分段播放了多段");
                Check.EqualInt(player.Played, http.Requests.Count, "每段一次合成");
                Check.EqualInt(0, system.Spoken.Count, "系统语音没用上");
            });

            Runner.Add("朗读模型", "合成失败时提示原因并改用系统语音读完", delegate
            {
                var http = new ScriptedHttp();
                http.Enqueue(new HttpResponseSpec { StatusCode = 401, Body = "{\"error\":{\"message\":\"bad key\"}}" });
                var system = new RecordingSynth();
                var player = new RecordingPlayer();
                var tts = new TtsService(system, NullLogger.Instance, http, player);
                string reason = null;
                tts.Fallback += delegate (string r) { reason = r; };

                tts.SpeakAsync("第一句。第二句。", CloudSettings(), CancellationToken.None).Wait();

                Check.Contains(reason ?? "", "bad key", "告诉用户为什么");
                Check.True(system.Spoken.Count > 0, "系统语音接着读");
                Check.EqualInt(0, player.Played, "没播坏音频");
            });

            Runner.Add("朗读模型", "选了朗读模型但 Provider 已删：提示并用系统语音", delegate
            {
                var store = new InMemoryFileStore();
                var workspace = new Workspace(store, new InMemorySecretStore(), NullLogger.Instance, SystemClock.Instance);
                workspace.InitializeAsync().Wait();
                workspace.Settings.SpeechEngine = SpeechEngines.Model;
                workspace.Settings.SpeechModel = new ModelRef { ApiProfileId = "gone", Model = "tts-1" };
                var settings = TtsSettings.From(workspace);
                Check.True(settings.Cloud == null, "不走朗读模型");
                Check.Contains(settings.CloudProblem, "已删除", "说明原因");

                var system = new RecordingSynth();
                var tts = new TtsService(system, NullLogger.Instance, new ScriptedHttp(), new RecordingPlayer());
                string reason = null;
                tts.Fallback += delegate (string r) { reason = r; };
                tts.SpeakAsync("你好。", settings, CancellationToken.None).Wait();
                Check.True(reason != null, "提示了");
                Check.True(system.Spoken.Count > 0, "系统语音读了");
            });

            Runner.Add("朗读模型", "停止后不再播放后续段", delegate
            {
                var http = new ScriptedHttp();
                for (int i = 0; i < 10; i++) http.Enqueue(Audio());
                var player = new RecordingPlayer();
                var tts = new TtsService(new RecordingSynth(), NullLogger.Instance, http, player);
                player.OnPlay = delegate { tts.StopAsync().Wait(); };
                var settings = CloudSettings();
                settings.MaxChunkChars = 20;
                tts.SpeakAsync(LongText(), settings, CancellationToken.None).Wait();
                Check.EqualInt(1, player.Played, "第一段之后就停了");
            });
        }

        // ═════════════ 辅助 ═════════════
        private static ApiProfileFile Providers()
        {
            var file = new ApiProfileFile { DefaultId = "main" };
            file.Items.Add(new ApiProfile { Id = "main", Name = "主", Model = "big", SubModel = "main-sub", RequiresApiKey = false });
            file.Items.Add(new ApiProfile { Id = "cheap", Name = "便宜", Model = "small-default", RequiresApiKey = false, Url = "https://cheap.example/v1" });
            return file;
        }

        private static ChatService BuildChat(IHttpTransport http, out Workspace workspace, out Profile profile, out InMemoryFileStore store)
        {
            store = new InMemoryFileStore();
            workspace = new Workspace(store, new InMemorySecretStore(), NullLogger.Instance, SystemClock.Instance);
            workspace.InitializeAsync().Wait();
            foreach (var api in Providers().Items) workspace.ApiProfiles.Items.Add(api);
            workspace.ApiProfiles.DefaultId = "main";
            profile = workspace.CreateProfileAsync("小满", "xm").Result;
            return new ChatService(workspace, store, NullLogger.Instance, SystemClock.Instance,
                http, null, null, DenyAllInteraction.Instance, ToolRegistry.CreateDefault());
        }

        private static HttpResponseSpec Completion(string content)
        {
            var body = new JObject
            {
                ["choices"] = new JArray(new JObject { ["message"] = new JObject { ["role"] = "assistant", ["content"] = content }, ["finish_reason"] = "stop" })
            };
            return new HttpResponseSpec { StatusCode = 200, Body = body.ToString(), ContentType = "application/json" };
        }

        private static HttpResponseSpec Audio()
        {
            return new HttpResponseSpec { StatusCode = 200, Body = "", BodyBytes = new byte[] { 1, 2, 3 }, ContentType = "audio/mpeg" };
        }

        private static TtsSettings CloudSettings()
        {
            return new TtsSettings
            {
                Cloud = new CloudSpeechTarget { Api = new ApiProfile { Provider = "openai", Model = "tts-1", RequiresApiKey = false }, Voice = "alloy" }
            };
        }

        private static string LongText()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 6; i++) sb.Append("这是第").Append(i).Append("句比较长的话，用来切成好几段。\n\n");
            return sb.ToString();
        }

        private sealed class ScriptedHttp : IHttpTransport
        {
            private readonly Queue<HttpResponseSpec> _queue = new Queue<HttpResponseSpec>();
            public readonly List<HttpRequestSpec> Requests = new List<HttpRequestSpec>();
            private readonly object _gate = new object();

            public void Enqueue(HttpResponseSpec response) { _queue.Enqueue(response); }

            public Task<HttpResponseSpec> SendAsync(HttpRequestSpec request, CancellationToken ct)
            {
                lock (_gate)
                {
                    Requests.Add(request);
                    var response = _queue.Count > 0 ? _queue.Dequeue() : new HttpResponseSpec { StatusCode = 500, Body = "{}" };
                    return Task.FromResult(response);
                }
            }

            public Task StreamLinesAsync(HttpRequestSpec request, Func<string, bool> onLine, CancellationToken ct)
            {
                return Task.CompletedTask;
            }
        }

        private sealed class RecordingSynth : ISpeechSynthesizer
        {
            public readonly List<string> Spoken = new List<string>();
            public bool IsAvailable { get { return true; } }
            public Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct) { IReadOnlyList<string> v = new List<string>(); return Task.FromResult(v); }
            public Task SpeakAsync(SpeechRequest request, CancellationToken ct) { Spoken.Add(request.Text); return Task.CompletedTask; }
            public Task StopAsync() { return Task.CompletedTask; }
        }

        private sealed class RecordingPlayer : IAudioPlayback
        {
            public int Played;
            public Action OnPlay;
            public Task PlayAsync(byte[] audio, string contentType, double volume, CancellationToken ct)
            {
                Played++;
                if (OnPlay != null) OnPlay();
                return Task.CompletedTask;
            }
            public Task StopAsync() { return Task.CompletedTask; }
        }
    }
}
