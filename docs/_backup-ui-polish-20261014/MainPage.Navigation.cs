using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Yanshuai.Messages.Core;
using Yanshuai.Messages.Ui.Pages;

namespace Yanshuai.Messages.Ui
{
    public sealed partial class MainPage
    {
        private string _translationOverlay = "";
        private string _translationLanguage = "";

        private void ApplyChatAppearance()
        {
            var chat = _services.Chat;
            var appearance = chat.Conversation != null && chat.Conversation.AppearanceOverride != null
                ? chat.Conversation.AppearanceOverride : chat.Profile != null ? chat.Profile.Appearance : _services.Workspace.Settings.DefaultAppearance;
            ChatView.Background = FindBrush("BgBrush");
            ChatBackgroundImage.SourcePath = "";
            ChatBackgroundImage.Opacity = Math.Max(0, Math.Min(100, appearance.BackgroundOpacity)) / 100.0;
            if (appearance.BackgroundType == "solid") {
                var color = ThemeHelper.ParseHex(appearance.BackgroundValue);
                if (color.A > 0) { color.A = (byte)(255 * ChatBackgroundImage.Opacity); ChatView.Background = new SolidColorBrush(color); }
            } else if (appearance.BackgroundType == "image" && !string.IsNullOrWhiteSpace(appearance.BackgroundValue)) {
                ChatBackgroundImage.SourcePath = appearance.BackgroundValue;
            }
        }

        private readonly List<Attachment> _pendingAttachments = new List<Attachment>();
        private readonly List<StepRecord> _liveSteps = new List<StepRecord>();
        private PageContext _detailContext;
        private DispatcherTimer _streamTimer;
        private bool _streamDirty;

        private bool _startupApplied;
        private async Task RestoreStartupAsync()
        {
            if (_startupApplied) return; _startupApplied = true;
            var settings = _services.Workspace.Settings;
            var crash = await Windows.Storage.ApplicationData.Current.LocalFolder.TryGetItemAsync("crash.log") as Windows.Storage.StorageFile;
            if (crash != null) {
                var properties = await crash.GetBasicPropertiesAsync();
                long timestamp = properties.DateModified.UtcDateTime.Ticks;
                if (timestamp > settings.LastSeenCrash) {
                    settings.LastSeenCrash = timestamp; await _services.Workspace.SaveSettingsAsync();
                    var choice = await new ContentDialog { Title = "上次异常退出", Content = "已保留本地崩溃记录，可查看后继续使用。",
                        PrimaryButtonText = "查看记录", CloseButtonText = "继续" }.ShowAsync();
                    if (choice == ContentDialogResult.Primary) { OpenDetails<DiagnosticsPage>(null, "crash"); return; }
                }
            }
            if (!settings.OnboardingComplete && _services.Workspace.Profiles.Count == 0 && _services.Workspace.ApiProfiles.Items.Count == 0) {
                OpenDetails<OnboardingPage>(); return;
            }
            if (settings.StartupBehavior == "new") { OpenDetails<NewConversationPage>(); return; }
            if (settings.StartupBehavior == "last") {
                var p = _services.Workspace.FindProfile(settings.LastProfileId);
                if (p != null) {
                    var c = await _services.Workspace.LoadConversationAsync(p.Slug, settings.LastConversationId);
                    if (c != null) await OpenChatFromPageAsync(p, c);
                }
            }
        }

        private string _profileFilter = "";
        private int _listGeneration;
        private void RefreshProfileFilters()
        {
            if (!string.IsNullOrEmpty(_profileFilter) && !_services.Workspace.Profiles.Any(x => x.Id == _profileFilter)) _profileFilter = "";
            ProfileFilterPanel.Children.Clear();
            var all = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("", "全部") };
            foreach (var profile in _services.Workspace.Profiles) all.Add(new KeyValuePair<string, string>(profile.Id, profile.Name));
            foreach (var entry in all) {
                var id = entry.Key;
                var text = new TextBlock { Text = entry.Value, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 90, FontSize = 12 };
                var button = new Button { Content = text, MinHeight = 44, Margin = new Thickness(0, 0, 8, 0),
                    Background = FindBrush(_profileFilter == id ? "AccentSoftBrush" : "BgBrush"),
                    Foreground = FindBrush(_profileFilter == id ? "AccentTextBrush" : "MidBrush") };
                button.Click += async delegate { _profileFilter = id; RefreshProfileFilters(); await RefreshConversationsAsync(); };
                ProfileFilterPanel.Children.Add(button);
            }
        }

