using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Ui.Pages
{
    /// <summary>三种模型分工的一句话描述。「我的」页和「模型」页共用。</summary>
    public static class ModelRoles
    {
        public static string Chat(Workspace w)
        {
            var api = ModelSelection.Resolve(w.ApiProfiles, null, null);
            if (api == null) return "还没有添加 Provider";
            return string.IsNullOrEmpty(api.Model) ? api.Name + " · 未选模型" : api.Model + " · " + api.Name;
        }

        public static string Assistant(Workspace w)
        {
            var chosen = ModelSelection.ResolveRef(w.ApiProfiles, w.Settings.AssistantModel);
            if (chosen != null) return chosen.Model + " · " + chosen.Name;
            var fallback = ModelSelection.ResolveAssistant(w.ApiProfiles, null, null, null);
            if (fallback == null || string.IsNullOrEmpty(fallback.Model)) return "跟随对话模型";
            return "跟随对话模型（" + fallback.Model + "）";
        }

        public static string Speech(Workspace w)
        {
            var s = w.Settings;
            if (s.SpeechEngine != SpeechEngines.Model)
                return "系统语音" + (string.IsNullOrEmpty(s.SpeechVoice) ? "" : " · " + s.SpeechVoice);
            var settings = TtsSettings.From(w);
            if (settings.Cloud == null) return "朗读模型不可用 · " + settings.CloudProblem;
            return settings.Cloud.Api.Model + " · " + (string.IsNullOrEmpty(settings.Cloud.Voice) ? "默认音色" : settings.Cloud.Voice);
        }

        public static string Short(Workspace w)
        {
            var chat = ModelSelection.Resolve(w.ApiProfiles, null, null);
            if (chat == null) return "还没有添加 Provider，去加一个";
            var assistant = ModelSelection.ResolveAssistant(w.ApiProfiles, w.Settings.AssistantModel, null, null);
            var text = "对话 " + (string.IsNullOrEmpty(chat.Model) ? "未选" : chat.Model);
            if (assistant != null && !string.IsNullOrEmpty(assistant.Model) && assistant.Model != chat.Model) text += " · 副手 " + assistant.Model;
            return text;
        }
    }

    /// <summary>模型分工：对话模型、副手模型、朗读。一屏看清每件事交给了谁。</summary>
    public sealed class ModelRolesPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            Title("模型", "我的");
            var settings = W.Settings;

            Text("不同的事交给不同的模型：聊天用好的，后台小任务用快的，省钱也省时间。", true);

            Section("对话");
            Row("对话模型", ModelRoles.Chat(W), delegate { Go<ModelPickerPage>("global"); return Task.CompletedTask; }, "\uE8BD");

            Section("副手");
            Row("副手模型", ModelRoles.Assistant(W), delegate { Go<ModelPickerPage>("assistant"); return Task.CompletedTask; }, "\uE945");
            Text("负责起标题、翻译、回复建议、压缩上下文和整理记忆。", true);
            var suggestions = Toggle("回复建议", settings.ReplySuggestions, "每次回复后，在输入框上方给几句你可能想说的话");
            var titles = Toggle("自动起标题", settings.AiTitles, "第一轮回复后，给会话起一个简短的标题");
            RoutedEventHandler save = async delegate {
                settings.ReplySuggestions = suggestions.IsOn; settings.AiTitles = titles.IsOn;
                await Safe(W.SaveSettingsAsync);
            };
            suggestions.Toggled += save; titles.Toggled += save;

            Section("朗读");
            Row("朗读", ModelRoles.Speech(W), delegate { Go<SettingsPage>("speech"); return Task.CompletedTask; }, "\uE767");

            Section("服务");
            Row("Provider", W.ApiProfiles.Items.Count + " 个 · 地址、密钥和模型列表", delegate { Go<ApiProfilesPage>(); return Task.CompletedTask; }, "\uE774");

            if (settings.AssistantModel.IsSet)
                Command("副手跟随对话模型", Symbol.Undo, async delegate { settings.AssistantModel.Clear(); await W.SaveSettingsAsync(); await BuildAsync(); }, true);
            return Task.CompletedTask;
        }
    }

    public sealed class ApiProfilesPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            Title("Provider", "模型与服务");
            Text("每个 Provider 独立保存地址、密钥和模型列表。切换模型时同时切换所属服务。", true);
            if (W.ApiProfiles.Items.Count == 0) Text("还没有 Provider。添加一个服务后，自动获取它提供的模型。");
            foreach (var api in W.ApiProfiles.Items)
            {
                var item = api;
                int count = ModelCatalogBuilder.Build(new ApiProfileFile { Items = new List<ApiProfile> { api } }, "", "").Count;
                var row = Row((api.Id == W.ApiProfiles.DefaultId ? "★ " : "") + api.Name,
                    api.Provider + " · " + count + " 个模型 · 默认 " + (string.IsNullOrEmpty(api.Model) ? "未选择" : api.Model),
                    delegate { Go<ProviderModelsPage>("provider", item); return Task.CompletedTask; });
                row.Holding += delegate (object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e) {
                    if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;
                    Actions(item, row); e.Handled = true;
                };
                row.RightTapped += delegate (object sender, Windows.UI.Xaml.Input.RightTappedRoutedEventArgs e) { Actions(item, row); e.Handled = true; };
            }
            Command("添加 Provider", Symbol.Add, delegate { Go<ApiEditorPage>(null, new ApiProfile()); return Task.CompletedTask; });
            Command("选择默认模型", Symbol.Switch, delegate { Go<ModelPickerPage>("global"); return Task.CompletedTask; });
            Command("刷新全部", Symbol.Refresh, async delegate {
                int ok = 0; var failures = new List<string>();
                foreach (var api in W.ApiProfiles.Items) {
                    var result = await ModelCatalog.FetchAsync(S.Http, api, W.GetApiKey(api), S.Log, Lifetime.Token);
                    if (result.Ok) { api.ApplyModelList(result, S.Clock.UtcNow); ok++; }
                    else failures.Add(api.Name + "：" + result.Error);
                }
                await W.SaveApiProfilesAsync(); await BuildAsync();
                Status.Text = "已刷新 " + ok + " 个 Provider" + (failures.Count > 0 ? "\n" + string.Join("\n", failures) : "。");
            }, true);
            return Task.CompletedTask;
        }
        private void Actions(ApiProfile api, FrameworkElement target)
        {
            var menu = new MenuFlyout();
            Action<string, Func<Task>> add = delegate (string title, Func<Task> action) {
                var item = new MenuFlyoutItem { Text = title }; item.Click += async delegate { await Safe(action); }; menu.Items.Add(item);
            };
            add("管理模型", delegate { Go<ProviderModelsPage>("provider", api); return Task.CompletedTask; });
            add("编辑 Provider", delegate { Go<ApiEditorPage>(null, api); return Task.CompletedTask; });
            add("复制 Provider", async delegate {
                RequireIdle(); var copy = api.Clone(); copy.Id = Guid.NewGuid().ToString("N"); copy.Name = api.Name + " 副本";
                W.ApiProfiles.Items.Add(copy); W.SetApiKey(copy, W.GetApiKey(api)); await W.SaveApiProfilesAsync(); await BuildAsync();
            });
            add("删除 Provider", async delegate {
                RequireIdle();
                if (await Confirm("删除 Provider？", "模型列表和该服务的密钥也会移除。使用它的会话需要重新选择模型。", "删除")) {
                    await W.DeleteApiProfileAsync(api.Id); await BuildAsync();
                }
            });
            menu.ShowAt(target);
        }
    }

    public sealed class ApiEditorPage : DetailPage
    {
        public ApiEditorPage() { NavigationCacheMode = Windows.UI.Xaml.Navigation.NavigationCacheMode.Required; RetainDraftOnBack = true; }
        protected override Task BuildAsync()
        {
            var source = Context.Item as ApiProfile ?? new ApiProfile();
            var draft = source.Clone();
            bool exists = W.ApiProfiles.Find(source.Id) != null;
            Title(exists ? "编辑 Provider" : "添加 Provider", "模型与服务");
            var name = Field("服务名称", draft.Name == "新配置" ? "" : draft.Name);
            var type = Choice("接口协议", new[] { "openai", "anthropic", "gemini" }, draft.Provider);
            Text("OpenAI 兼容协议适用于兼容服务和本地模型；Anthropic / Gemini 使用各自的原生接口。", true);
            var url = Field("服务地址", draft.Url);
            url.PlaceholderText = "https://example.com/v1";
            Text("可填写服务根地址、/v1 地址或完整对话端点。留空使用所选协议的官方地址。", true);
            var key = Secret("API 密钥", W.GetApiKey(source));
            var requiresKey = Toggle("服务需要 API 密钥", draft.RequiresApiKey);
            Text("密钥保存到系统凭据保险库，同一 Provider 下的模型共用这份密钥。", true);
            var autoFetch = Toggle("保存时自动获取模型", true);
            Section("高级");
            var modelsUrl = Field("模型列表地址（可选）", draft.ModelsUrl);
            Text("留空自动推导 /v1/models；非标准部署可填写完整模型列表地址。", true);
            var vision = Toggle("能力未知时允许图片输入", draft.VisionEnabled);
            var headers = Field("自定义请求头（JSON）", JsonStore.Serialize(draft.CustomHeaders), true);
            var body = Field("自定义请求体（JSON）", JsonStore.Serialize(draft.CustomBody), true);
            Action collect = delegate {
                draft.Name = Required(name); draft.Provider = (string)type.SelectedItem; draft.Url = url.Text.Trim();
                draft.ModelsUrl = modelsUrl.Text.Trim(); draft.RequiresApiKey = requiresKey.IsOn; draft.VisionEnabled = vision.IsOn;
                ValidateUrl(draft.Url); ValidateUrl(draft.ModelsUrl);
                draft.CustomHeaders = JsonObject<Dictionary<string, string>>(headers);
                draft.CustomBody = JsonObject<Dictionary<string, object>>(body);
            };
            Save(async delegate {
                RequireIdle(); collect();
                string fetchError = "";
                if (autoFetch.IsOn) {
                    var result = await ModelCatalog.FetchAsync(S.Http, draft, key.Password, S.Log, Lifetime.Token);
                    if (result.Ok) draft.ApplyModelList(result, S.Clock.UtcNow);
                    else fetchError = result.Error;
                }
                draft.ModelsFetchError = fetchError;
                if (exists) W.ApiProfiles.Items[W.ApiProfiles.Items.FindIndex(x => x.Id == source.Id)] = draft;
                else W.ApiProfiles.Items.Add(draft);
                if (string.IsNullOrEmpty(W.ApiProfiles.DefaultId)) W.ApiProfiles.DefaultId = draft.Id;
                W.SetApiKey(draft, key.Password); await W.SaveApiProfilesAsync();
            });
            Command("获取模型", Symbol.Download, async delegate {
                collect();
                var result = await ModelCatalog.FetchAsync(S.Http, draft, key.Password, S.Log, Lifetime.Token);
                if (!result.Ok) throw new InvalidOperationException(result.Error);
                draft.ApplyModelList(result, S.Clock.UtcNow); draft.ModelsFetchError = "";
                Status.Text = "已获取 " + result.Models.Count + " 个模型 · 保存后生效。";
                Section("获取结果");
                foreach (var m in result.Entries.Take(20)) Text(m.ModelId + " · " + new ModelCatalogEntry { Metadata = m }.Badges, true);
                if (result.Entries.Count > 20) Text("其余模型保存后可在 Provider 的模型列表中查看。", true);
            }, true);
            Command("测试连接", Symbol.Refresh, delegate {
                collect(); Go<ApiTestPage>(null, new ApiProbe { Api = draft.Clone(), Key = key.Password }, true); return Task.CompletedTask;
            }, true);
            return Task.CompletedTask;
        }
        private static void ValidateUrl(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                throw new ArgumentException("请输入完整的 HTTP / HTTPS 地址。");
        }
    }

    public sealed class ProviderModelsPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var passed = Context.Item as ApiProfile;
            var api = passed != null ? W.ApiProfiles.Find(passed.Id) : null;
            if (api == null) { Title("Provider 不存在", "模型与服务"); return Task.CompletedTask; }
            Title(api.Name, "Provider 的模型");
            Text(api.Provider + " · " + new Uri(api.EffectiveUrl).Host, true);
            if (!string.IsNullOrEmpty(api.ModelsFetchError)) Text("上次获取模型失败：" + api.ModelsFetchError + "。已保留原来的模型列表。");
            Text(api.ModelsFetchedAtUtc.HasValue ? "上次刷新：" + api.ModelsFetchedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "尚未获取模型列表。", true);
            Text("点击模型设为这个 Provider 的默认模型；会话中的模型切换不会改变这里的默认值。", true);
            var search = Field("搜索这个 Provider 的模型", "");
            var list = new StackPanel(); Body.Children.Add(list);
            Action populate = delegate {
                list.Children.Clear();
                var entries = ModelCatalogBuilder.Build(new ApiProfileFile { Items = new List<ApiProfile> { api } }, api.Id, api.Model).Entries
                    .Where(x => (x.ModelId + " " + x.Metadata.DisplayName).IndexOf(search.Text ?? "", StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
                if (entries.Count == 0) list.Children.Add(new TextBlock { Text = "没有匹配的模型。可以刷新列表或手动添加。", TextWrapping = TextWrapping.Wrap });
                foreach (var entry in entries) {
                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var select = ModelPickerPage.ModelRow(entry);
                    select.Click += async delegate { await Safe(async delegate { RequireIdle(); api.Model = entry.ModelId; await W.SaveApiProfilesAsync(); await BuildAsync(); }); };
                    var detail = ModelPickerPage.IconAction("\uE946", "模型详情");
                    detail.Click += delegate { Go<ModelDetailsPage>(null, entry); };
                    grid.Children.Add(select); Grid.SetColumn(detail, 1); grid.Children.Add(detail); list.Children.Add(grid);
                }
            };
            search.TextChanged += delegate { populate(); }; populate();
            Section("默认选择");
            Row("全局默认", W.ApiProfiles.DefaultId == api.Id ? "正在使用这个 Provider" : "使用 " + api.Name + " 的默认模型",
                async delegate { RequireIdle(); if (string.IsNullOrEmpty(api.Model)) throw new InvalidOperationException("请先选择一个默认模型。");
                    ModelSelection.SetDefault(W.ApiProfiles, api.Id, api.Model); await W.SaveApiProfilesAsync(); await BuildAsync(); });
            Link<ModelRolesPage>("副手模型", ModelRoles.Assistant(W));
            Command("刷新模型", Symbol.Refresh, async delegate {
                var result = await ModelCatalog.FetchAsync(S.Http, api, W.GetApiKey(api), S.Log, Lifetime.Token);
                if (!result.Ok) { api.ModelsFetchError = result.Error; await W.SaveApiProfilesAsync(); Status.Text = result.Error; return; }
                api.ApplyModelList(result, S.Clock.UtcNow); api.ModelsFetchError = "";
                await W.SaveApiProfilesAsync(); await BuildAsync(); Status.Text = "已获取 " + result.Models.Count + " 个模型。";
            });
            Command("手动添加", Symbol.Add, delegate { Go<ModelDetailsPage>("manual", api); return Task.CompletedTask; });
            Command("编辑服务", Symbol.Edit, delegate { Go<ApiEditorPage>(null, api); return Task.CompletedTask; });
            return Task.CompletedTask;
        }
    }

    public sealed class ModelPickerPage : DetailPage
    {
        private TextBox _search;
        private StackPanel _models;
        private ApiProfile TargetProvider { get { var item = Context.Item as ApiProfile; return item != null ? W.ApiProfiles.Find(item.Id) : null; } }
        private bool Global { get { return Context.Section == "global"; } }
        private bool Assistant { get { return Context.Section == "assistant"; } }
        private bool ConversationScope { get { return !Global && !Assistant && TargetProvider == null && Context.Section != "profile" && Context.Conversation != null; } }
        protected override Task BuildAsync()
        {
            if (Context.Item is ApiProfile && TargetProvider == null) { Title("Provider 已删除", "模型与服务"); Link<ApiProfilesPage>("管理 Provider", "重新选择一个服务"); return Task.CompletedTask; }
            if (Assistant)
            {
                Title("副手模型", "起标题 · 翻译 · 回复建议");
                Text("挑一个响应快、价格低的模型。它不参与正式对话，只做后台小任务。", true);
            }
            else
            {
                Title(Global ? "对话模型" : "切换模型", TargetProvider != null ? TargetProvider.Name : ConversationScope ? "当前会话" : Global || Context.Profile == null ? "全局默认" : "当前档案");
                Text(ConversationScope ? "只改这个会话的模型，内容保留。" : "点选一个模型。星标收藏的会排在最上面。", true);
            }
            _search = Field("搜索模型或 Provider", "");
            _models = new StackPanel(); Body.Children.Add(_models);
            _search.TextChanged += delegate { Populate(); }; Populate();
            Command("刷新模型", Symbol.Refresh, async delegate {
                var failures = new List<string>();
                foreach (var api in W.ApiProfiles.Items.Where(x => TargetProvider == null || x.Id == TargetProvider.Id)) {
                    var result = await ModelCatalog.FetchAsync(S.Http, api, W.GetApiKey(api), S.Log, Lifetime.Token);
                    if (result.Ok) { api.ApplyModelList(result, S.Clock.UtcNow); api.ModelsFetchError = ""; }
                    else { api.ModelsFetchError = result.Error; failures.Add(api.Name + "：" + result.Error); }
                }
                await W.SaveApiProfilesAsync(); Populate(); Status.Text = failures.Count > 0 ? string.Join("\n", failures) : "模型列表已更新。";
            });
            Command("管理 Provider", Symbol.Setting, delegate { Go<ApiProfilesPage>(); return Task.CompletedTask; });
            if (ConversationScope) Command("跟随档案", Symbol.Undo, async delegate {
                RequireIdle(); Context.Conversation.ModelApiProfileId = ""; Context.Conversation.ModelOverride = "";
                await SaveConversationSelection(); Context.Close();
            }, true);
            if (Context.Section == "provider-sub") Command("跟随主模型", Symbol.Undo, async delegate {
                RequireIdle(); W.ApiProfiles.Find(TargetProvider.Id).SubModel = ""; await W.SaveApiProfilesAsync(); Finish();
            }, true);
            if (Assistant && W.Settings.AssistantModel.IsSet) Command("跟随对话模型", Symbol.Undo, async delegate {
                W.Settings.AssistantModel.Clear(); await W.SaveSettingsAsync(); Finish();
            }, true);
            return Task.CompletedTask;
        }
        private Task SaveConversationSelection()
        {
            if (S.Chat.IsDraft && ReferenceEquals(S.Chat.Conversation, Context.Conversation)) return Task.CompletedTask;
            return W.SaveConversationAsync(Context.Profile.Slug, Context.Conversation);
        }
        private void Populate()
        {
            _models.Children.Clear();
            var active = Assistant ? ModelSelection.ResolveRef(W.ApiProfiles, W.Settings.AssistantModel)
                : ModelSelection.Resolve(W.ApiProfiles, Global ? null : Context.Profile, ConversationScope ? Context.Conversation : null);
            string selectedApi = TargetProvider != null ? TargetProvider.Id : active != null ? active.Id : "";
            string selectedModel = TargetProvider != null ? Context.Section == "provider-sub" ? TargetProvider.SubModel : TargetProvider.Model : active != null ? active.Model : "";
            var entries = ModelCatalogBuilder.Build(W.ApiProfiles, selectedApi, selectedModel).Entries
                .Where(x => (TargetProvider == null || x.ApiProfileId == TargetProvider.Id) &&
                    (x.ModelId + " " + x.Metadata.DisplayName + " " + x.ApiProfileName).IndexOf(_search.Text ?? "", StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            if (entries.Count == 0) _models.Children.Add(new TextBlock { Text = "没有匹配的模型。可刷新模型列表，或在 Provider 中手动添加。", TextWrapping = TextWrapping.Wrap });
            var favorites = entries.Where(x => W.Settings.FavoriteModels.Contains(x.ApiProfileId + "/" + x.ModelId)).ToList();
            if (favorites.Count > 0) AddGroup("收藏", null, favorites);
            foreach (var api in W.ApiProfiles.Items) {
                var group = entries.Where(x => x.ApiProfileId == api.Id).ToList();
                if (group.Count > 0) AddGroup(api.Name, api.Id, group);
            }
        }
        private void AddGroup(string name, string apiId, List<ModelCatalogEntry> entries)
        {
            // 分组头：分节标签 + 数量 + 折叠箭头（设计稿 model-picker），不再是一条灰色按钮
            var label = new TextBlock { Text = name + " · " + entries.Count, Style = FindStyle("SectionLabel"), Margin = new Thickness(0) };
            var arrow = new TextBlock { FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 11, Foreground = Brush("MidBrush"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            var headRow = new StackPanel { Orientation = Orientation.Horizontal };
            headRow.Children.Add(label); headRow.Children.Add(arrow);
            var header = new Button { Content = headRow, Style = FindStyle("BareButton"), MinHeight = 36,
                HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 0) };
            _models.Children.Add(header);
            var list = new StackPanel { Visibility = apiId != null && W.Settings.CollapsedProviders.Contains(apiId) && string.IsNullOrEmpty(_search.Text) ? Visibility.Collapsed : Visibility.Visible };
            Action syncArrow = delegate { arrow.Text = list.Visibility == Visibility.Visible ? "\uE70E" : "\uE70D"; };
            syncArrow();
            header.Click += async delegate { await Safe(async delegate {
                if (apiId == null) { list.Visibility = list.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; syncArrow(); return; }
                if (W.Settings.CollapsedProviders.Contains(apiId)) W.Settings.CollapsedProviders.Remove(apiId); else W.Settings.CollapsedProviders.Add(apiId);
                await W.SaveSettingsAsync(); list.Visibility = W.Settings.CollapsedProviders.Contains(apiId) ? Visibility.Collapsed : Visibility.Visible;
                syncArrow();
            }); };
            foreach (var entry in entries)
            {
                var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var choose = ModelRow(entry);
                choose.Click += async delegate { await Safe(async delegate {
                    var api = W.ApiProfiles.Find(entry.ApiProfileId);
                    if (Assistant) {
                        W.Settings.AssistantModel = new ModelRef { ApiProfileId = api.Id, Model = entry.ModelId };
                        await W.SaveSettingsAsync(); Finish(); return;
                    }
                    RequireIdle();
                    if (TargetProvider != null) {
                        if (Context.Section == "provider-sub") api.SubModel = entry.ModelId; else api.Model = entry.ModelId;
                        await W.SaveApiProfilesAsync(); Finish();
                    } else if (ConversationScope) {
                        Context.Conversation.ModelApiProfileId = api.Id; Context.Conversation.ModelOverride = entry.ModelId;
                        await SaveConversationSelection(); Context.Close();
                    } else if (!Global && Context.Profile != null) {
                        Context.Profile.Api.ProfileId = api.Id; Context.Profile.Api.Model = entry.ModelId; await W.SaveProfileAsync(Context.Profile); Finish();
                    } else {
                        ModelSelection.SetDefault(W.ApiProfiles, api.Id, entry.ModelId); await W.SaveApiProfilesAsync(); Finish();
                    }
                }); };
                grid.Children.Add(choose);
                var detail = IconAction("\uE946", "模型详情");
                detail.Click += delegate { Go<ModelDetailsPage>(null, entry); };
                Grid.SetColumn(detail, 1); grid.Children.Add(detail);
                string favoriteKey = entry.ApiProfileId + "/" + entry.ModelId;
                // 收藏：空心 / 实心星，开时强调色文字（设计稿 .ibtn.on），不用会整块填色的 ToggleButton
                var star = IconAction("", "收藏");
                Action syncStar = delegate {
                    bool on = W.Settings.FavoriteModels.Contains(favoriteKey);
                    star.Content = on ? "\uE735" : "\uE734";
                    star.Foreground = Brush(on ? "AccentTextBrush" : "MidBrush");
                };
                syncStar();
                star.Click += async delegate { await Safe(async delegate {
                    if (!W.Settings.FavoriteModels.Contains(favoriteKey)) W.Settings.FavoriteModels.Add(favoriteKey);
                    else W.Settings.FavoriteModels.Remove(favoriteKey);
                    syncStar();
                    await W.SaveSettingsAsync();
                }); };
                Grid.SetColumn(star, 2); grid.Children.Add(star); list.Children.Add(grid);
            }
            _models.Children.Add(list);
        }

        internal static Button ModelRow(ModelCatalogEntry entry)
        {
            var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(new TextBlock { Text = (entry.IsDefault ? "\u2713 " : "") + entry.ModelId, FontSize = 15, TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(entry.IsDefault ? "AccentTextBrush" : "TextBrush") });
            if (!string.IsNullOrEmpty(entry.Badges))
                lines.Children.Add(new TextBlock { Text = entry.Badges, FontSize = 12, Foreground = Brush("MidBrush"), TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 0, 0) });
            return new Button { Content = lines, Style = FindStyle("RowButton"), Margin = new Thickness(-12, 0, 0, 0) };
        }

        internal static Button IconAction(string glyph, string name)
        {
            var button = new Button { Content = glyph, Style = FindStyle("IconButton"), Width = 40, FontSize = 16,
                Foreground = Brush("MidBrush"), VerticalAlignment = VerticalAlignment.Center };
            Windows.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            return button;
        }
    }

    public sealed class ModelDetailsPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var entry = Context.Item as ModelCatalogEntry;
            var initial = entry != null ? W.ApiProfiles.Find(entry.ApiProfileId) : Context.Item as ApiProfile;
            bool manual = entry == null;
            Title(manual ? "添加模型" : "模型能力", "Provider 的模型");
            var apis = W.ApiProfiles.Items;
            if (apis.Count == 0) { Link<ApiProfilesPage>("先添加 Provider", "配置服务地址与密钥"); return Task.CompletedTask; }
            var service = new ComboBox { Header = "所属 Provider", ItemsSource = apis, DisplayMemberPath = "Name", SelectedItem = initial ?? apis[0],
                HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 8), IsEnabled = manual };
            Body.Children.Add(service);
            var id = Field("模型 ID", entry != null ? entry.ModelId : ""); id.IsReadOnly = !manual;
            var effective = entry != null ? entry.Metadata : new ModelMetadata();
            ModelMetadata stored;
            var m = initial != null && entry != null && initial.ModelOverrides != null && initial.ModelOverrides.TryGetValue(entry.ModelId, out stored)
                ? stored.Clone() : initial != null && entry != null && initial.ModelMetadataCache.TryGetValue(entry.ModelId, out stored) && stored.Source == MetadataSource.Manual
                ? stored.Clone() : new ModelMetadata();
            Text("能力来源：" + SourceLabel(effective.Source) + " · " + effective.Origin, true);
            if (entry != null) Text("当前能力：" + entry.Badges + (effective.MaxOutputTokens > 0 ? " · 输出上限 " + effective.MaxOutputTokens : ""), true);
            Text("服务端没有提供的字段可能采用内置参考。未知能力可手动指定；刷新不会覆盖手动设置。", true);
            if (!manual) {
                Text("输入：" + (effective.InputModalities.Count > 0 ? string.Join(" / ", effective.InputModalities) : "未知") +
                    " · 输出：" + (effective.OutputModalities.Count > 0 ? string.Join(" / ", effective.OutputModalities) : "未知"), true);
                if (effective.SupportedParameters.Count > 0) Text("支持参数：" + string.Join("、", effective.SupportedParameters), true);
            }
            var window = Field("上下文窗口（0 自动）", m.ContextWindow.ToString());
            var output = Field("最大输出 tokens（0 自动）", m.MaxOutputTokens.ToString());
            var vision = Choice("图片输入", new[] { "自动", "支持", "不支持" }, m.Vision.HasValue ? m.Vision.Value ? "支持" : "不支持" : "自动");
            var reasoning = Choice("推理", new[] { "自动", "支持", "不支持" }, m.Reasoning.HasValue ? m.Reasoning.Value ? "支持" : "不支持" : "自动");
            var tools = Choice("工具调用", new[] { "自动", "支持", "不支持" }, m.Tools.HasValue ? m.Tools.Value ? "支持" : "不支持" : "自动");
            Save(async delegate {
                RequireIdle(); var api = (ApiProfile)service.SelectedItem; var modelId = Required(id);
                m.ModelId = modelId; m.DisplayName = modelId; m.ContextWindow = Int(window, 0, 2000000); m.MaxOutputTokens = Int(output, 0, 2000000);
                m.Vision = vision.SelectedIndex == 0 ? (bool?)null : vision.SelectedIndex == 1;
                m.Reasoning = reasoning.SelectedIndex == 0 ? (bool?)null : reasoning.SelectedIndex == 1;
                m.Tools = tools.SelectedIndex == 0 ? (bool?)null : tools.SelectedIndex == 1;
                m.Source = MetadataSource.Manual; m.Origin = "手动覆盖";
                m.InputModalities.Clear(); m.OutputModalities.Clear(); m.SupportedParameters.Clear();
                if (api.ModelOverrides == null) api.ModelOverrides = new Dictionary<string, ModelMetadata>();
                api.ModelOverrides[modelId] = m;
                if (manual) { if (api.ManualModels == null) api.ManualModels = new List<string>(); if (!api.ManualModels.Contains(modelId)) api.ManualModels.Add(modelId); }
                if (string.IsNullOrEmpty(api.Model)) api.Model = modelId;
                await W.SaveApiProfilesAsync();
            });
            if (entry != null) Command("恢复自动能力", Symbol.Undo, async delegate {
                RequireIdle(); var api = W.ApiProfiles.Find(entry.ApiProfileId);
                if (api.ModelOverrides != null) api.ModelOverrides.Remove(entry.ModelId);
                ModelMetadata old;
                if (api.ModelMetadataCache.TryGetValue(entry.ModelId, out old) && old.Source == MetadataSource.Manual) api.ModelMetadataCache.Remove(entry.ModelId);
                await W.SaveApiProfilesAsync(); Finish();
            }, true);
            return Task.CompletedTask;
        }
        private static string SourceLabel(MetadataSource source)
        {
            return source == MetadataSource.Server ? "服务端" : source == MetadataSource.Manual ? "手动设置" : source == MetadataSource.BuiltIn ? "内置参考（推断）" : "未知";
        }
    }
}
