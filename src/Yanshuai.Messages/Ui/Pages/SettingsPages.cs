using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class SettingsPage : DetailPage
    {
        protected override async Task BuildAsync()
        {
            string section = Context.Section ?? "general";
            var settings = W.Settings;
            if (section == "typography") {
                Title("文字与可读性", "外观");
                var size = Range("正文大小", settings.BodyFontSize, 12, 24);
                var height = Range("行高", settings.LineHeight, 18, 40);
                Text("预览：中文与 English text。长路径会在屏幕内换行。");
                Save(async delegate { settings.BodyFontSize = (int)size.Value; settings.LineHeight = (int)height.Value; await W.SaveSettingsAsync(); });
            } else if (section == "speech") {
                await BuildSpeechAsync(settings);
            } else if (section == "translation") {
                Title("消息翻译", "会话");
                var language = Choice("目标语言", new[] { "zh-CN", "en", "ja", "ko", "fr", "de" }, settings.TranslationLanguage);
                Text("译文叠加显示，原文保留；同语言翻译使用消息缓存。", true);
                Save(async delegate { settings.TranslationLanguage = (string)language.SelectedItem; await W.SaveSettingsAsync(); });
            } else if (section == "attachment") {
                Title("附件处理", "文件");
                var pages = Range("PDF 最多渲染页数", settings.AttachmentMaxPages, 1, 20);
                Text("Office 文档提取文字；PDF 扫描件转图片。图片按需装载，不写进消息 JSON。", true);
                Save(async delegate { settings.AttachmentMaxPages = (int)pages.Value; await W.SaveSettingsAsync(); });
            } else if (section == "workspace") {
                Title("工作区位置", "通用"); Text(S.Files.GetAbsolutePath(""));
                Text("当前路径：" + S.Files.GetNativePath(""), true);
                Row("切换工作区", "选择文档目录或 SD 卡，复制现有数据后切换", async delegate { RequireIdle(); var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary }; picker.FileTypeFilter.Add("*"); var folder = await picker.PickSingleFolderAsync(); if (folder == null) return; if (!await Confirm("切换工作区？", "现有数据将复制到 " + folder.Path + "，原数据保留。")) return; var replacement = await S.RelocateAsync(folder, Lifetime.Token); await Context.SwitchServices(replacement); });
                Link<FolderAccessPage>("授权目录", "从文件夹导入文件");
                Command("重建会话索引", Symbol.Refresh, async delegate {
                    RequireIdle(); int count = 0;
                    foreach (var p in W.Profiles) count += await S.Operations.RebuildIndexAsync(p);
                    Status.Text = "已重建 " + count + " 条会话索引，正文未改动。";
                });
            } else {
                Title("通用设置", "我的");
                var enter = Choice("回车键", new[] { "send", "newline" }, settings.EnterBehavior);
                var startup = Choice("启动时", new[] { "last", "new", "list" }, settings.StartupBehavior);
                var sound = Toggle("回复提示音", settings.ReplySound);
                var global = Toggle("全局记忆", settings.GlobalMemoryEnabled);
                var pageSize = Range("每批历史消息数", settings.HistoryPageSize, 10, 100);
                Save(async delegate { settings.EnterBehavior = (string)enter.SelectedItem; settings.StartupBehavior = (string)startup.SelectedItem;
                    settings.ReplySound = sound.IsOn; settings.GlobalMemoryEnabled = global.IsOn; settings.HistoryPageSize = (int)pageSize.Value; await W.SaveSettingsAsync(); });
                Link<SettingsPage>("工作区位置", "路径与索引维护", "workspace");
                Link<FolderAccessPage>("授权目录", "系统文件夹授权");
            }
        }

        private async Task BuildSpeechAsync(GlobalSettings settings)
        {
            Title("朗读", "我的");

            Section("用什么读");
            var useSystem = Radio("speech-engine", "系统语音", "这台设备自带，离线可用，不花钱。", settings.SpeechEngine != SpeechEngines.Model);
            var useModel = Radio("speech-engine", "朗读模型", "声音更自然。需要联网，按字数计费；失败时自动改用系统语音。", settings.SpeechEngine == SpeechEngines.Model);

            // 系统语音
            var systemSection = SectionLabel("系统语音");
            var voices = await S.Speech.ListVoicesAsync(Lifetime.Token);
            var voiceLabels = new[] { "自动（优先中文）" }.Concat(voices).ToArray();
            var voiceValues = new[] { "" }.Concat(voices).ToArray();
            var systemVoice = Choice("音色", voiceLabels, voiceLabels[Math.Max(0, Array.IndexOf(voiceValues, settings.SpeechVoice))]);
            if (voices.Count == 0) systemVoice.IsEnabled = false;

            // 朗读模型：只列 OpenAI 兼容的 Provider
            var modelSection = SectionLabel("朗读模型");
            var providers = W.ApiProfiles.Items.Where(x => CloudSpeech.Unsupported(new ApiProfile { Provider = x.Provider, Model = "x" }).Length == 0).ToList();
            var current = W.ApiProfiles.Find(settings.SpeechModel.ApiProfileId);
            if (current == null && providers.Count > 0) current = providers[0];
            var provider = Choice("Provider", providers.Select(x => x.Name).ToArray(), current != null ? current.Name : "");
            var noProvider = Text("还没有 OpenAI 兼容的 Provider。先在「Provider」里添加一个（OpenAI、硅基流动等）。", true);
            var model = Field("模型", settings.SpeechModel.IsSet ? settings.SpeechModel.Model : GuessSpeechModel(current));
            model.PlaceholderText = "tts-1";
            var modelHelp = Text("OpenAI：tts-1、gpt-4o-mini-tts；硅基流动：FunAudioLLM/CosyVoice2-0.5B", true);
            var voice = Field("音色", settings.SpeechModelVoice);
            voice.PlaceholderText = "alloy";
            var voiceHelp = Text("OpenAI：alloy、nova、shimmer、echo、onyx、fable；硅基流动：FunAudioLLM/CosyVoice2-0.5B:anna", true);
            provider.SelectionChanged += delegate {
                var picked = provider.SelectedIndex >= 0 ? providers[provider.SelectedIndex] : null;
                if (string.IsNullOrWhiteSpace(model.Text)) model.Text = GuessSpeechModel(picked);
            };

            Section("语速与音量");
            var rate = Range("语速", settings.SpeechRate, 0.5, 2, 0.1);
            var volume = Range("音量", settings.SpeechVolume, 0, 1, 0.1);

            var systemParts = new FrameworkElement[] { systemSection, systemVoice };
            var modelParts = new FrameworkElement[] { modelSection, provider, model, modelHelp, voice, voiceHelp };
            Action sync = delegate {
                bool cloud = useModel.IsChecked == true;
                foreach (var part in systemParts) part.Visibility = cloud ? Visibility.Collapsed : Visibility.Visible;
                foreach (var part in modelParts) part.Visibility = cloud && providers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                noProvider.Visibility = cloud && providers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            };
            useSystem.Checked += delegate { sync(); };
            useModel.Checked += delegate { sync(); };
            sync();

            Func<GlobalSettings> draft = delegate {
                var d = JsonBridge.Clone(settings);
                d.SpeechEngine = useModel.IsChecked == true ? SpeechEngines.Model : SpeechEngines.System;
                d.SpeechVoice = systemVoice.SelectedIndex >= 0 ? voiceValues[systemVoice.SelectedIndex] : "";
                d.SpeechRate = rate.Value; d.SpeechVolume = volume.Value;
                d.SpeechModelVoice = voice.Text.Trim();
                if (d.SpeechEngine == SpeechEngines.Model)
                {
                    if (providers.Count == 0) throw new InvalidOperationException("先添加一个 OpenAI 兼容的 Provider。");
                    if (string.IsNullOrWhiteSpace(model.Text)) throw new ArgumentException("请填写朗读模型。");
                    d.SpeechModel = new ModelRef { ApiProfileId = providers[Math.Max(0, provider.SelectedIndex)].Id, Model = model.Text.Trim() };
                }
                return d;
            };

            Save(async delegate {
                var d = draft();
                settings.SpeechEngine = d.SpeechEngine; settings.SpeechVoice = d.SpeechVoice;
                settings.SpeechRate = d.SpeechRate; settings.SpeechVolume = d.SpeechVolume;
                settings.SpeechModelVoice = d.SpeechModelVoice; settings.SpeechModel = d.SpeechModel;
                await W.SaveSettingsAsync();
            });
            Command("试听", Symbol.Play, async delegate {
                var d = draft();
                var preview = TtsSettings.From(d, W.ApiProfiles, W.GetApiKey);
                Action<string> fallback = delegate (string reason) { RunOnUi(delegate { Status.Text = "朗读模型没成功，已改用系统语音：" + reason; }); };
                S.Tts.Fallback += fallback;
                try
                {
                    Status.Text = "";
                    await S.Tts.SpeakAsync("你好，这是言枢的朗读试听。", preview, Lifetime.Token);
                }
                finally { S.Tts.Fallback -= fallback; }
            });
        }

        private static string GuessSpeechModel(ApiProfile api)
        {
            if (api != null && api.CachedModels != null)
            {
                var tts = api.CachedModels.FirstOrDefault(x => x.IndexOf("tts", StringComparison.OrdinalIgnoreCase) >= 0
                    || x.IndexOf("CosyVoice", StringComparison.OrdinalIgnoreCase) >= 0);
                if (tts != null) return tts;
            }
            return "tts-1";
        }
    }

    public sealed class AppearancePage : DetailPage
    {
        protected override Task BuildAsync()
        {
            if (Context.Section == "theme" || Context.Section == null) {
                Title("主题与强调色", "我的");
                var theme = Choice("主题", new[] { "dark", "light", "system" }, W.Settings.Theme);
                var follow = Toggle("跟随系统强调色", W.Settings.FollowSystemAccent);
                var color = Field("强调色（#RRGGBB）", W.Settings.CustomAccent); color.IsEnabled = !follow.IsOn;
                follow.Toggled += delegate { color.IsEnabled = !follow.IsOn; };
                Save(async delegate {
                    if (!follow.IsOn && ThemeHelper.ParseHex(color.Text).A == 0) throw new ArgumentException("请输入有效颜色。");
                    W.Settings.Theme = (string)theme.SelectedItem; W.Settings.FollowSystemAccent = follow.IsOn;
                    W.Settings.CustomAccent = color.Text; await W.SaveSettingsAsync(); ThemeHelper.Apply(W.Settings);
                });
                Link<AppearancePage>("默认会话外观", "背景与消息颜色", "default");
                Link<SettingsPage>("文字与可读性", "字号与行高", "typography");
            } else {
                bool conversation = Context.Section == "chat";
                var source = Context.Section == "default" ? W.Settings.DefaultAppearance : conversation
                    ? Context.Conversation.AppearanceOverride ?? Context.Profile.Appearance : Context.Profile.Appearance;
                var draft = JsonBridge.Clone(source);
                Title("会话外观", Context.Section == "default" ? "全局默认" : Context.Profile.Name);
                var type = Choice("背景类型", new[] { "none", "solid", "image" }, draft.BackgroundType);
                var value = Field("背景颜色 / 图片路径", draft.BackgroundValue);
                Row("选择背景图片", "从相册选择并压缩", async delegate {
                    var file = await FilesPage.PickAsync(true); if (file == null) return;
                    var bytes = await Yanshuai.Messages.Platform.ImagePreparation.CompressAsync(await FilesPage.ReadBytes(file, 10 * 1024 * 1024), 1536);
                    string root = Context.Section == "default" ? "_shared/backgrounds" : WorkspaceLayout.FilesDir(Context.Profile.Slug);
                    string path = root + "/" + Guid.NewGuid().ToString("N") + ".jpg";
                    await S.Files.WriteBytesAsync(path, bytes);
                    type.SelectedItem = "image"; value.Text = path;
                });
                var opacity = Range("背景不透明度 %", draft.BackgroundOpacity, 0, 100);
                var user = Field("用户气泡色（留空跟随强调色）", draft.UserBubbleColor);
                var quote = Field("引用文字色", draft.QuoteColor);
                var bracket = Field("括号文字色", draft.BracketColor);
                Save(async delegate {
                    draft.BackgroundType = (string)type.SelectedItem; draft.BackgroundValue = value.Text;
                    draft.BackgroundOpacity = (int)opacity.Value; draft.UserBubbleColor = user.Text; draft.QuoteColor = quote.Text; draft.BracketColor = bracket.Text;
                    if (draft.BackgroundType == "solid" && ThemeHelper.ParseHex(value.Text).A == 0) throw new ArgumentException("背景颜色不正确。");
                    foreach (var color in new[] { user.Text, quote.Text, bracket.Text })
                        if (color.Length > 0 && ThemeHelper.ParseHex(color).A == 0) throw new ArgumentException("请输入 #RRGGBB 色值。");
                    if (conversation) { Context.Conversation.AppearanceOverride = draft; await W.SaveConversationAsync(Context.Profile.Slug, Context.Conversation); }
                    else if (Context.Section == "default") { W.Settings.DefaultAppearance = draft; await W.SaveSettingsAsync(); }
                    else { Context.Profile.Appearance = draft; await W.SaveProfileAsync(Context.Profile); }
                });
                if (conversation) Command("跟随档案", Symbol.Refresh, async delegate {
                    Context.Conversation.AppearanceOverride = null; await W.SaveConversationAsync(Context.Profile.Slug, Context.Conversation); Finish();
                }, true);
            }
            return Task.CompletedTask;
        }
    }

    public sealed class ContentRulesPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            bool regex = Context.Section == "regex";
            Title(regex ? "正则转换器" : "快捷消息", "内容规则");
            if (regex) foreach (var rule in W.Settings.RegexRules)
                Link<RuleEditorPage>(rule.Name, rule.Find + " · " + rule.Scope + (rule.VisualOnly ? " · 仅显示" : ""), "regex", rule);
            else foreach (var item in W.Library.QuickMessages.OrderBy(x => x.Sort))
                Link<RuleEditorPage>(item.Title + (item.Visible ? "" : " · 隐藏"), item.Content, "quick", item);
            if (Body.Children.Count == 0) Text("暂无规则，点「新增」添加。");
            Command("新增", Symbol.Add, delegate { Go<RuleEditorPage>(regex ? "regex" : "quick", regex ? (object)new RegexTransform() : new QuickMessage()); return Task.CompletedTask; });
            if (regex) Command("测试", Symbol.Play, delegate { Go<RegexTestPage>(); return Task.CompletedTask; });
            return Task.CompletedTask;
        }
    }
    public sealed class RuleEditorPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            bool regex = Context.Section == "regex";
            Title(regex ? "正则规则" : "快捷消息", "编辑");
            Func<Task> save, delete;
            if (regex) {
                var source = (RegexTransform)Context.Item;
                var name = Field("名称", source.Name); var find = Field("查找模式", source.Find, true);
                var replace = Field("替换（支持 $1 等捕获组）", source.Replace, true);
                var scope = Choice("作用范围", new[] { "user", "assistant", "both" }, source.Scope);
                var visual = Toggle("仅改变显示", source.VisualOnly); var enabled = Toggle("启用", source.Enabled);
                save = async delegate {
                    new System.Text.RegularExpressions.Regex(Required(find), System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(200));
                    var draft = source.Clone(); draft.Name = Required(name); draft.Find = find.Text; draft.Replace = replace.Text;
                    draft.Scope = (string)scope.SelectedItem; draft.VisualOnly = visual.IsOn; draft.Enabled = enabled.IsOn;
                    W.Settings.RegexRules.RemoveAll(x => x.Id == draft.Id); W.Settings.RegexRules.Add(draft); await W.SaveSettingsAsync();
                };
                delete = async delegate { W.Settings.RegexRules.RemoveAll(x => x.Id == source.Id); await W.SaveSettingsAsync(); };
                Command("测试规则", Symbol.Play, delegate {
                    var draft = source.Clone(); draft.Find = find.Text; draft.Replace = replace.Text;
                    draft.Scope = (string)scope.SelectedItem; draft.VisualOnly = visual.IsOn; draft.Enabled = enabled.IsOn;
                    Go<RegexTestPage>(null, draft); return Task.CompletedTask;
                }, true);
            } else {
                var source = (QuickMessage)Context.Item;
                var title = Field("按钮文字", source.Title); var content = Field("填入内容", source.Content, true);
                var sort = Field("排序", source.Sort.ToString()); var visible = Toggle("显示在快捷栏", source.Visible);
                Text("点快捷消息只填入输入框，由你修改后发送。", true);
                save = async delegate {
                    var draft = JsonBridge.Clone(source); draft.Title = Required(title); draft.Content = Required(content);
                    draft.Sort = Int(sort, -10000, 10000); draft.Visible = visible.IsOn;
                    W.Library.QuickMessages.RemoveAll(x => x.Id == draft.Id); W.Library.QuickMessages.Add(draft); await W.SaveLibraryAsync();
                };
                delete = async delegate { W.Library.QuickMessages.RemoveAll(x => x.Id == source.Id); await W.SaveLibraryAsync(); };
            }
            Save(save);
            Command("删除", Symbol.Delete, async delegate {
                if (await Confirm("删除此规则？", "规则将从配置中移除。", "删除")) { await delete(); Finish(); }
            }, true);
            return Task.CompletedTask;
        }
    }
    public sealed class RegexTestPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            Title("正则测试", "内容规则");
            var input = Field("输入", "", true);
            var scope = Choice("消息角色", new[] { "user", "assistant" }, "assistant");
            var visual = Toggle("显示阶段（关为发送阶段）", true);
            var output = Field("输出", "", true); output.IsReadOnly = true;
            Command("运行测试", Symbol.Play, delegate {
                var transformer = new RegexTransformer();
                transformer.Load(Context.Item is RegexTransform ? (System.Collections.Generic.IEnumerable<RegexTransform>)new[] { (RegexTransform)Context.Item } : W.Settings.RegexRules);
                output.Text = transformer.Apply(input.Text, scope.SelectedIndex == 0, visual.IsOn);
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        }
    }

    public sealed class DiagnosticsPage : DetailPage
    {
        protected override async Task BuildAsync()
        {
            bool usage = Context.Section == "usage"; bool crash = Context.Section == "crash";
            Title(usage ? "会话用量" : crash ? "崩溃日志" : "诊断日志", "关于与诊断");
            string text;
            if (usage) {
                if (Context.Conversation == null) throw new InvalidOperationException("请先打开会话。");
                var entries = await S.TechLog.ReadAsync(Context.Profile.Slug, Context.Conversation.Id, 0, Lifetime.Token);
                text = TokenTechLog.Format(TokenTechLog.Summarize(entries), entries);
            } else if (crash) {
                var file = await Windows.Storage.ApplicationData.Current.LocalFolder.TryGetItemAsync("crash.log") as Windows.Storage.StorageFile;
                text = file == null ? "没有崩溃日志。" : await Windows.Storage.FileIO.ReadTextAsync(file);
            } else text = await S.Log.ReadTailAsync(400);
            var box = Field("日志", text, true); box.IsReadOnly = true;
            Text("用量与诊断只记录元数据；导出前可查看内容。", true);
            Command("复制", Symbol.Copy, delegate {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); Status.Text = "已复制。"; return Task.CompletedTask;
            });
            Link<DiagnosticsPage>("崩溃日志", "查看启动与异常记录", "crash");
            Link<SettingsPage>("重建会话索引", "索引维护", "workspace");
        }
    }
    public sealed class AboutPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            Title("关于言枢", "我的"); Text("言枢 · 消息");
            var version = Windows.ApplicationModel.Package.Current.Id.Version;
            Text(version.Major + "." + version.Minor + "." + version.Build + "." + version.Revision);
            Text("Windows 10 Mobile / UWP · 人设、工具、记忆和模型服务。");
            Link<DiagnosticsPage>("诊断日志", "脱敏记录");
            Link<ToolDetailsPage>("脚本运行时", "PyUwp 运行状态");
            return Task.CompletedTask;
        }
    }
}
