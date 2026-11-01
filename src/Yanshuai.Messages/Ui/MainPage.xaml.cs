// Ui/MainPage.xaml.cs —— 根列表与会话时间线；独立功能页位于 Ui/Pages。
//
// 这一层只做三件事：把用户操作翻译成 ChatService 的调用、把结果翻译成列表项、
// 把 Agent 的回调搬回 UI 线程。它不认识 provider、工具、记忆、压缩。
//
// 接手的 AI 请注意：这个文件是刻意"薄"的。要加页面就新建 Page + ViewModel，
// 不要把逻辑再堆回这里 —— 旧项目 20 个分部文件糊在一个 MainPage 上，
// 结果是任何视觉改动都可能碰坏业务。

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Yanshuai.Messages.Core;
using Yanshuai.Messages.Platform;
using Yanshuai.Messages.Ui.Pages;

namespace Yanshuai.Messages.Ui
{
    public sealed partial class MainPage : Page, IBackHandler
    {
        private AppServices _services;
        private readonly ObservableCollection<RowVm> _rows = new ObservableCollection<RowVm>();
        private CancellationTokenSource _sendCts;
        private bool _sending;
        private bool _searchActive;
        private RowVm _streamingRow;
        private readonly StringBuilder _streamingText = new StringBuilder();

        private CancellationTokenSource _speechCts;

        public MainPage()
        {
            Platform.StartupTrace.Mark("MainPage ctor: 开始");
            try
            {
                InitializeComponent();
                Platform.StartupTrace.Mark("MainPage ctor: InitializeComponent 完成");
            }
            catch (Exception ex)
            {
                // XAML 解析失败在桌面上会抛得很清楚，但在 .NET Native 下常常直接
                // fail-fast 把进程带走、什么都不留。能 catch 到就把真实原因写下来。
                Platform.StartupTrace.Fail("MainPage.InitializeComponent", ex);
                throw;
            }
            TimelineList.ItemsSource = _rows;

            Loaded += ChatStatus_Loaded; Unloaded += ChatStatus_Unloaded;
            Platform.StartupTrace.Mark("MainPage ctor: 完成");
        }

        // ═════════════════════════════════════════════════════════════════
        // 生命周期
        // ═════════════════════════════════════════════════════════════════
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Platform.StartupTrace.Mark("MainPage.OnNavigatedTo: 开始");

            _services = e.Parameter as AppServices ?? AppServices.Current;
            if (_services == null)
            {
                ShowSimpleMessage("服务还没准备好。");
                return;
            }

            BindAttachmentNavigation();
            WireInteraction();
            Platform.StartupTrace.Mark("MainPage.OnNavigatedTo: 交互桥已挂");
            ApplyTheme();
            Platform.StartupTrace.Mark("MainPage.OnNavigatedTo: 主题已应用");

            var ignored = InitializeAsync();
            Platform.StartupTrace.Mark("MainPage.OnNavigatedTo: 完成");
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            // 离开页面不取消生成（手机上切出去看别的很常见），但要停掉交互桥上的回调，
            // 免得回到页面后还指着旧实例。生成用的 CancellationToken 保持不变。
            if (_services != null && _services.Interaction != null)
            {
                _services.Interaction.PermissionHandler = null;
            }
        }

        private void ApplyTheme() { ThemeHelper.Apply(_services.Workspace.Settings); }