        private void BindAttachmentNavigation()
        {
            AttachmentStrip.OpenAttachment = delegate (Attachment attachment) {
                var path = attachment.Path;
                if (!path.StartsWith(_services.Chat.ProfileSlug + "/", StringComparison.Ordinal)) path = _services.Chat.ProfileSlug + "/" + path;
                OpenDetails<FilePreviewPage>(null, null, path);
            };
        }

        private void OpenDetails<T>(Profile profile = null, string section = null, object item = null) where T : Page
        {
            _detailContext = new PageContext {
                Services = _services, Profile = profile ?? _services.Chat.Profile,
                Conversation = ChatView.Visibility == Visibility.Visible ? _services.Chat.Conversation : null,
                Section = section, Item = item, Close = CloseDetails,
                IsRunning = delegate { return _sending; },
                OpenChat = OpenChatFromPageAsync,
                SwitchServices = delegate (Platform.AppServices replacement) { _services.Shutdown(); Frame.Navigate(typeof(MainPage), replacement); return Task.CompletedTask; },
                AddAttachment = delegate (Attachment attachment) {
                    if (_pendingAttachments.Count >= Limits.MaxAttachments) throw new InvalidOperationException("附件数量达到上限。");
                    _pendingAttachments.Add(attachment); RefreshAttachmentHint();
                }
            };
            AttachmentStrip.OpenAttachment = delegate (Attachment attachment) { var path = attachment.Path; if (!path.StartsWith(_services.Chat.ProfileSlug + "/", StringComparison.Ordinal)) path = _services.Chat.ProfileSlug + "/" + path; OpenDetails<FilePreviewPage>(null, null, path); };
            DetailFrame.BackStack.Clear();
            DetailFrame.Navigate(typeof(T), _detailContext);
            DetailFrame.Visibility = Visibility.Visible;
        }

        private async void CloseDetails()
        {
            DetailFrame.Visibility = Visibility.Collapsed;
            DetailFrame.Content = null; DetailFrame.BackStack.Clear();
            try {
                ApplyTheme();
                await RefreshProfilesAsync(); await RefreshConversationsAsync(); RefreshMePanel(); UpdateHomeSubtitle();
                if (ChatView.Visibility == Visibility.Visible && !_sending) {
                    var active = _services.Chat.Conversation;
                    var p = active != null ? _services.Workspace.FindProfile(active.ProfileId) : null;
                    if (p == null) CloseChat();
                    else if (_services.Chat.IsDraft) { ShowChat(); await RenderAsync(); }
                    else if (!await _services.Files.ExistsAsync(WorkspaceLayout.ConversationFile(p.Slug, active.Id))) CloseChat();
                    else {
                        await _services.OpenProfileAsync(p);
                        await _services.Chat.OpenConversationAsync(await _services.Workspace.LoadConversationAsync(p.Slug, active.Id));
                        ShowChat(); await RenderAsync();
                    }
                }
                await RefreshQuickBarAsync();
            } catch (Exception ex) { App.WriteCrashLog("DETAIL_CLOSE", ex); await ShowSimpleMessageAsync("刷新失败：" + ex.Message); }
        }

        private async Task OpenChatFromPageAsync(Profile profile, Conversation conversation)
        {
            if (_sending) throw new InvalidOperationException("请先等当前会话完成。");
            if (profile == null || conversation == null) throw new InvalidOperationException("会话已不存在。");
            DetailFrame.Visibility = Visibility.Collapsed; DetailFrame.Content = null; DetailFrame.BackStack.Clear();
            await _services.OpenProfileAsync(profile); await _services.Chat.OpenConversationAsync(conversation);
            ShowChat(); await RenderAsync();
            _services.Workspace.Settings.LastProfileId = profile.Id;
            _services.Workspace.Settings.LastConversationId = conversation.Id;
            await _services.Workspace.SaveSettingsAsync();
        }

