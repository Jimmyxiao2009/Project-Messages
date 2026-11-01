// tools/SeedData/Program.cs —— 往工作区塞演示数据
//
// 用法：
//   dotnet run --project tools\SeedData -- "C:\...\Packages\<包族名>\LocalState\workspace"
//
// 为什么要这个工具：验证界面在"有数据"时的样子，每次手动建档案、建会话、造分支太慢，
// 而且分支、群聊、快捷消息这些功能光靠点很难造出可复现的样本。
//
// 它用的是 Core 里同一套 Workspace / JsonStore，所以写出来的文件格式
// 与应用自己写的**完全一致** —— 不需要手写 JSON，也就不会因为格式猜错而白折腾。

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tools
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length == 0)
            {
                Console.WriteLine("用法：SeedData <工作区目录> [--fresh]");
                Console.WriteLine("  例如：SeedData \"%LOCALAPPDATA%\\Packages\\com.yanshuai.messages_amaw0954g0pkg\\LocalState\\workspace\"");
                return 2;
            }

            var root = args[0];
            var fresh = Array.IndexOf(args, "--fresh") >= 0;
            var verify = Array.IndexOf(args, "--verify") >= 0;

            if (fresh && System.IO.Directory.Exists(root))
            {
                Console.WriteLine("清空：" + root);
                System.IO.Directory.Delete(root, true);
            }

            System.IO.Directory.CreateDirectory(root);

            try
            {
                if (verify)
                {
                    VerifyAsync(root).GetAwaiter().GetResult();
                    return 0;
                }

                RunAsync(root).GetAwaiter().GetResult();
                Console.WriteLine();
                Console.WriteLine("完成。工作区：" + root);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("失败：" + ex.GetType().Name + " " + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// 回读校验：把工作区里的 JSON 逐个解析一遍，失败就把真实异常打出来。
        /// JsonStore 会吞掉异常只记一行日志 —— 排障时正是需要看到那条异常。
        /// </summary>
        private static async Task VerifyAsync(string root)
        {
            var store = new FileSystemStore(root);
            var files = await store.ListFilesAsync("", true);

            int ok = 0, bad = 0;

            foreach (var file in files)
            {
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var raw = await store.ReadTextAsync(file);
                    JsonStore.TryDeserialize<object>(raw, NullLogger.Instance, "verify");
                    Console.WriteLine("OK   " + file);
                    ok++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("BAD  " + file + "  →  " + ex.GetType().Name + ": " + ex.Message);
                    bad++;
                }
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + ok + "，失败 " + bad);
        }

        private static async Task RunAsync(string root)
        {
            var store = new FileSystemStore(root);
            var secrets = new MemorySecrets();
            var workspace = new Workspace(store, secrets, NullLogger.Instance, SystemClock.Instance);
            await workspace.InitializeAsync();

            // ── 模型档案（不写真密钥，只为了让界面有东西显示） ──
            if (workspace.ApiProfiles.Items.Count == 0)
            {
                var api = new ApiProfile
                {
                    Name = "本地中转",
                    Provider = "openai",
                    Url = "http://localhost:8080/v1/chat/completions",
                    Model = "gpt-4o"
                };
                workspace.ApiProfiles.Items.Add(api);
                workspace.SetApiKey(api, "sk-demo-not-a-real-key");
                await workspace.SaveApiProfilesAsync();
                Console.WriteLine("已建模型档案：" + api.Name);
            }

            // ── 快捷消息 ──
            if (workspace.Library.QuickMessages == null || workspace.Library.QuickMessages.Count == 0)
            {
                workspace.Library.QuickMessages = QuickMessages.CreateDefaults();
                await workspace.SaveLibraryAsync();
                Console.WriteLine("已写入 " + workspace.Library.QuickMessages.Count + " 条快捷消息");
            }

            // ── 档案 ──
            var existing = workspace.Profiles.Count > 0 ? workspace.Profiles[0] : null;
            if (existing == null)
            {
                existing = await workspace.CreateProfileAsync("小满", "xiaoman");
                existing.Persona.SystemPrompt = "你是小满，说话简短、直接，喜欢用具体例子说明问题。";
                existing.Persona.FirstMessage = "在。有什么事？";
                existing.Tools.ScriptEnabled = true;      // 让脚本开关在界面上是"开"的状态
                await workspace.SaveProfileAsync(existing);
                Console.WriteLine("已建档案：" + existing.Name);
            }

            // 群聊成员要引用真实档案，所以再补两个
            var laozhou = await EnsureProfileAsync(workspace, "老周", "laozhou",
                "你是老周，关注长期维护成本，语气温和，喜欢补充别人漏掉的前提。");
            var amay = await EnsureProfileAsync(workspace, "阿May", "amay",
                "你是阿May，从用户视角提问，喜欢举反例。");

            // ── 一条普通会话（含过程轨道、分支、译文） ──
            await SeedMainConversationAsync(workspace, existing);

            // ── 一条群聊会话 ──
            await SeedGroupConversationAsync(workspace, existing, laozhou, amay);

            // ── 一条很长的会话，用来验证列表滚动与预览截断 ──
            await SeedLongConversationAsync(workspace, existing);
        }

        private static async Task<Profile> EnsureProfileAsync(Workspace workspace, string name, string slug, string prompt)
        {
            foreach (var profile in workspace.Profiles)
            {
                if (string.Equals(profile.Slug, slug, StringComparison.Ordinal)) return profile;
            }

            var created = await workspace.CreateProfileAsync(name, slug);
            created.Persona.SystemPrompt = prompt;
            await workspace.SaveProfileAsync(created);
            Console.WriteLine("已建档案：" + created.Name);
            return created;
        }

        private static async Task SeedMainConversationAsync(Workspace workspace, Profile profile)
        {
            var conversation = workspace.CreateConversation(profile, "周报怎么写");
            conversation.Messages.Add(new Message
            {
                Role = MessageRole.User,
                Content = "帮我看看这段周报怎么改：本周完成了登录模块的重构，下周准备做权限。"
            });

            // 一条带工具过程轨道的助手消息
            var withTools = new Message
            {
                Role = MessageRole.Assistant,
                Content = "改了两处：\n\n1. 「重构」太笼统，换成具体动作，比如「把登录模块拆成三个可单测的类」。\n2. 「准备做权限」没有验收标准，加一句「下周内能跑通管理员与普通用户两条路径」。\n\n改完是这样：\n\n> 本周把登录模块拆成三个可单测的类，覆盖率从 12% 提到 47%。下周打通管理员与普通用户两条权限路径，以两条路径各跑通一次端到端用例为准。",
                IsFinal = true,
                ModelName = "gpt-4o",
                TokensIn = 412,
                TokensOut = 186
            };
            withTools.ToolCalls.Add(new ToolCallRecord
            {
                Id = "call_1",
                Name = "read_file",
                ArgumentsJson = "{\"path\":\"files/周报.md\"}",
                ResultPreview = "读到 1.2KB 文本"
            });
            conversation.Messages.Add(withTools);

            // 给这条消息加一个分支候选（重生成出来的另一版）
            var alternative = withTools.Clone();
            alternative.Id = Guid.NewGuid().ToString("N");
            alternative.Content = "主要问题在「完成了」这个词上 —— 它没说你做了什么，只说了结果。\n\n建议改成动作 + 度量：\n\n> 本周重构登录模块，拆出 3 个可单测的类，单测覆盖率 12% → 47%。\n> 下周实现角色权限，验收标准是管理员与普通用户两条路径各跑通一次端到端用例。";
            alternative.TokensOut = 214;
            conversation.AddVariant(conversation.Messages.Count - 1, alternative);

            // 一条带译文的消息
            var bilingual = new Message
            {
                Role = MessageRole.Assistant,
                Content = "Yes — that reads much better. The only remaining nit is \"end-to-end case\"; say \"end-to-end test\" so it matches your team's vocabulary.",
                IsFinal = true,
                ModelName = "gpt-4o"
            };
            bilingual.SetTranslation("zh-CN", "可以 —— 这样读起来好多了。唯一还剩的小问题是「端到端用例」这个词；改成「端到端测试」，跟你团队的说法保持一致。");
            conversation.Messages.Add(bilingual);

            await workspace.SaveConversationAsync(profile.Slug, conversation);
            Console.WriteLine("已建会话：" + conversation.Title + "（含分支与译文）");
        }

        private static async Task SeedGroupConversationAsync(Workspace workspace, Profile main, Profile laozhou, Profile amay)
        {
            var conversation = workspace.CreateConversation(main, "三个人的方案评审");
            conversation.GroupReplyMode = GroupReplyModes.RoundRobin;

            conversation.Members.Add(new GroupMember { ProfileId = main.Id, Name = "小满", Order = 0 });
            conversation.Members.Add(new GroupMember { ProfileId = laozhou.Id, Name = "老周", Order = 1 });
            conversation.Members.Add(new GroupMember { ProfileId = amay.Id, Name = "阿May", Order = 2 });

            conversation.Messages.Add(new Message
            {
                Role = MessageRole.User,
                Content = "我们准备把配置全部改成 JSON 文件，大家怎么看？"
            });
            conversation.Messages.Add(new Message
            {
                Role = MessageRole.Assistant,
                Content = "风险在于没有 schema 校验时，一个手抖的逗号就会让应用起不来。建议至少加一层加载时校验，失败就退回上一份可用配置。",
                IsFinal = true,
                ModelName = "gpt-4o"
            });
            conversation.Messages.Add(new Message
            {
                Role = MessageRole.Assistant,
                Content = "同意小满。补一句：配置的默认值要写在代码里，而不是写在文件里 —— 否则以后加字段时，老配置文件会缺字段。",
                IsFinal = true,
                ModelName = "claude-sonnet-4"
            });
            conversation.Messages.Add(new Message
            {
                Role = MessageRole.Assistant,
                Content = "那用户改坏配置之后，界面上会看到什么？如果只是白屏，他们不会知道是自己的文件写错了。",
                IsFinal = true,
                ModelName = "gemini-2.5-pro"
            });

            await workspace.SaveConversationAsync(main.Slug, conversation);
            Console.WriteLine("已建群聊会话：" + conversation.Title + "（" + conversation.Members.Count + " 人）");
        }

        private static async Task SeedLongConversationAsync(Workspace workspace, Profile profile)
        {
            var conversation = workspace.CreateConversation(profile, "一个很长的会话（用来验证滚动）");

            for (int i = 1; i <= 12; i++)
            {
                conversation.Messages.Add(new Message
                {
                    Role = MessageRole.User,
                    Content = "第 " + i + " 个问题：这一步该怎么做？"
                });
                conversation.Messages.Add(new Message
                {
                    Role = MessageRole.Assistant,
                    Content = "第 " + i + " 个回答：先确认输入，再确认输出，最后看中间有没有可以省掉的一步。",
                    IsFinal = true,
                    ModelName = "gpt-4o",
                    TokensIn = 100 + i * 7,
                    TokensOut = 40 + i * 3
                });
            }

            conversation.TotalInputTokens = 1900;
            conversation.TotalOutputTokens = 700;

            await workspace.SaveConversationAsync(profile.Slug, conversation);
            Console.WriteLine("已建长会话：" + conversation.Title + "（" + conversation.Messages.Count + " 条消息）");
        }

        /// <summary>演示用的密钥存储：不落盘，只在进程内存里。</summary>
        private sealed class MemorySecrets : ISecretStore
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

            public string Get(string key)
            {
                string value;
                return _values.TryGetValue(key ?? "", out value) ? value : null;
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
        }
    }
}