        private async Task InitializeAsync()
        {
            try
            {
                Platform.StartupTrace.Mark("InitializeAsync: 开始");
                await RefreshProfilesAsync();
                Platform.StartupTrace.Mark("InitializeAsync: 档案已刷新");
                await RefreshConversationsAsync();
                Platform.StartupTrace.Mark("InitializeAsync: 会话已刷新");
                RefreshMePanel();
                Platform.StartupTrace.Mark("InitializeAsync: 我的页已刷新");
                UpdateHomeSubtitle();
                await RestoreStartupAsync();
                Platform.StartupTrace.Mark("InitializeAsync: 完成");
            }
            catch (Exception ex)
            {
                Platform.StartupTrace.Fail("InitializeAsync", ex);
                App.WriteCrashLog("INIT", ex);
                await ShowSimpleMessageAsync("初始化失败：" + ex.Message);
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // 列表刷新
        // ═════════════════════════════════════════════════════════════════
        private async Task RefreshProfilesAsync()
        {
            var items = new List<ProfileVm>();
            foreach (var profile in _services.Workspace.Profiles) items.Add(new ProfileVm(profile));

            ProfileList.ItemsSource = items;
            RefreshProfileFilters();
            EmptyProfiles.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            await Task.CompletedTask;
        }

        private async Task RefreshConversationsAsync()
        {
            int generation = ++_listGeneration;
            var entries = await _services.Workspace.ListAllConversationsAsync(false);
            if (generation != _listGeneration) return;

            var names = new Dictionary<string, string>();
            foreach (var profile in _services.Workspace.Profiles)
                if (!string.IsNullOrEmpty(profile.Id)) names[profile.Id] = profile.Name;

            var items = new List<ConversationVm>();
            foreach (var entry in entries)
            {
                if (!string.IsNullOrEmpty(_profileFilter) && entry.ProfileId != _profileFilter) continue;
                if (_searchActive && !Matches(entry)) continue;
                string profileName;
                names.TryGetValue(entry.ProfileId ?? "", out profileName);
                // 已经按档案筛选时，每行再写一遍档案名是噪音
                items.Add(new ConversationVm(entry, string.IsNullOrEmpty(_profileFilter) ? profileName : null));
            }

            var groups = new List<ConversationGroup>();
            foreach (var title in new[] { "需要你处理", "今天", "更早" }) { var group = new ConversationGroup { Title = title, Attention = title == "需要你处理" }; foreach (var vm in items) { string name = vm.Entry.NeedsAttention ? "需要你处理" : vm.Entry.UpdatedAtUtc.ToLocalTime().Date == DateTimeOffset.Now.Date ? "今天" : "更早"; if (name == title) group.Items.Add(vm); } if (group.Items.Count > 0) groups.Add(group); }
            var source = new Windows.UI.Xaml.Data.CollectionViewSource { Source = groups, IsSourceGrouped = true, ItemsPath = new PropertyPath("Items") };
            ConversationList.ItemsSource = source.View;
            // 搜索无结果 ≠ 一条会话都没有，文案与图标都要不同
            EmptyConversationsGlyph.Text = _searchActive ? "\uE721" : "\uE8BD";
            EmptyConversationsTitle.Text = _searchActive ? "没有匹配的会话" : !string.IsNullOrEmpty(_profileFilter) ? "这个档案还没有会话" : "还没有会话";
            EmptyConversationsText.Text = _searchActive ? "换个关键词试试，标题和最后一条消息都会被搜索。" : "点下面的「新建会话」开始。";
            EmptyConversations.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool Matches(ConversationIndexEntry entry)
        {
            var keyword = SearchBox.Text;
            if (string.IsNullOrWhiteSpace(keyword)) return true;

            return Contains(entry.Title, keyword) || Contains(entry.Preview, keyword);
        }

        private static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) &&
                   haystack.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void RefreshMePanel()
        {
            var settings = _services.Workspace.Settings;

            var apiCount = _services.Workspace.ApiProfiles.Items.Count;
            var defaultApi = _services.Workspace.DefaultApiProfile();
            ModelsRow.Subtitle = Pages.ModelRoles.Short(_services.Workspace);
            ApiProfilesRow.Subtitle = apiCount == 0
                ? "还没有配置，去加一个"
                : apiCount.ToString(CultureInfo.InvariantCulture) + " 个 · 地址、密钥和模型列表";
            SpeechRow.Subtitle = Pages.ModelRoles.Speech(_services.Workspace);

            SearchRow.Subtitle = settings.Search.Provider + " · 结果深度 " +
                (settings.Search.Depth <= 0 ? "不限" : settings.Search.Depth.ToString(CultureInfo.InvariantCulture) + " 字");

            var granted = _services.Workspace.Permissions.Granted.Count;
            PermissionsRow.Subtitle = granted == 0 ? "暂无「始终允许」" : granted.ToString(CultureInfo.InvariantCulture) + " 项「始终允许」";

            var quickCount = QuickMessages.Visible(_services.Workspace.Library.QuickMessages).Count;
            QuickMessagesRow.Subtitle = quickCount == 0
                ? "还没有快捷消息，点进去加一条"
                : quickCount.ToString(CultureInfo.InvariantCulture) + " 条，显示在输入框上方";

            StorageRow.Subtitle = "统计中…";
            var storageIgnored = RefreshStorageSubtitleAsync();

            AboutRow.Subtitle = "言枢·消息 1.2.0.0";
        }

        private async Task RefreshStorageSubtitleAsync()
        {
            try
            {
                // 统计本身是扫整个工作区的（MapBucket 不按档案过滤），
                // 所以没打开档案时用空 slug 也能给出有意义的数字 ——
                // 用户点开"我的"时想看的正是"这个应用一共占了多少"。
                var slug = _services.Chat.ProfileSlug ?? "";

                var stats = await _services.StorageStats.MeasureAsync(slug, CancellationToken.None);
                StorageRow.Subtitle = "已用 " + StorageStats.FormatBytes(stats.TotalBytes)
                    + " · " + stats.TotalFiles.ToString(CultureInfo.InvariantCulture) + " 个文件";
            }
            catch (Exception)
            {
                StorageRow.Subtitle = "统计失败";
            }
        }

        private void UpdateHomeSubtitle()
        {
            var profileCount = _services.Workspace.Profiles.Count;
            HomeSubtitle.Text = profileCount.ToString(CultureInfo.InvariantCulture) + " 个档案";
        }

        // 桌面启动时 Pivot 拿到键盘焦点，模板里的 HeaderClipper 会在整条标题行外画一圈白框，
        // 它用的是自己的焦点设置，Pivot 上的属性传不进去，只能在模板应用后关掉。
        // 选中头本身就是白字高亮，键盘左右键切页不受影响。
        private void HomePivot_Loaded(object sender, RoutedEventArgs e)
        {
            var clipper = FindDescendant<ContentControl>(HomePivot, "HeaderClipper");
            if (clipper != null) clipper.UseSystemFocusVisuals = false;
        }

        private static T FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            int count = Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                var typed = child as T;
                if (typed != null && typed.Name == name) return typed;
                var found = FindDescendant<T>(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private void HomePivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 切到"档案"或"我的"时收起搜索框
            if (HomePivot.SelectedIndex != 0 && _searchActive)
            {
                _searchActive = false;
                SearchBox.Visibility = Visibility.Collapsed;
                SearchBox.Text = "";
                var ignored = RefreshConversationsAsync();
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var ignored = RefreshConversationsAsync();
        }

        // ═════════════════════════════════════════════════════════════════
        // 会话列表
        // ═════════════════════════════════════════════════════════════════
        private async void ConversationList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_sending) { var active = e.ClickedItem as ConversationVm; if (active != null && _services.Chat.Conversation != null && active.Entry.Id == _services.Chat.Conversation.Id) { ShowChat(); return; } await ShowSimpleMessageAsync("当前会话正在生成，请先等本轮结束或停止。"); return; }
            var vm = e.ClickedItem as ConversationVm;
            if (vm == null) return;

            var profile = _services.Workspace.FindProfile(vm.Entry.ProfileId);
            if (profile == null)
            {
                await ShowSimpleMessageAsync("这条会话所属的档案已经找不到了。");
                return;
            }

            await _services.OpenProfileAsync(profile);

            var conversation = await _services.Workspace.LoadConversationAsync(profile.Slug, vm.Entry.Id);
            if (conversation == null)
            {
                await ShowSimpleMessageAsync("会话文件读不出来，可能已损坏。");
                return;
            }

            await _services.Chat.OpenConversationAsync(conversation);
            ShowChat();
        }

        private void NewConversation_Click(object sender, RoutedEventArgs e)
        {
            OpenDetails<NewConversationPage>();
        }

        // ═════════════════════════════════════════════════════════════════
        // 档案
        // ═════════════════════════════════════════════════════════════════
        private void ProfileList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var vm = e.ClickedItem as ProfileVm;
            if (vm != null) OpenDetails<ProfilePage>(vm.Profile);
        }

