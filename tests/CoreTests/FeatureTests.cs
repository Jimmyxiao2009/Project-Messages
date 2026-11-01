// tests/CoreTests/FeatureTests.cs —— 角色卡 / 工作区 / 压缩 / 日志
//
// PNG 卡解析是最容易写错的一处（chunk 长度、zlib 包装、base64 嵌套），
// 这里用合成 PNG 把 tEXt 与 zTXt 两条路径都覆盖到。
// 工作区测试验证"一个档案一个文件夹"、"删除进回收区"、"索引与正文分离"。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tests
{
    public sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        public string Get(string key)
        {
            string value;
            return _values.TryGetValue(key ?? "", out value) ? value : "";
        }

        public void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            _values[key] = value ?? "";
        }

        public void Remove(string key)
        {
            if (!string.IsNullOrEmpty(key)) _values.Remove(key);
        }

        public int Count { get { return _values.Count; } }
    }

    public static class FeatureTests
    {
        private const string SampleCardJson = @"{
  ""spec"": ""chara_card_v2"",
  ""spec_version"": ""2.0"",
  ""data"": {
    ""name"": ""林深"",
    ""description"": ""冷静的工程师，说话很少"",
    ""personality"": ""寡言、严谨"",
    ""scenario"": ""深夜的机房"",
    ""first_mes"": ""还没睡？"",
    ""mes_example"": ""<START>\n{{user}}: 在吗\n{{char}}: 在。"",
    ""creator"": ""someone"",
    ""tags"": [""工程"", ""中文""],
    ""alternate_greetings"": [""又来加班？""],
    ""extensions"": { ""talkativeness"": ""0.3"" },
    ""character_book"": {
      ""entries"": [
        { ""comment"": ""世界观"", ""content"": ""这个世界用蒸汽驱动"", ""keys"": [""蒸汽""], ""constant"": false, ""disable"": false, ""order"": 50 },
        { ""comment"": ""禁用的条目"", ""content"": ""不该出现"", ""keys"": [""x""], ""disable"": true }
      ]
    }
  }
}";

        public static void Register()
        {
            // ═════════════ 角色卡 ═════════════
            Runner.Add("角色卡", "JSON v2 字段全部落位", delegate
            {
                var result = CardImporter.ImportJson(SampleCardJson, "card.json");
                Check.True(result.Ok, "应当成功");
                Check.Equal("林深", result.Profile.Persona.Name, "名称");
                Check.Equal("冷静的工程师，说话很少", result.Profile.Persona.Description, "描述");
                Check.Equal("寡言、严谨", result.Profile.Persona.Personality, "性格");
                Check.Equal("深夜的机房", result.Profile.Persona.Scenario, "场景");
                Check.Equal("还没睡？", result.Profile.Persona.FirstMessage, "首句");
                Check.Contains(result.Profile.Persona.ExampleDialogue, "{{char}}", "示例对话");
                Check.EqualInt(2, result.Profile.Persona.Tags.Count, "标签数");
                Check.EqualInt(2, result.CardSpecVersion, "规范版本");
                // 这张卡填了：描述 / 性格 / 场景 / 首句 / 示例对话
                Check.EqualInt(5, result.FilledFieldCount, "解析到的字段数");
            });

            Runner.Add("角色卡", "备用开场白与未知扩展不丢失", delegate
            {
                var result = CardImporter.ImportJson(SampleCardJson, "card.json");
                Check.True(result.Profile.Persona.Extensions.ContainsKey("alternateGreetings"), "备用开场白被保留");
                Check.True(result.Profile.Persona.Extensions.ContainsKey("talkativeness"), "未知扩展被保留");
            });

            Runner.Add("角色卡", "内嵌世界书被解析且跳过禁用条目", delegate
            {
                var result = CardImporter.ImportJson(SampleCardJson, "card.json");
                Check.True(result.Lorebook != null, "有世界书");
                Check.EqualInt(1, result.Lorebook.Entries.Count, "禁用条目被跳过");
                Check.Equal("世界观", result.Lorebook.Entries[0].Name, "条目名");
                Check.EqualInt(1, result.Lorebook.Entries[0].Keywords.Count, "关键词");
                Check.EqualInt(50, result.Lorebook.Entries[0].Priority, "排序值");
            });

            Runner.Add("角色卡", "空卡明确报错而不是静默成功", delegate
            {
                var result = CardImporter.ImportJson("{\"spec\":\"chara_card_v2\",\"data\":{\"name\":\"空卡\"}}", "c.json");
                Check.False(result.Ok, "应当失败");
                Check.Contains(result.Error, "没有解析到任何可用内容", "错误说明");
            });

            Runner.Add("角色卡", "坏 JSON 给出可读错误", delegate
            {
                var result = CardImporter.ImportJson("这不是 JSON", "c.json");
                Check.False(result.Ok, "应当失败");
                Check.Contains(result.Error, "JSON", "错误说明");
            });

            Runner.Add("角色卡", "PNG 的 tEXt 块能解析出来", delegate
            {
                var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(SampleCardJson));
                var png = BuildPng(BuildTextChunk("chara", base64));

                var result = CardImporter.Import(png, "card.png");
                Check.True(result.Ok, "应当成功");
                Check.Equal("林深", result.Profile.Persona.Name, "从 PNG 里读到名字");
                Check.True(result.ImageBytes != null && result.ImageBytes.Length == png.Length, "原图字节被带回");
            });

            Runner.Add("角色卡", "PNG 的 zTXt（zlib 压缩）块能解析出来", delegate
            {
                var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(SampleCardJson));
                var png = BuildPng(BuildZtxtChunk("chara", base64));

                var result = CardImporter.Import(png, "card.png");
                Check.True(result.Ok, "应当成功");
                Check.Equal("林深", result.Profile.Persona.Name, "解压后读到名字");
            });

            Runner.Add("角色卡", "没有卡块的 PNG 给出可操作提示", delegate
            {
                var png = BuildPng(BuildTextChunk("Author", "someone"));
                var result = CardImporter.Import(png, "photo.png");
                Check.False(result.Ok, "应当失败");
                Check.Contains(result.Error, "头像", "提示可以当头像用");
            });

            Runner.Add("角色卡", "超过 5MB 的 PNG 被拒绝", delegate
            {
                var big = new byte[CardImporter.MaxPngBytes + 1];
                Buffer.BlockCopy(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, big, 0, 8);
                var result = CardImporter.Import(big, "huge.png");
                Check.False(result.Ok, "应当失败");
                Check.Contains(result.Error, "5MB", "提示体积上限");
            });

            Runner.Add("角色卡", "畸形块长度不会导致死循环", delegate
            {
                // 手工造一个长度字段越界的块，解析应当立刻停下而不是死循环
                var bytes = new List<byte>();
                bytes.AddRange(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
                bytes.AddRange(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF });          // 长度极大
                bytes.AddRange(Encoding.ASCII.GetBytes("tEXt"));
                bytes.AddRange(new byte[] { 1, 2, 3, 4 });                        // 随便几个字节

                var result = CardImporter.Import(bytes.ToArray(), "broken.png");
                Check.False(result.Ok, "应当失败而不是卡死");
            });

            Runner.Add("角色卡", "导出成 v2 JSON 可回读", delegate
            {
                var imported = CardImporter.ImportJson(SampleCardJson, "card.json");
                var exported = CardImporter.ExportJson(imported.Profile);

                var reread = CardImporter.ImportJson(exported, "again.json");
                Check.True(reread.Ok, "回读成功");
                Check.Equal("林深", reread.Profile.Persona.Name, "名称往返一致");
                Check.Equal("深夜的机房", reread.Profile.Persona.Scenario, "场景往返一致");
                Check.EqualInt(2, reread.Profile.Persona.Tags.Count, "标签往返一致");
            });

            // ═════════════ 工作区 ═════════════
            Runner.Add("工作区", "空工作区初始化得到合理默认值", delegate
            {
                var workspace = MakeWorkspace(out InMemoryFileStore store, out InMemorySecretStore secrets);
                workspace.InitializeAsync().Wait();

                Check.EqualInt(0, workspace.Profiles.Count, "没有档案");
                Check.Equal("dark", workspace.Settings.Theme, "默认主题");
                Check.True(workspace.ApiProfiles.Items != null, "模型档案集合已初始化");
            });

            Runner.Add("工作区", "新建档案落在独立文件夹里", delegate
            {
                var workspace = MakeWorkspace(out InMemoryFileStore store, out InMemorySecretStore secrets);
                workspace.InitializeAsync().Wait();

                var profile = workspace.CreateProfileAsync("工程助理", null).Result;
                Check.Equal("工程助理", profile.Slug, "slug 用中文名");
                Check.True(store.Peek("工程助理/profile.json") != null, "档案文件落在自己的目录");
                Check.EqualInt(1, workspace.Profiles.Count, "档案列表已更新");
            });

            Runner.Add("工作区", "同名声档自动加后缀不互相覆盖", delegate
            {
                var workspace = MakeWorkspace(out InMemoryFileStore store, out InMemorySecretStore secrets);
                workspace.InitializeAsync().Wait();

                var first = workspace.CreateProfileAsync("助手", null).Result;
                var second = workspace.CreateProfileAsync("助手", null).Result;

                Check.Equal("助手", first.Slug, "第一个用原名");
                Check.Equal("助手-2", second.Slug, "第二个加后缀");
                Check.False(first.Slug == second.Slug, "文件夹不同");
            });

            Runner.Add("工作区", "会话正文与索引分开保存", delegate
            {
                var workspace = MakeWorkspace(out InMemoryFileStore store, out InMemorySecretStore secrets);
                workspace.InitializeAsync().Wait();
                var profile = workspace.CreateProfileAsync("测试", "test").Result;

                var conversation = workspace.CreateConversation(profile, "第一条");
                conversation.Messages.Add(new Message { Role = MessageRole.User, Content = "你好世界" });
                conversation.Messages.Add(new Message { Role = MessageRole.Assistant, Content = "回你", IsFinal = true });

                workspace.SaveConversationAsync(profile.Slug, conversation).Wait();

                Check.True(store.Peek("test/conversations/index.json") != null, "索引已写");
                Check.True(store.Peek("test/conversations/" + conversation.Id + ".json") != null, "正文已写");

                var entries = workspace.ListConversationsAsync("test", false).Result;
                Check.EqualInt(1, entries.Count, "索引里有一条");
                Check.Equal("第一条", entries[0].Title, "标题");
                Check.Contains(entries[0].Preview, "回你", "预览取最后输出");

                var loaded = workspace.LoadConversationAsync("test", conversation.Id).Result;
                Check.EqualInt(2, loaded.Messages.Count, "正文重新载入");
            });

            Runner.Add("工作区", "删除会话同时清理索引", delegate
            {
                var workspace = MakeWorkspace(out InMemoryFileStore store, out InMemorySecretStore secrets);
                workspace.InitializeAsync().Wait();
                var profile = workspace.CreateProfileAsync("测试", "test").Result;

                var conversation = workspace.CreateConversation(profile, "要删的");
                workspace.SaveConversationAsync(profile.Slug, conversation).Wait();
                workspace.DeleteConversationAsync(profile.Slug, conversation.Id).Wait();

                Check.EqualInt(0, workspace.ListConversationsAsync("test", false).Result.Count, "索引已清");
                Check.True(store.Peek("test/conversations/" + conversation.Id + ".json") == null, "正文已删");
            });

            Runner.Add("工作区", "删除档案是移入回收区而不是抹掉", delegate
            {
                var workspace = MakeWorkspace(out InMemoryFileStore store, out InMemorySecretStore secrets);
                workspace.InitializeAsync().Wait();
                var profile = workspace.CreateProfileAsync("要删的", "gone").Result;

                var conversation = workspace.CreateConversation(profile, "留个记录");
                workspace.SaveConversationAsync(profile.Slug, conversation).Wait();

                Check.True(workspace.DeleteProfileAsync(profile.Id).Result, "删除成功");
                Check.EqualInt(0, workspace.Profiles.Count, "列表里没有了");

                var files = store.ListFilesAsync(WorkspaceLayout.TrashDir, true).Result;
                Check.True(files.Count > 0, "回收区里有东西");
            });

            Runner.Add("工作区", "密钥只进凭据存储、不进配置文件", delegate
            {
                var workspace = MakeWorkspace(out InMemoryFileStore store, out InMemorySecretStore secrets);
                workspace.InitializeAsync().Wait();

                var api = new ApiProfile { Name = "DeepSeek", Url = "https://api.deepseek.com/v1/chat/completions" };
                workspace.ApiProfiles.Items.Add(api);
                workspace.SetApiKey(api, "sk-very-secret-key");
                workspace.SaveApiProfilesAsync().Wait();

                var json = store.Peek(WorkspaceLayout.SharedApiProfiles);
                Check.NotContains(json, "sk-very-secret-key", "配置文件里没有密钥");
                Check.Equal("sk-very-secret-key", secrets.Get("api." + api.Id), "密钥在凭据存储里");
                Check.Equal("sk-very-secret-key", workspace.GetApiKey(api), "能读回来");
            });

            Runner.Add("工作区", "删除模型档案同时清掉密钥", delegate
            {
                var workspace = MakeWorkspace(out InMemoryFileStore store, out InMemorySecretStore secrets);
                workspace.InitializeAsync().Wait();

                var api = new ApiProfile { Name = "临时" };
                workspace.ApiProfiles.Items.Add(api);
                workspace.SetApiKey(api, "sk-x");
                workspace.SaveApiProfilesAsync().Wait();

                workspace.DeleteApiProfileAsync(api.Id).Wait();
                Check.EqualInt(0, workspace.ApiProfiles.Items.Count, "列表已清");
                Check.Equal("", secrets.Get("api." + api.Id), "密钥已清");
            });

            Runner.Add("工作区", "会话列表排序：置顶优先再按时间", delegate
            {
                var workspace = MakeWorkspace(out InMemoryFileStore store, out InMemorySecretStore secrets);
                workspace.InitializeAsync().Wait();
                var profile = workspace.CreateProfileAsync("测试", "test").Result;

                var older = workspace.CreateConversation(profile, "旧");
                older.UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-3);
                workspace.SaveConversationAsync(profile.Slug, older).Wait();

                var pinned = workspace.CreateConversation(profile, "置顶");
                pinned.Pinned = true;
                pinned.UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-5);
                workspace.SaveConversationAsync(profile.Slug, pinned).Wait();

                var newer = workspace.CreateConversation(profile, "新");
                workspace.SaveConversationAsync(profile.Slug, newer).Wait();

                var list = workspace.ListConversationsAsync("test", false).Result;
                Check.Equal("置顶", list[0].Title, "置顶在最前");
                Check.Equal("新", list[1].Title, "然后按时间倒序");
            });

            // ═════════════ 压缩 ═════════════
            Runner.Add("压缩", "低于阈值不触发", delegate
            {
                var conversation = new Conversation();
                conversation.Messages.Add(new Message { Role = MessageRole.User, Content = "短消息" });
                Check.False(ContextCompressor.ShouldCompact(conversation, "系统", 128000), "不该触发");
            });

            Runner.Add("压缩", "超过阈值且条数足够时触发", delegate
            {
                var conversation = new Conversation();
                for (int i = 0; i < Limits.CompactMinMessages + 2; i++)
                {
                    conversation.Messages.Add(new Message { Role = MessageRole.User, Content = new string('字', 200) });
                }
                Check.True(ContextCompressor.ShouldCompact(conversation, "系统", 1000), "应当触发");
            });

            Runner.Add("压缩", "摘要是替换式写入、并推进已压缩位置", delegate
            {
                var conversation = new Conversation();
                for (int i = 0; i < 12; i++)
                {
                    conversation.Messages.Add(new Message
                    {
                        Role = i % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
                        Content = "第" + i + "条消息的内容",
                        IsFinal = i % 2 == 1
                    });
                }

                var compressor = new ContextCompressor(NullLogger.Instance);
                var result = compressor.CompactAsync(
                    conversation, "系统", 500,
                    delegate (string prompt, CancellationToken ct) { return System.Threading.Tasks.Task.FromResult("【主题】测试\n【已完成】无"); },
                    CancellationToken.None).Result;

                Check.True(result.Compacted, "应当压缩");
                Check.Equal("【主题】测试\n【已完成】无", conversation.Summary, "摘要写入");
                Check.EqualInt(12 - Limits.CompactKeepRecent - 1, conversation.SummarizedUpTo, "已压缩位置正确");

                // 再压一次：摘要被替换而不是拼接
                var second = compressor.CompactAsync(
                    conversation, "系统", 500,
                    delegate (string prompt, CancellationToken ct) { return System.Threading.Tasks.Task.FromResult("【主题】第二次"); },
                    CancellationToken.None).Result;

                if (second.Compacted)
                {
                    Check.Equal("【主题】第二次", conversation.Summary, "摘要是替换不是叠加");
                }
            });

            Runner.Add("压缩", "总结模型返回空时不改动会话", delegate
            {
                var conversation = new Conversation();
                for (int i = 0; i < 12; i++)
                {
                    conversation.Messages.Add(new Message { Role = MessageRole.User, Content = "内容" + i });
                }

                var compressor = new ContextCompressor(NullLogger.Instance);
                var result = compressor.CompactAsync(
                    conversation, "系统", 500,
                    delegate (string prompt, CancellationToken ct) { return System.Threading.Tasks.Task.FromResult(""); },
                    CancellationToken.None).Result;

                Check.False(result.Compacted, "没有压缩");
                Check.Equal("", conversation.Summary, "摘要仍是空");
                Check.EqualInt(-1, conversation.SummarizedUpTo, "位置没动");
            });

            // ═════════════ 日志 ═════════════
            Runner.Add("日志", "密钥与查询串被清洗", delegate
            {
                var cleaned = DiagnosticLog.Sanitize("调用失败 key=sk-abcdef1234567890 https://x.com/a?token=abc");
                Check.NotContains(cleaned, "sk-abcdef1234567890", "密钥被替换");
                Check.NotContains(cleaned, "token=abc", "查询串被替换");
            });

            Runner.Add("日志", "长十六进制与超长串被清洗", delegate
            {
                var cleaned = DiagnosticLog.Sanitize("hash=9f3c1a2b4d5e6f708192a3b4c5d6e7f8");
                Check.NotContains(cleaned, "9f3c1a2b4d5e6f708192a3b4c5d6e7f8", "长十六进制被替换");
            });

            Runner.Add("日志", "写入后能读回且不含换行", delegate
            {
                var store = new InMemoryFileStore();
                var log = new DiagnosticLog(store, SystemClock.Instance);
                log.Log(LogLevel.Info, "test", "第一行\n第二行");

                var tail = log.ReadTailAsync(10).Result;
                Check.Contains(tail, "第一行", "内容可读");
                Check.NotContains(tail, "第一行\n第二行", "换行被压掉");
                Check.True(store.Peek(DiagnosticLog.FilePath) != null, "已落盘");
            });

            Runner.Add("日志", "低于门槛的级别不记录", delegate
            {
                var store = new InMemoryFileStore();
                var log = new DiagnosticLog(store, SystemClock.Instance) { MinLevel = LogLevel.Warning };
                log.Log(LogLevel.Debug, "t", "不该出现");
                Check.True(store.Peek(DiagnosticLog.FilePath) == null, "没有写任何东西");
            });

            Runner.Add("模型目录", "从对话端点推导模型列表端点", delegate
            {
                Check.Equal("https://api.deepseek.com/v1/models",
                    ModelCatalog.DeriveModelsUrl("https://api.deepseek.com/v1/chat/completions"), "OpenAI 形态");
                Check.Equal("https://api.anthropic.com/v1/models",
                    ModelCatalog.DeriveModelsUrl("https://api.anthropic.com/v1/messages"), "Anthropic 形态");
                Check.Equal("https://x.com/v1/models",
                    ModelCatalog.DeriveModelsUrl("https://x.com/v1/"), "去尾部斜杠");
            });
        }

        // ═════════════ 辅助 ═════════════

        private static Workspace MakeWorkspace(out InMemoryFileStore store, out InMemorySecretStore secrets)
        {
            store = new InMemoryFileStore();
            secrets = new InMemorySecretStore();
            return new Workspace(store, secrets, NullLogger.Instance, SystemClock.Instance);
        }

        /// <summary>合成一个最小可解析的 PNG（含一个自定义文本块）。不校验 CRC，读实现也不校验。</summary>
        private static byte[] BuildPng(byte[] textChunk)
        {
            var bytes = new List<byte>();
            bytes.AddRange(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            // IHDR 假数据（解析器不关心内容）
            bytes.AddRange(BuildChunk("IHDR", new byte[13]));
            if (textChunk != null) bytes.AddRange(textChunk);
            bytes.AddRange(BuildChunk("IEND", new byte[0]));

            return bytes.ToArray();
        }

        private static byte[] BuildChunk(string type, byte[] data)
        {
            var chunk = new List<byte>();
            chunk.AddRange(Int32Be(data.Length));
            chunk.AddRange(Encoding.ASCII.GetBytes(type));
            chunk.AddRange(data);
            chunk.AddRange(new byte[4]);      // CRC 占位
            return chunk.ToArray();
        }

        private static byte[] BuildTextChunk(string keyword, string text)
        {
            var data = new List<byte>();
            data.AddRange(Encoding.ASCII.GetBytes(keyword));
            data.Add(0);
            data.AddRange(Encoding.UTF8.GetBytes(text));
            return BuildChunk("tEXt", data.ToArray());
        }

        private static byte[] BuildZtxtChunk(string keyword, string text)
        {
            var data = new List<byte>();
            data.AddRange(Encoding.ASCII.GetBytes(keyword));
            data.Add(0);
            data.Add(0);                       // 压缩方法 0（deflate）

            // zlib 包装：2 字节头 + deflate + 4 字节校验尾
            var raw = Encoding.UTF8.GetBytes(text);
            var compressed = new MemoryStream();
            compressed.WriteByte(0x78);
            compressed.WriteByte(0x9C);
            using (var deflate = new DeflateStream(compressed, CompressionMode.Compress, true))
            {
                deflate.Write(raw, 0, raw.Length);
            }
            compressed.Write(new byte[4], 0, 4);

            data.AddRange(compressed.ToArray());
            return BuildChunk("zTXt", data.ToArray());
        }

        private static byte[] Int32Be(int value)
        {
            return new byte[]
            {
                (byte)((value >> 24) & 0xFF),
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)(value & 0xFF)
            };
        }
    }
}
