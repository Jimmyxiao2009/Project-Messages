// tests/CoreTests/Feature7Tests.cs —— 朗读、翻译、会话用量日志（v1.2）

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tests
{
    public static class Feature7Tests
    {
        public static void Register()
        {
            RegisterSpeechText();
            RegisterTts();
            RegisterTranslation();
            RegisterTechLog();
        }

        // ═════════════ 朗读文本处理 ═════════════
        private static void RegisterSpeechText()
        {
            Runner.Add("朗读", "Markdown 标记被清掉", delegate
            {
                var md = "# 标题\n\n这是**加粗**和*斜体*的文字。\n\n- 列表项一\n- 列表项二\n\n> 引用内容";
                var text = SpeechText.ToSpeechText(md, false);

                Check.False(text.Contains("#"), "没有标题符号");
                Check.False(text.Contains("**"), "没有加粗标记");
                Check.False(text.Contains("- "), "没有列表符号");
                Check.False(text.Contains("> "), "没有引用符号");
                Check.Contains(text, "加粗", "正文保留");
                Check.Contains(text, "列表项一", "列表内容保留");
            });

            Runner.Add("朗读", "代码块默认不读", delegate
            {
                var md = "看这段代码：\n\n```python\nprint('hello')\n```\n\n就这些。";
                var text = SpeechText.ToSpeechText(md, false);

                Check.False(text.Contains("print"), "代码内容不读");
                Check.Contains(text, "就这些", "正文保留");
            });

            Runner.Add("朗读", "需要时保留代码内容", delegate
            {
                var md = "看这段代码：\n\n```python\nprint('hello')\n```\n";
                var text = SpeechText.ToSpeechText(md, true);

                Check.Contains(text, "print", "代码内容保留");
                Check.False(text.Contains("```"), "围栏符号不读");
            });

            Runner.Add("朗读", "链接只读文字", delegate
            {
                var text = SpeechText.ToSpeechText("见 [文档](https://example.com/a/b?c=d) 和 https://raw.example.com/x", false);

                Check.Contains(text, "文档", "链接文字保留");
                Check.False(text.Contains("example.com/a/b"), "URL 目标不读");
                Check.False(text.Contains("https://"), "裸 URL 不读");
            });

            Runner.Add("朗读", "表格整体跳过", delegate
            {
                var md = "下面是对比：\n\n| 项目 | 数值 |\n| --- | --- |\n| 甲 | 1 |\n\n以上就是对比。";
                var text = SpeechText.ToSpeechText(md, false);

                Check.False(text.Contains("|"), "表格符号不读");
                Check.False(text.Contains("---"), "分隔行不读");
                Check.Contains(text, "以上就是对比", "表格外正文保留");
            });

            Runner.Add("朗读", "长文本按句末标点切段", delegate
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 20; i++) sb.Append("这是第").Append(i).Append("句话，内容比较长一些。");

                var chunks = SpeechText.Split(sb.ToString(), 60);

                Check.True(chunks.Count > 1, "被切成多段");
                foreach (var chunk in chunks)
                {
                    Check.True(chunk.Text.Length <= 60, "每段不超上限：" + chunk.Text.Length);
                }
                foreach (var chunk in chunks)
                {
                    Check.False(chunk.Text.EndsWith("第", StringComparison.Ordinal), "没有把词切断");
                }
            });

            Runner.Add("朗读", "没有标点时硬切但不切坏字符", delegate
            {
                var text = new string('啊', 300);
                var chunks = SpeechText.Split(text, 50);

                Check.True(chunks.Count >= 6, "被切开");
                int total = 0;
                foreach (var chunk in chunks) total += chunk.Text.Length;
                Check.EqualInt(300, total, "一个字都没丢");
            });

            Runner.Add("朗读", "emoji 不会被切出半个字符", delegate
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 40; i++) sb.Append("😀");
                var text = sb.ToString();

                var chunks = SpeechText.Split(text, 21);
                var rejoined = "";
                foreach (var chunk in chunks) rejoined += chunk.Text;

                Check.Equal(text, rejoined, "重组后与原文一致（没有半个代理对）");
                foreach (var chunk in chunks)
                {
                    Check.False(char.IsHighSurrogate(chunk.Text[chunk.Text.Length - 1]), "段尾不是孤立高代理");
                }
            });

            Runner.Add("朗读", "空文本不产生段落", delegate
            {
                Check.EqualInt(0, SpeechText.Split(null, 50).Count, "null");
                Check.EqualInt(0, SpeechText.Split("   \n\n  ", 50).Count, "空白");
            });
        }

        // ═════════════ 朗读服务 ═════════════
        private static void RegisterTts()
        {
            Runner.Add("朗读服务", "不可用时静默返回", delegate
            {
                var service = new TtsService(NullSpeechSynthesizer.Instance, NullLogger.Instance);
                Check.False(service.IsAvailable, "标记不可用");

                service.SpeakAsync("一段文字", new TtsSettings(), CancellationToken.None).Wait();
            });

            Runner.Add("朗读服务", "按段依次朗读", delegate
            {
                var synth = new FakeSynth();
                var service = new TtsService(synth, NullLogger.Instance);

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 10; i++) sb.Append("第").Append(i).Append("句测试文本。");

                service.SpeakAsync(sb.ToString(), new TtsSettings { MaxChunkChars = 30 }, CancellationToken.None).Wait();

                Check.True(synth.Spoken.Count > 1, "分了多段朗读");
                Check.True(synth.Stopped, "开始前停过引擎（清掉上一次残留）");
            });

            Runner.Add("朗读服务", "新朗读会打断旧的", delegate
            {
                var synth = new SlowSynth();
                var service = new TtsService(synth, NullLogger.Instance);

                var first = service.SpeakAsync("第一段很长的文字。第二段也很长。第三段还是很长。第四段。",
                    new TtsSettings { MaxChunkChars = 12 }, CancellationToken.None);

                // 让第一次跑起来
                Thread.Sleep(30);

                var second = service.SpeakAsync("新的朗读内容。",
                    new TtsSettings { MaxChunkChars = 12 }, CancellationToken.None);

                Task.WaitAll(first, second);

                // 第一次不该把它所有的段都读完
                Check.True(synth.TotalSpoken < 6, "旧朗读被中途打断，没有读完所有段：" + synth.TotalSpoken);
            });

            Runner.Add("朗读服务", "停止后不再继续读", delegate
            {
                var synth = new SlowSynth();
                var service = new TtsService(synth, NullLogger.Instance);

                var task = service.SpeakAsync("第一段。第二段。第三段。第四段。第五段。",
                    new TtsSettings { MaxChunkChars = 6 }, CancellationToken.None);

                Thread.Sleep(40);
                service.StopAsync().Wait();
                task.Wait();

                int afterStop = synth.TotalSpoken;
                Thread.Sleep(120);
                Check.EqualInt(afterStop, synth.TotalSpoken, "停止后没有新增朗读");
            });

            Runner.Add("朗读服务", "总量上限被遵守", delegate
            {
                var synth = new FakeSynth();
                var service = new TtsService(synth, NullLogger.Instance);

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 100; i++) sb.Append("很长的一句话内容在这里。");

                service.SpeakAsync(sb.ToString(), new TtsSettings
                {
                    MaxChunkChars = 20,
                    MaxTotalChars = 200
                }, CancellationToken.None).Wait();

                int total = 0;
                foreach (var s in synth.Spoken) total += s.Length;
                Check.True(total <= 260, "总量被夹住：" + total);
            });

            Runner.Add("朗读服务", "进度回调被调用", delegate
            {
                var synth = new FakeSynth();
                var service = new TtsService(synth, NullLogger.Instance);

                int lastIndex = 0, lastTotal = 0;
                service.Progress += delegate (int index, int total) { lastIndex = index; lastTotal = total; };

                service.SpeakAsync("第一段。第二段。第三段。", new TtsSettings { MaxChunkChars = 6 }, CancellationToken.None).Wait();

                Check.True(lastTotal >= 1, "总段数被报告");
                Check.EqualInt(lastTotal, lastIndex, "最后停在最后一段");
            });

            Runner.Add("朗读服务", "引擎抛异常不会打断整个流程", delegate
            {
                var synth = new ThrowingSynth();
                var service = new TtsService(synth, NullLogger.Instance);

                service.SpeakAsync("一段话。另一段话。", new TtsSettings { MaxChunkChars = 5 }, CancellationToken.None).Wait();
                // 不抛出去就算通过
            });
        }

        // ═════════════ 翻译 ═════════════
        private static void RegisterTranslation()
        {
            Runner.Add("翻译", "语言名可读", delegate
            {
                Check.Equal("简体中文", TranslationService.LanguageName("zh-CN"), "中文");
                Check.Equal("英语", TranslationService.LanguageName("en-US"), "英语");
                Check.Equal("日语", TranslationService.LanguageName("ja"), "日语");
                Check.Equal("未知语", TranslationService.LanguageName("未知语"), "不认识的标签原样返回");
            });

            Runner.Add("翻译", "已有译文直接命中缓存", delegate
            {
                var service = new TranslationService(new StubHttp(), NullLogger.Instance);

                var message = new Message { Role = MessageRole.Assistant, Content = "原文" };
                message.SetTranslation("en", "cached translation");

                var result = service.TranslateMessageAsync(
                    message, "en", new ApiProfile { Id = "a" }, "key", CancellationToken.None).Result;

                Check.True(result.Ok, "成功");
                Check.True(result.FromCache, "来自缓存");
                Check.Equal("cached translation", result.Text, "内容正确");
            });

            Runner.Add("翻译", "没有模型时给出可读错误", delegate
            {
                var service = new TranslationService(new StubHttp(), NullLogger.Instance);
                var message = new Message { Content = "原文" };

                var result = service.TranslateMessageAsync(
                    message, "en", null, "key", CancellationToken.None).Result;

                Check.False(result.Ok, "失败");
                Check.Equal("no_api", result.ErrorCode, "错误码");
            });

            Runner.Add("翻译", "没有密钥时给出可读错误", delegate
            {
                var service = new TranslationService(new StubHttp(), NullLogger.Instance);
                var message = new Message { Content = "原文" };

                var result = service.TranslateMessageAsync(
                    message, "en", new ApiProfile { Id = "a" }, "", CancellationToken.None).Result;

                Check.False(result.Ok, "失败");
                Check.Equal("no_key", result.ErrorCode, "错误码");
            });

            Runner.Add("翻译", "空内容不发起请求", delegate
            {
                var service = new TranslationService(new StubHttp(), NullLogger.Instance);
                var message = new Message { Content = "   " };

                var result = service.TranslateMessageAsync(
                    message, "en", new ApiProfile { Id = "a" }, "key", CancellationToken.None).Result;

                Check.False(result.Ok, "失败");
                Check.Equal("empty_content", result.ErrorCode, "错误码");
            });

            Runner.Add("翻译", "译文缓存在消息上，原文不被覆盖", delegate
            {
                var message = new Message { Content = "Hello world" };
                message.SetTranslation("zh-CN", "你好世界");

                Check.Equal("Hello world", message.Content, "原文未变");
                Check.Equal("你好世界", message.GetTranslation("zh-CN"), "译文可读回");
                Check.True(message.GetTranslation("ja") == null, "未翻译的语言返回空");
            });

            Runner.Add("翻译", "译文随消息一起克隆", delegate
            {
                var message = new Message { Content = "Hello" };
                message.SetTranslation("zh-CN", "你好");

                var clone = message.Clone();

                Check.Equal("你好", clone.GetTranslation("zh-CN"), "克隆带上译文");
                Check.Equal("Hello", clone.Content, "克隆带上原文");
            });
        }

        // ═════════════ 会话用量日志 ═════════════
        private static void RegisterTechLog()
        {
            Runner.Add("用量日志", "追加与读回", delegate
            {
                var store = new InMemoryFileStore();
                var log = new TokenTechLog(store, NullLogger.Instance);

                log.AppendAsync("test", "conv1", new TechLogEntry
                {
                    Turn = 1, Model = "gpt-4o", InputTokens = 100, OutputTokens = 50, DurationMs = 1200
                }, CancellationToken.None).Wait();

                log.AppendAsync("test", "conv1", new TechLogEntry
                {
                    Turn = 2, Model = "gpt-4o", InputTokens = 200, OutputTokens = 80, DurationMs = 900
                }, CancellationToken.None).Wait();

                var entries = log.ReadAsync("test", "conv1", 0, CancellationToken.None).Result;

                Check.EqualInt(2, entries.Count, "两条都在");
                Check.EqualInt(1, entries[0].Turn, "顺序保持");
                Check.EqualInt(2, entries[1].Turn, "顺序保持");
            });

            Runner.Add("用量日志", "汇总算得对", delegate
            {
                var entries = new List<TechLogEntry>
                {
                    new TechLogEntry { InputTokens = 100, OutputTokens = 50, DurationMs = 1000, ModelCalls = 2, ToolCalls = 1, CachedTokens = 60 },
                    new TechLogEntry { InputTokens = 200, OutputTokens = 80, DurationMs = 2000, ModelCalls = 1, Estimated = true }
                };

                var summary = TokenTechLog.Summarize(entries);

                Check.EqualInt(2, summary.Turns, "轮次");
                Check.EqualInt(300, summary.InputTokens, "输入合计");
                Check.EqualInt(130, summary.OutputTokens, "输出合计");
                Check.EqualInt(430, summary.TotalTokens, "总计");
                Check.EqualInt(215, summary.AverageTokensPerTurn, "平均每轮");
                Check.EqualInt(3, summary.ModelCalls, "模型调用次数");
                Check.EqualInt(1, summary.ToolCalls, "工具调用次数");
                Check.EqualInt(1, summary.EstimatedTurns, "估算轮次数");
                Check.True(summary.CacheHitRate > 0.19 && summary.CacheHitRate < 0.21, "缓存命中率约 20%");
            });

            Runner.Add("用量日志", "坏行被跳过不影响其它行", delegate
            {
                var store = new InMemoryFileStore();
                var log = new TokenTechLog(store, NullLogger.Instance);

                var path = TokenTechLog.PathFor("test", "conv1");
                var good = "{\"turn\":1,\"input\":10,\"output\":5}";
                store.WriteTextAsync(path, good + "\n" + "{这不是合法 JSON}\n" + good + "\n").Wait();

                var entries = log.ReadAsync("test", "conv1", 0, CancellationToken.None).Result;

                Check.EqualInt(2, entries.Count, "坏行被跳过，好行都在");
            });

            Runner.Add("用量日志", "只取最近 N 条", delegate
            {
                var store = new InMemoryFileStore();
                var log = new TokenTechLog(store, NullLogger.Instance);

                for (int i = 1; i <= 10; i++)
                {
                    log.AppendAsync("test", "conv1", new TechLogEntry { Turn = i }, CancellationToken.None).Wait();
                }

                var entries = log.ReadAsync("test", "conv1", 3, CancellationToken.None).Result;

                Check.EqualInt(3, entries.Count, "只取三条");
                Check.EqualInt(8, entries[0].Turn, "是最近的三条");
                Check.EqualInt(10, entries[2].Turn, "最后一条最新");
            });

            Runner.Add("用量日志", "不同会话互不干扰", delegate
            {
                var store = new InMemoryFileStore();
                var log = new TokenTechLog(store, NullLogger.Instance);

                log.AppendAsync("test", "convA", new TechLogEntry { Turn = 1 }, CancellationToken.None).Wait();
                log.AppendAsync("test", "convB", new TechLogEntry { Turn = 1 }, CancellationToken.None).Wait();
                log.AppendAsync("test", "convB", new TechLogEntry { Turn = 2 }, CancellationToken.None).Wait();

                Check.EqualInt(1, log.ReadAsync("test", "convA", 0, CancellationToken.None).Result.Count, "A 只有一条");
                Check.EqualInt(2, log.ReadAsync("test", "convB", 0, CancellationToken.None).Result.Count, "B 有两条");
            });

            Runner.Add("用量日志", "清除后读回为空", delegate
            {
                var store = new InMemoryFileStore();
                var log = new TokenTechLog(store, NullLogger.Instance);

                log.AppendAsync("test", "conv1", new TechLogEntry { Turn = 1 }, CancellationToken.None).Wait();
                log.ClearAsync("test", "conv1", CancellationToken.None).Wait();

                Check.EqualInt(0, log.ReadAsync("test", "conv1", 0, CancellationToken.None).Result.Count, "已清空");
            });

            Runner.Add("用量日志", "渲染文本包含关键数字", delegate
            {
                var entries = new List<TechLogEntry>
                {
                    new TechLogEntry { Turn = 1, Model = "gpt-4o", InputTokens = 1200, OutputTokens = 300, DurationMs = 3400, Estimated = true },
                    new TechLogEntry { Turn = 2, Model = "gpt-4o", InputTokens = 1500, OutputTokens = 400, DurationMs = 2100, Error = "http_429" }
                };

                var text = TokenTechLog.Format(TokenTechLog.Summarize(entries), entries);

                Check.Contains(text, "2700", "输入合计");
                Check.Contains(text, "700", "输出合计");
                Check.Contains(text, "gpt-4o", "模型名");
                Check.Contains(text, "估算", "标注了估算");
                Check.Contains(text, "http_429", "标出了失败轮次");
            });

            Runner.Add("用量日志", "不含提示词与密钥", delegate
            {
                var store = new InMemoryFileStore();
                var log = new TokenTechLog(store, NullLogger.Instance);

                log.AppendAsync("test", "conv1", new TechLogEntry
                {
                    Turn = 1, Model = "gpt-4o", Provider = "openai", Host = "api.openai.com",
                    InputTokens = 10, OutputTokens = 5
                }, CancellationToken.None).Wait();

                var raw = store.Peek(TokenTechLog.PathFor("test", "conv1"));

                Check.False(raw.Contains("sk-"), "不含密钥");
                Check.Contains(raw, "api.openai.com", "只有主机名");
            });
        }

        // ═════════════ 假实现 ═════════════
        private sealed class FakeSynth : ISpeechSynthesizer
        {
            public List<string> Spoken = new List<string>();
            public bool Stopped;

            public bool IsAvailable { get { return true; } }

            public Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct)
            {
                IReadOnlyList<string> voices = new List<string> { "默认" };
                return Task.FromResult(voices);
            }

            public Task SpeakAsync(SpeechRequest request, CancellationToken ct)
            {
                Spoken.Add(request.Text);
                return Task.CompletedTask;
            }

            public Task StopAsync() { Stopped = true; return Task.CompletedTask; }
        }

        /// <summary>每段朗读都慢一点，用来观察打断行为。</summary>
        private sealed class SlowSynth : ISpeechSynthesizer
        {
            public int TotalSpoken;

            public bool IsAvailable { get { return true; } }

            public Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct)
            {
                IReadOnlyList<string> voices = new List<string>();
                return Task.FromResult(voices);
            }

            public Task SpeakAsync(SpeechRequest request, CancellationToken ct)
            {
                TotalSpoken++;
                return Task.Delay(50, ct);
            }

            public Task StopAsync() { return Task.CompletedTask; }
        }

        private sealed class ThrowingSynth : ISpeechSynthesizer
        {
            public bool IsAvailable { get { return true; } }

            public Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct)
            {
                IReadOnlyList<string> voices = new List<string>();
                return Task.FromResult(voices);
            }

            public Task SpeakAsync(SpeechRequest request, CancellationToken ct)
            {
                throw new InvalidOperationException("引擎炸了");
            }

            public Task StopAsync() { return Task.CompletedTask; }
        }

        /// <summary>不会真的发请求的传输层。</summary>
        private sealed class StubHttp : IHttpTransport
        {
            public Task<HttpResponseSpec> SendAsync(HttpRequestSpec request, CancellationToken ct)
            {
                return Task.FromResult(new HttpResponseSpec { StatusCode = 200, Body = "{}" });
            }

            public Task StreamLinesAsync(HttpRequestSpec request, Func<string, bool> onLine, CancellationToken ct)
            {
                return Task.CompletedTask;
            }
        }
    }
}