        private void NewProfile_Click(object sender, RoutedEventArgs e) { OpenDetails<NewProfilePage>(); }

        /// <summary>
        /// 把运行时的真实状态告诉用户。这里刻意不粉饰：
        /// 桌面/模拟器上加载不了 ARM 版 python-uwp.dll，就得说清楚，
        /// 否则用户打开开关后一直等一个永远不会成功的工具调用。
        /// </summary>


        // ═════════════════════════════════════════════════════════════════
        // 导入角色卡
        // ═════════════════════════════════════════════════════════════════
        private void ImportCard_Click(object sender, RoutedEventArgs e) { OpenDetails<ImportPage>(); }

        // ═════════════════════════════════════════════════════════════════
        // 我的
        // ═════════════════════════════════════════════════════════════════
        private void ApiProfiles_Click(object sender, RoutedEventArgs e) { OpenDetails<ApiProfilesPage>(); }

        private void SearchSettings_Click(object sender, RoutedEventArgs e) { OpenDetails<SearchSettingsPage>(); }

        private void Permissions_Click(object sender, RoutedEventArgs e) { OpenDetails<PermissionsPage>(); }

        private void DebugLog_Click(object sender, RoutedEventArgs e) { OpenDetails<DiagnosticsPage>(); }

        private void About_Click(object sender, RoutedEventArgs e) { OpenDetails<AboutPage>(); }

        // ═════════════════════════════════════════════════════════════════
        // 存储空间
        // ═════════════════════════════════════════════════════════════════
        private void Storage_Click(object sender, RoutedEventArgs e) { OpenDetails<StoragePage>(); }

        // ═════════════════════════════════════════════════════════════════
        // 备份与恢复
        // ═════════════════════════════════════════════════════════════════
        private void Backup_Click(object sender, RoutedEventArgs e) { OpenDetails<BackupPage>(); }

        // ═════════════════════════════════════════════════════════════════
        // 快捷消息管理
        // ═════════════════════════════════════════════════════════════════
        private void QuickMessages_Click(object sender, RoutedEventArgs e) { OpenDetails<ContentRulesPage>(); }

        // ═════════════════════════════════════════════════════════════════
        // 会话页
        // ═════════════════════════════════════════════════════════════════
        private void ShowChat()
        {
            _followTail = true;
            AttachButton.IsEnabled = !_services.Chat.IsDraft;
            HomeView.Visibility = Visibility.Collapsed;
            ChatView.Visibility = Visibility.Visible;
            ChatBackButton.Visibility = ThemeHelper.IsMobile ? Visibility.Collapsed : Visibility.Visible;
            ChatTitlePanel.Margin = new Thickness(ThemeHelper.IsMobile ? 12 : 0, 0, 4, 0);
            ApplyChatAppearance();

            var profile = _services.Chat.Profile;
            var conversation = _services.Chat.Conversation;

            ChatTitle.Text = conversation != null && !string.IsNullOrEmpty(conversation.Title)
                ? conversation.Title
                : (profile != null ? profile.Name : "对话");

            var api = _services.Workspace.DefaultApiProfile();
            var model = profile != null && !string.IsNullOrEmpty(profile.Api.Model)
                ? profile.Api.Model
                : (api != null ? api.Model : "");
            ChatSubtitle.Text = string.IsNullOrEmpty(model) ? "未配置模型" : model;

            var ignored = RenderAsync();
            var quickIgnored = EnsureQuickMessagesAsync();
            RestoreSuggestions();
            ComposerBox.Focus(FocusState.Programmatic);
        }