        private void PageLink_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            string route = element != null ? element.Tag as string : "";
            switch (route) {
                case "general": OpenDetails<SettingsPage>(); break;
                case "appearance": OpenDetails<AppearancePage>(null, "theme"); break;
                case "typography": OpenDetails<SettingsPage>(null, "typography"); break;
                case "user-personas": OpenDetails<ResourcesPage>(null, "user-personas"); break;
                case "lorebooks": OpenDetails<ResourcesPage>(null, "lorebooks"); break;
                case "injections": OpenDetails<ResourcesPage>(null, "injections"); break;
                case "tools": OpenDetails<ToolsPage>(); break;
                case "mcp": OpenDetails<ExtensionsPage>(null, "mcp"); break;
                case "skills": OpenDetails<ExtensionsPage>(null, "skills"); break;
                case "regex": OpenDetails<ContentRulesPage>(null, "regex"); break;
                case "trash": OpenDetails<TrashPage>(); break;
                case "data": OpenDetails<DataPage>(); break;
                case "workspace": OpenDetails<SettingsPage>(null, "workspace"); break;
                case "folders": OpenDetails<FolderAccessPage>(); break;
                case "archived": OpenDetails<ConversationsPage>(null, "archived"); break;
                case "manage": OpenDetails<ConversationsPage>(); break;
                case "model-picker": OpenDetails<ModelPickerPage>(null, "global"); break;
                case "memory": OpenDetails<MemoryPage>(null, "global"); break;
            }
        }

        private void SearchToggle_Click(object sender, RoutedEventArgs e) { OpenDetails<GlobalSearchPage>(); }

        private void RefreshAttachmentHint()
        {
            AttachmentHint.Text = "附件：" + string.Join(" · ", _pendingAttachments.Select(x => x.Name));
            AttachmentHint.Visibility = _pendingAttachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var chips = new List<Button>();
            foreach (var attachment in _pendingAttachments) { var item = attachment; var button = new Button { Content = item.Name + " ×", MinHeight = 44, MaxWidth = 180, Margin = new Thickness(0, 0, 6, 0) }; button.Click += delegate { _pendingAttachments.Remove(item); RefreshAttachmentHint(); }; chips.Add(button); }
            PendingAttachmentList.ItemsSource = chips;
        }

        private void ChatMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_services.Chat.IsDraft) { ShowSimpleMessage("发送第一条消息后可以修改会话设置。"); return; }
            var menu = new MenuFlyout();
            AddMenu(menu, "切换 Provider 与模型", delegate { OpenDetails<ModelPickerPage>(null, "conversation"); });
            AddMenu(menu, "会话设置", delegate { OpenDetails<ChatSettingsPage>(); });
            AddMenu(menu, "群聊成员", delegate { OpenDetails<GroupPage>(); });
            AddMenu(menu, "压缩上下文", delegate { OpenDetails<ContextPage>(); });
            AddMenu(menu, "朗读最后回复", async delegate { await SpeakLastAsync(); });
            AddMenu(menu, "翻译最后回复", async delegate { await TranslateLastAsync(); });
            AddMenu(menu, "用量", delegate { OpenDetails<DiagnosticsPage>(null, "usage"); });
            AddMenu(menu, "文件与附件", delegate { OpenDetails<FilesPage>(null, "attachments"); });
            menu.ShowAt((FrameworkElement)sender);
        }

        private static void AddMenu(MenuFlyout menu, string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text }; item.Click += delegate { action(); }; menu.Items.Add(item);
        }

        private void TimelineList_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;
            DependencyObject element = e.OriginalSource as DependencyObject; RowVm row = null;
            while (element != null) {
                var framework = element as FrameworkElement;
                row = framework != null ? framework.DataContext as RowVm : null;
                if (row != null) break;
                element = VisualTreeHelper.GetParent(element);
            }
            if (row == null || row.Row.Source == null) return;
            var message = _services.Chat.Conversation.Messages.FirstOrDefault(x => x.Id == row.Row.Source.MessageId);
            if (message == null) return;
            var menu = new MenuFlyout();
            AddMenu(menu, "复制", delegate {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(message.Content);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            });
            AddMenu(menu, "编辑为新候选", delegate { OpenDetails<MessageEditPage>(null, null, message); });
            AddMenu(menu, "从这里创建分支", delegate { OpenDetails<MessageEditPage>(null, "fork", message); });
            AddMenu(menu, "删除", async delegate {
                if (_sending) { await ShowSimpleMessageAsync("正在生成，请先停止。"); return; }
                var result = await new ContentDialog { Title = "删除消息？", Content = "原候选仍会保留，当前消息将从活跃历史中移除。",
                    PrimaryButtonText = "删除", CloseButtonText = "取消" }.ShowAsync();
                if (result != ContentDialogResult.Primary) return;
                _services.Chat.Conversation.Messages.Remove(message);
                _services.Chat.Conversation.Summary = ""; _services.Chat.Conversation.SummarizedUpTo = -1;
                await _services.Workspace.SaveConversationAsync(_services.Chat.ProfileSlug, _services.Chat.Conversation);
                await RenderAsync();
            });
            menu.ShowAt(TimelineList, e.GetPosition(TimelineList)); e.Handled = true;
        }

        private ScrollViewer _timelineScroll;
        private bool _followTail = true;
        private bool _suppressScrollObservation;
        private int _renderGeneration;
        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
                var child = VisualTreeHelper.GetChild(parent, i);
                var match = child as T; if (match != null) return match;
                match = FindChild<T>(child); if (match != null) return match;
            }
            return null;
        }
        private void BindTimelineScroll()
        {
            if (_timelineScroll != null) return;
            TimelineList.ApplyTemplate();
            _timelineScroll = FindChild<ScrollViewer>(TimelineList);
            if (_timelineScroll == null) return;
            _timelineScroll.ViewChanged += delegate {
                if (_suppressScrollObservation) return;
                _followTail = _timelineScroll.ScrollableHeight - _timelineScroll.VerticalOffset < 80;
                ReturnToBottomButton.Visibility = _followTail ? Visibility.Collapsed : Visibility.Visible;
            };
        }
        private void ReturnToBottom_Click(object sender, RoutedEventArgs e)
        {
            _followTail = true; ReturnToBottomButton.Visibility = Visibility.Collapsed; ScrollToEnd();
        }
        private int _historyLimit;
        private string _historyConversationId = "";
        private bool _loadingHistory;
        private async void LoadHistory_Click(object sender, RoutedEventArgs e)
        {
            var anchor = _rows.FirstOrDefault(x => x.Row.Source != null);
            _historyLimit += _services.Workspace.Settings.HistoryPageSize;
            _followTail = false;
            _loadingHistory = true;
            try {
                await RenderAsync();
                if (anchor != null) {
                    var row = _rows.FirstOrDefault(x => x.Row.Key == anchor.Row.Key);
                    if (row != null) TimelineList.ScrollIntoView(row);
                }
            } catch (Exception ex) { App.WriteCrashLog("HISTORY", ex); }
            finally { _loadingHistory = false; }
        }

        private void ProcessSummary_Click(object sender, RoutedEventArgs e)
        {
            var framework = sender as FrameworkElement;
            var row = framework != null ? framework.DataContext as RowVm : null;
            if (row != null) row.ToggleRail();
        }

        private void FlushStreaming()
        {
            if (!_streamDirty) return;
            _streamDirty = false;
            if (_streamingRow == null) {
                _streamingRow = new RowVm(new TimelineRow { Kind = RowKind.Output, Key = "stream", Text = "" });
                _rows.Add(_streamingRow);
            }
            _streamingRow.Row.Text = _streamingText.ToString();
            _streamingRow.RaiseChanged(); ScrollToEnd();
        }
    }
}