        private void CloseChat()
        {
            ChatView.Visibility = Visibility.Collapsed;
            HomeView.Visibility = Visibility.Visible;
            var speechIgnored = StopSpeechAsync();
            var ignored = RefreshConversationsAsync();
        }

        private void ChatBack_Click(object sender, RoutedEventArgs e)
        {
            var ignored = StopSpeechAsync();
            CloseChat();
        }

        /// <summary>
        /// 按 key 找样式/画刷。必须先查 Application.Resources ——
        /// 页面自己的 Resources 里没有 Themes/Design.xaml，而 ResourceDictionary 的
        /// 索引器**不会**往上找父级，所以 `this.Resources["X"]` 对全局样式必然抛异常。
        /// </summary>
        private static T FindResource<T>(string key) where T : class
        {
            try
            {
                var app = Application.Current;
                if (app != null && app.Resources != null)
                {
                    object value;
                    if (app.Resources.TryGetValue(key, out value))
                    {
                        var typed = value as T;
                        if (typed != null) return typed;
                    }
                }
            }
            catch (Exception) { }

            return null;
        }

        private static Style FindStyle(string key)
        {
            return FindResource<Style>(key);
        }

        private static Windows.UI.Xaml.Media.Brush FindBrush(string key)
        {
            return FindResource<Windows.UI.Xaml.Media.Brush>(key);
        }

        /// <summary>点快捷消息是**填进输入框**，不是直接发出去 —— 多半还要改几个字。</summary>
        private void FillComposer(QuickMessage item)
        {
            if (item == null) return;

            var profile = _services.Chat.Profile;
            var conversation = _services.Chat.Conversation;

            var context = new QuickMessageContext
            {
                CharacterName = profile != null ? profile.Name : "",
                UserName = _services.Workspace.ResolveUserPersona(profile) != null
                    ? _services.Workspace.ResolveUserPersona(profile).Name
                    : "",
                Now = DateTimeOffset.Now,
                ConversationTitle = conversation != null ? conversation.Title : ""
            };

            var expanded = QuickMessages.Expand(item.Content, context);

            // 已有内容就追加，不覆盖用户已经打的字
            ComposerBox.Text = string.IsNullOrEmpty(ComposerBox.Text)
                ? expanded
                : ComposerBox.Text.TrimEnd() + "\n" + expanded;

            ComposerBox.SelectionStart = ComposerBox.Text.Length;
            ComposerBox.Focus(FocusState.Programmatic);
        }

        // ═════════════════════════════════════════════════════════════════
        // 分支切换
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// 找到最后一条有多个候选的消息位置，更新分支条。
        /// 只认最后一条：历史中间的分支也切得动，但手机上"切历史"几乎没有使用场景，
        /// 反而会让界面长期挂着一个看不懂的控件。
        /// </summary>
        private void UpdateBranchBar()
        {
            var conversation = _services.Chat.Conversation;
            if (conversation == null)
            {
                BranchBar.Visibility = Visibility.Collapsed;
                return;
            }

            int index = -1;
            for (int i = conversation.Messages.Count - 1; i >= 0; i--)
            {
                if (conversation.VariantCount(i) > 1) { index = i; break; }
            }

            if (index < 0)
            {
                BranchBar.Visibility = Visibility.Collapsed;
                return;
            }

            int total = conversation.VariantCount(index);
            int current = conversation.VariantIndex(index);

            BranchText.Text = "候选 " + (current + 1).ToString(CultureInfo.InvariantCulture)
                            + " / " + total.ToString(CultureInfo.InvariantCulture);
            BranchBar.Visibility = Visibility.Visible;
        }

        private async void BranchPrev_Click(object sender, RoutedEventArgs e)
        {
            await SwitchBranchAsync(-1);
        }

        private async void BranchNext_Click(object sender, RoutedEventArgs e)
        {
            await SwitchBranchAsync(1);
        }

        private async Task SwitchBranchAsync(int delta)
        {
            var conversation = _services.Chat.Conversation;
            if (conversation == null) return;

            int index = -1;
            for (int i = conversation.Messages.Count - 1; i >= 0; i--)
            {
                if (conversation.VariantCount(i) > 1) { index = i; break; }
            }
            if (index < 0) return;

            int total = conversation.VariantCount(index);
            int current = conversation.VariantIndex(index);
            int target = ((current + delta) % total + total) % total;   // 循环切换

            if (_sending) { await ShowSimpleMessageAsync("生成中不能切换候选，请先停止。"); return; }
            if (!conversation.SelectVariant(index, target))
            {
                ShowSimpleMessage("切换失败：这个位置的候选已经不可用。");
                return;
            }

            await _services.Workspace.SaveConversationAsync(_services.Chat.ProfileSlug, conversation);
            await RenderAsync();
        }

        // ═════════════════════════════════════════════════════════════════
        // 朗读
        // ═════════════════════════════════════════════════════════════════
        private async Task SpeakLastAsync()
        {
            var tts = _services.Tts;
            var speech = TtsSettings.From(_services.Workspace);
            if (tts == null || !tts.CanSpeak(speech))
            {
                await ShowSimpleMessageAsync(string.IsNullOrEmpty(speech.CloudProblem)
                    ? "这台设备上没有可用的语音引擎。可以在「我的 → 朗读」里改用朗读模型。"
                    : "朗读模型不可用（" + speech.CloudProblem + "），这台设备也没有系统语音。");
                return;
            }

            var conversation = _services.Chat.Conversation;
            if (conversation == null) return;

            string text = null;
            for (int i = conversation.Messages.Count - 1; i >= 0; i--)
            {
                var message = conversation.Messages[i];
                if (message.Role == MessageRole.Assistant && !string.IsNullOrWhiteSpace(message.Content))
                {
                    text = message.Content;
                    break;
                }
            }

            if (string.IsNullOrEmpty(text))
            {
                await ShowSimpleMessageAsync("还没有可朗读的内容。");
                return;
            }

            await StopSpeechAsync();

            var cts = new CancellationTokenSource();
            _speechCts = cts;

            SpeechBar.Visibility = Visibility.Visible;
            SpeechText.Text = speech.Cloud != null ? "正在准备朗读…" : "正在朗读…";

            Action<int, int> progress = delegate (int index, int total)
            {
                RunOnUi(delegate { if (_speechCts == cts) SpeechText.Text = total > 1 ? "正在朗读 · " + index + " / " + total : "正在朗读…"; });
            };
            Action<string> fallback = delegate (string reason)
            {
                RunOnUi(delegate { if (_speechCts == cts) SpeechText.Text = "已改用系统语音 · " + reason; });
            };
            tts.Progress += progress;
            tts.Fallback += fallback;
            try
            {
                await tts.SpeakAsync(text, speech, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                SpeechText.Text = "朗读失败。";
            }
            finally
            {
                tts.Progress -= progress;
                tts.Fallback -= fallback;
                if (_speechCts == cts) _speechCts = null;
                RunOnUi(delegate { SpeechBar.Visibility = Visibility.Collapsed; });
            }
        }

        private async Task StopSpeechAsync()
        {
            var cts = _speechCts;
            _speechCts = null;
            if (cts != null)
            {
                try { cts.Cancel(); } catch (Exception) { }
            }

            try
            {
                if (_services.Tts != null) await _services.Tts.StopAsync();
            }
            catch (Exception) { }

            SpeechBar.Visibility = Visibility.Collapsed;
        }

        private async void StopSpeech_Click(object sender, RoutedEventArgs e)
        {
            await StopSpeechAsync();
        }

        // ═════════════════════════════════════════════════════════════════
        // 翻译
        // ═════════════════════════════════════════════════════════════════
        private async Task TranslateLastAsync()
        {
            var conversation = _services.Chat.Conversation;
            var profile = _services.Chat.Profile;
            if (conversation == null || profile == null) return;

            Message target = null;
            for (int i = conversation.Messages.Count - 1; i >= 0; i--)
            {
                var message = conversation.Messages[i];
                if (message.Role == MessageRole.Assistant && !string.IsNullOrWhiteSpace(message.Content))
                {
                    target = message;
                    break;
                }
            }

            if (target == null)
            {
                await ShowSimpleMessageAsync("还没有可翻译的内容。");
                return;
            }

            // 目标语言：界面上让用户选，默认英语（中文用户的常见需求）
            var codes = new List<string>(TranslationService.CommonLanguages);
            var names = new List<string>();
            foreach (var code in codes) names.Add(TranslationService.LanguageName(code));

            var picker = new ComboBox
            {
                ItemsSource = names,
                SelectedIndex = Math.Max(0, codes.IndexOf(_services.Workspace.Settings.TranslationLanguage)),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var dialog = new ContentDialog
            {
                Title = "翻译成",
                Content = picker,
                PrimaryButtonText = "翻译",
                CloseButtonText = "取消"
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            if (picker.SelectedIndex < 0) return;
            var language = codes[picker.SelectedIndex];

            var api = _services.Chat.ResolveAssistantApi();
            var key = api != null ? _services.Workspace.GetApiKey(api) : "";
            _services.Workspace.Settings.TranslationLanguage = language;

            SetRunning(true, "正在翻译");

            TranslationResult result;
            try
            {
                result = await _services.Translation.TranslateMessageAsync(
                    target, language, api, key, CancellationToken.None);
            }
            finally
            {
                SetRunning(false, "");
            }

            if (!result.Ok)
            {
                await ShowSimpleMessageAsync("翻译失败：" + result.ErrorMessage);
                return;
            }

            await _services.Workspace.SaveConversationAsync(_services.Chat.ProfileSlug, conversation);

            // 译文叠加显示，原文不覆盖 —— 用户随时能对照
            _translationOverlay = target.Id;
            _translationLanguage = language;
            await RenderAsync();
        }

        // ═════════════════════════════════════════════════════════════════
        // 会话用量
        // ═════════════════════════════════════════════════════════════════


        private void ChatMore_Click(object sender, RoutedEventArgs e) { ChatMenu_Click(sender, e); }

        // ═════════════════════════════════════════════════════════════════
        // 时间线渲染
        // ═════════════════════════════════════════════════════════════════
        /// <summary>从按钮所在行反查它对应的消息（行上带着 MessageId）。</summary>
        private Message MessageFromRowSender(object sender)
        {
            var element = sender as FrameworkElement;
            var row = element != null ? element.DataContext as RowVm : null;
            if (row == null || row.Row.Source == null) return null;
            var conversation = _services.Chat.Conversation;
            if (conversation == null || conversation.Messages == null) return null;
            return conversation.Messages.FirstOrDefault(x => x.Id == row.Row.Source.MessageId);
        }

        private async void BubbleCopy_Click(object sender, RoutedEventArgs e)
        {
            var message = MessageFromRowSender(sender);
            if (message == null) return;
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(message.Content ?? "");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private void BubbleBranch_Click(object sender, RoutedEventArgs e)
        {
            var message = MessageFromRowSender(sender);
            if (message == null) return;
            OpenDetails<MessageEditPage>(null, "fork", message);
        }

        /// <summary>
        /// 重试：撤掉这条助手消息及其后内容，连它对应的那条用户消息一起撤，
        /// 然后用原文本/原附件重新走一次发送（SendAsync 会重新落一条用户消息）。
        /// </summary>
        private async void BubbleRetry_Click(object sender, RoutedEventArgs e)
        {
            if (_sending) { await ShowSimpleMessageAsync("正在生成，请先停止。"); return; }
            var message = MessageFromRowSender(sender);
            if (message == null) return;
            var messages = _services.Chat.Conversation.Messages;
            int index = messages.IndexOf(message);
            if (index < 0) return;

            int userIndex = -1;
            for (int i = index - 1; i >= 0; i--)
            {
                if (messages[i].Role == MessageRole.User) { userIndex = i; break; }
            }
            if (userIndex < 0) { await ShowSimpleMessageAsync("找不到这条回复对应的用户消息，没法重试。"); return; }

            var user = messages[userIndex];
            var text = user.Content ?? "";
            var attachments = (user.Attachments != null && user.Attachments.Count > 0)
                ? new List<Attachment>(user.Attachments)
                : new List<Attachment>();

            messages.RemoveRange(userIndex, messages.Count - userIndex);
            _services.Chat.Conversation.Summary = "";
            _services.Chat.Conversation.SummarizedUpTo = -1;
            await _services.Workspace.SaveConversationAsync(_services.Chat.ProfileSlug, _services.Chat.Conversation);
            await RenderAsync();
            await RunTurnAsync(text, attachments);
        }
        // ── 「+」面板：附件 / 权限审批 / 模型与 Effort ──────────────────────
        private void ComposerFlyout_Opened(object sender, object e)
        {
            var level = _services.Chat.PermissionLevel;
            PermView.IsChecked = level == ToolPermissionLevel.ViewOnly;
            PermWorkspace.IsChecked = level == ToolPermissionLevel.WorkspaceWrite;
            PermFull.IsChecked = level == ToolPermissionLevel.FullAccess;

            var api = _services.Chat.Api;
            PanelModelName.Text = (api != null && !string.IsNullOrEmpty(api.Model)) ? api.Model : "未选择模型";

            var effort = _services.Chat.Profile != null ? _services.Chat.Profile.Api.ReasoningLevel : "auto";
            if (string.IsNullOrEmpty(effort)) effort = "auto";
            foreach (ComboBoxItem item in EffortBox.Items)
            {
                if (string.Equals((string)item.Tag, effort, StringComparison.OrdinalIgnoreCase))
                {
                    EffortBox.SelectedItem = item;
                    break;
                }
            }
        }

        private void ComposerPerm_Checked(object sender, RoutedEventArgs e)
        {
            var radio = sender as RadioButton;
            if (radio == null || radio.IsChecked != true) return;
            int level;
            if (!int.TryParse((string)radio.Tag, out level)) return;
            _services.Chat.PermissionLevel = (ToolPermissionLevel)level;
        }

        private async void EffortBox_Changed(object sender, SelectionChangedEventArgs e)
        {
            var item = EffortBox.SelectedItem as ComboBoxItem;
            var profile = _services.Chat.Profile;
            if (item == null || profile == null) return;
            var value = (string)item.Tag;
            if (string.Equals(profile.Api.ReasoningLevel, value, StringComparison.Ordinal)) return;
            profile.Api.ReasoningLevel = value;
            await _services.Workspace.SaveProfileAsync(profile);
        }

        private void PanelModel_Click(object sender, RoutedEventArgs e)
        {
            ComposerFlyout.Hide();
            OpenDetails<ModelPickerPage>(null, "conversation");
        }

        private void PanelAttach_Click(object sender, RoutedEventArgs e)
        {
            ComposerFlyout.Hide();
            OpenDetails<FilesPage>(null, "attachments");
        }
        private async Task RenderAsync()
        {
            BindTimelineScroll();
            int generation = ++_renderGeneration;
            double position = _timelineScroll != null && !_followTail ? _timelineScroll.VerticalOffset : -1;
            _suppressScrollObservation = true;
            var liveText = _sending ? _streamingText.ToString() : "";
            _streamDirty = false;
            _rows.Clear(); _streamingRow = null; _streamingText.Clear(); _shownLength = 0;
            var conversation = _services.Chat.Conversation;
            if (conversation == null) { _suppressScrollObservation = false; return; }
            UpdateChatStatus();
            var projected = TimelineProjector.Project(conversation, _sending, _liveSteps);
            if (_historyConversationId != conversation.Id) { _historyConversationId = conversation.Id; _historyLimit = _services.Workspace.Settings.HistoryPageSize; }
            int hidden = Math.Max(0, projected.Count - _historyLimit);
            LoadHistoryButton.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadHistoryButton.Content = "加载更早的消息 · " + hidden + " 条";
            TimelineList.Padding = new Thickness(12, hidden > 0 ? 44 : 10, 12, 4);
            foreach (var row in projected.Skip(hidden))
            {
                if (row.Source != null && row.Source.MessageId == _translationOverlay) {
                    var message = conversation.Messages.FirstOrDefault(x => x.Id == _translationOverlay);
                    var translated = message != null ? message.GetTranslation(_translationLanguage) : null;
                    if (!string.IsNullOrEmpty(translated)) row.Text += "\n\n---\n\n**译文 · " + _translationLanguage + "**\n\n" + translated;
                }
                _rows.Add(new RowVm(row));
            }
            if (!string.IsNullOrEmpty(liveText)) {
                _streamingText.Append(liveText);
                // 重建时间线时已经显示过的部分直接给全，打字机只负责之后新到的增量
                _shownLength = _streamingText.Length;
                _streamingRow = new RowVm(new TimelineRow { Kind = RowKind.Output, Key = "stream", Text = liveText });
                _rows.Add(_streamingRow);
            }
            UpdateBranchBar();
            try {
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, delegate {
                    if (generation != _renderGeneration) return;
                    TimelineList.UpdateLayout();
                    BindTimelineScroll();
                    if (!_loadingHistory) {
                        if (position >= 0 && _timelineScroll != null) _timelineScroll.ChangeView(null, position, null, true);
                        else ScrollToEnd();
                    }
                    ReturnToBottomButton.Visibility = _followTail ? Visibility.Collapsed : Visibility.Visible;
                });
            } finally {
                if (generation == _renderGeneration) _suppressScrollObservation = false;
            }
        }

        private void AppendDelta(string content)
        {
            if (string.IsNullOrEmpty(content)) return;
            _streamingText.Append(content);
            _streamDirty = true;
            if (_streamTimer == null)
            {
                _streamTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TypewriterTickMs) };
                _streamTimer.Tick += delegate { FlushStreaming(); };
            }
            // 运行中的计时器不要重复 Start：增量来得比节拍快时会一直重置间隔，导致迟迟不刷新
            if (!_streamTimer.IsEnabled) _streamTimer.Start();
        }

        private void ScrollToEnd()
        {
            if (_rows.Count == 0 || !_followTail) return;
            try
            {
                TimelineList.ScrollIntoView(_rows[_rows.Count - 1]);
            }
            catch (Exception)
            {
                // 列表还没布局完时会抛，忽略
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // 发送 / 停止
        // ═════════════════════════════════════════════════════════════════
        private void Composer_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;

            var behavior = _services.Workspace.Settings.EnterBehavior;
            var shift = Window.Current.CoreWindow.GetKeyState(Windows.System.VirtualKey.Shift);

            bool shiftDown = (shift & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            bool shouldSend = string.Equals(behavior, "send", StringComparison.OrdinalIgnoreCase) ? !shiftDown : shiftDown;

            if (shouldSend)
            {
                e.Handled = true;
                Send_Click(sender, null);
            }
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            UpdateChatStatus(); if (_offline) return;
            var text = ComposerBox.Text;
            if (string.IsNullOrWhiteSpace(text) && _pendingAttachments.Count == 0) return;
            var attachments = new List<Attachment>(_pendingAttachments);
            _pendingAttachments.Clear(); RefreshAttachmentHint(); ComposerBox.Text = "";
            if (_sending)
            {
                _queuedTurns.Enqueue(new PendingTurn { Text = text, Attachments = attachments });
                RunText.Text = "正在生成 · 待追加 " + _queuedTurns.Count + " 条";
                return;
            }
            await RunTurnAsync(text, attachments);
        }

        private async Task RunTurnAsync(string text, IList<Attachment> attachments)
        {
            UpdateChatStatus();
            if (_offline) {
                ComposerBox.Text = text + (string.IsNullOrEmpty(ComposerBox.Text) ? "" : "\n" + ComposerBox.Text);
                foreach (var attachment in attachments) _pendingAttachments.Add(attachment);
                RefreshAttachmentHint(); return;
            }
            await CancelFollowUpsAsync();
            _sending = true;
            _suggestions = new List<string>();
            UpdateSuggestionBar();
            SetRunning(true, "正在生成");

            _sendCts = new CancellationTokenSource();
            var observer = BuildObserver();

            try
            {
                var result = await _services.Chat.SendAsync(text, attachments, observer, _sendCts.Token);

                if (result.Outcome == AgentOutcome.Failed && !string.IsNullOrEmpty(result.ErrorMessage))
                {
                    // 错误已经作为一条失败消息落进会话（时间线上是红色错误行）
                    SetRunning(false, "");
                    await ShowSimpleMessageAsync(result.ErrorMessage);
                }
            }
            catch (OperationCanceledException)
            {
                SetRunning(false, "");
            }
            catch (Exception ex)
            {
                App.WriteCrashLog("SEND", ex);
                await ShowSimpleMessageAsync("发送失败：" + ex.Message);
            }
            finally
            {
                if (_streamTimer != null) _streamTimer.Stop();
                _liveSteps.Clear();
                _sending = false;
                var completedCts = _sendCts;
                _sendCts = null;
                if (completedCts != null) completedCts.Dispose();
                AttachButton.IsEnabled = !_services.Chat.IsDraft;
                SetRunning(false, "");
                await RenderAsync();
                await RefreshConversationsAsync();
                if (_queuedTurns.Count > 0) { var queued = _queuedTurns.Dequeue(); await RunTurnAsync(queued.Text, queued.Attachments); }
                else StartFollowUps();
            }
        }
        private AgentObserver BuildObserver()
        {
            return new AgentObserver
            {
                StateChanged = delegate (RunState state, string detail)
                {
                    RunOnUi(delegate
                    {
                        var running = state == RunState.Thinking
                                   || state == RunState.ToolRunning
                                   || state == RunState.WaitingApproval
                                   || state == RunState.Stopping;
                        SetRunning(running, string.IsNullOrEmpty(detail) ? "正在生成" : detail);
                    });
                },

                MessageAppended = delegate (Message message)
                {
                    RunOnUi(delegate
                    {
                        if (message.Role == MessageRole.Assistant) { _streamingText.Clear(); _streamingRow = null; _shownLength = 0; }
                        // 定稿了就重建时间线（流式的临时行会被真实行取代）
                        var ignored = RenderAsync();
                    });
                },

                StepStarted = delegate (StepRecord step)
                {
                    RunOnUi(delegate
                    {
                        _liveSteps.Add(step);
                        var ignored = RenderAsync();
                    });
                },

                StepUpdated = delegate (StepRecord step)
                {
                    RunOnUi(delegate
                    {
                        // 必须用定向通知：过程行模板带嵌套 ItemsControl，
                        // PropertyChanged(null) 会让绑定引擎抛 NRE，异常直接穿透到这里，
                        // 于是"工具跑完了但界面上一直不更新"。
                        foreach (var row in _rows) if (row.Kind == RowKind.Process) row.RaiseProcessChanged();
                    });
                },

                AssistantDelta = delegate (string content, string reasoning)
                {
                    RunOnUi(delegate { AppendDelta(content); });
                },

                Notice = delegate (string notice)
                {
                    RunOnUi(delegate { SetRunning(false, notice); });
                }
            };
        }

        private void SetRunning(bool running, string detail)
        {
            RunBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            RunRing.IsActive = running;
            if (!string.IsNullOrEmpty(detail)) RunText.Text = detail;

            StopButton.IsEnabled = running && _sendCts != null && !_sendCts.IsCancellationRequested;
            SendButton.IsEnabled = !_offline;
            SendButton.Content = running ? "\uE710" : "\uE724";

            ThemeHelper.ApplyStatusBar(running);
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_sendCts != null && !_sendCts.IsCancellationRequested)
                {
                    _queuedTurns.Clear();
                _sendCts.Cancel();
                    RunText.Text = "正在停止…";
                }
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        private void Attach_Click(object sender, RoutedEventArgs e) { OpenDetails<FilesPage>(null, "attachments"); }

        // ═════════════════════════════════════════════════════════════════
        // 审批桥
        // ═════════════════════════════════════════════════════════════════
        private void WireInteraction() { WireFullScreenInteraction(); }

        private void NoticeAction_Click(object sender, RoutedEventArgs e) { InteractionFrame.Visibility = Visibility.Visible; }

        // ═════════════════════════════════════════════════════════════════
        // 返回键
        // ═════════════════════════════════════════════════════════════════
        public bool OnBackRequested()
        {
            if (InteractionFrame.Visibility == Visibility.Visible)
            {
                var page = InteractionFrame.Content as DetailPage;
                if (page != null) page.Back();
                else { InteractionFrame.Visibility = Visibility.Collapsed; NoticeBanner.Visibility = Visibility.Collapsed; }
                return true;
            }
            if (DetailFrame.Visibility == Visibility.Visible) { var page = DetailFrame.Content as DetailPage; if (page != null) page.Back(); else CloseDetails(); return true; }
            if (ChatView.Visibility == Visibility.Visible)
            {
                CloseChat();
                return true;
            }

            if (_searchActive)
            {
                _searchActive = false;
                SearchBox.Visibility = Visibility.Collapsed;
                SearchBox.Text = "";
                var ignored = RefreshConversationsAsync();
                return true;
            }

            return false;
        }

        // ═════════════════════════════════════════════════════════════════
        // 小工具
        // ═════════════════════════════════════════════════════════════════
        private void RunOnUi(Action action)
        {
            if (action == null) return;
            if (Dispatcher == null || Dispatcher.HasThreadAccess)
            {
                action();
                return;
            }
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate { action(); });
        }

        /// <summary>
        /// StackPanel.Spacing 是 UniversalApiContract 5.0 才有的属性（16299+），
        /// 而我们的最低版本声明是 14393。硬写会在 1607 设备上抛 MissingMethodException，
        /// 所以按运行时的能力探测再设。
        /// </summary>
        private static bool SupportsPanelSpacing
        {
            get
            {
                try
                {
                    return Windows.Foundation.Metadata.ApiInformation
                        .IsPropertyPresent("Windows.UI.Xaml.Controls.StackPanel", "Spacing");
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        private static StackPanel NewStack(double spacing)
        {
            var panel = new StackPanel();
            if (SupportsPanelSpacing) panel.Spacing = spacing;
            return panel;
        }

        private void ShowSimpleMessage(string text)
        {
            var ignored = ShowSimpleMessageAsync(text);
        }

        private async Task ShowSimpleMessageAsync(string text)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "提示",
                    Content = new TextBlock { Text = text ?? "", TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "好"
                };
                await dialog.ShowAsync();
            }
            catch (Exception)
            {
                // 已有对话框打开时会抛，忽略
            }
        }
    }
}
