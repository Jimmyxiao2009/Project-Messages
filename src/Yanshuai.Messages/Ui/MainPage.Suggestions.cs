using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Yanshuai.Messages.Core;
using Yanshuai.Messages.Ui.Pages;

namespace Yanshuai.Messages.Ui
{
    // 回复建议条（副手模型生成，点一下填进输入框）与收进"⋯"菜单的快捷消息。
    public sealed partial class MainPage
    {
        private CancellationTokenSource _followUpCts;
        private Task _followUpTask = Task.CompletedTask;
        private bool _suggestionsLoading;
        private List<string> _suggestions = new List<string>();

        // ───────────── 回合后任务 ─────────────

        /// <summary>一轮结束后起标题、生成建议。不阻塞界面，失败只是不显示建议。</summary>
        private void StartFollowUps()
        {
            var settings = _services.Workspace.Settings;
            var conversation = _services.Chat.Conversation;
            if (conversation == null || (!settings.ReplySuggestions && !settings.AiTitles)) return;

            var cts = new CancellationTokenSource();
            _followUpCts = cts;
            if (settings.ReplySuggestions) { _suggestionsLoading = true; _suggestions = new List<string>(); UpdateSuggestionBar(); }
            _followUpTask = RunFollowUpsAsync(cts);
        }

        private async Task RunFollowUpsAsync(CancellationTokenSource cts)
        {
            FollowUpResult result = null;
            try
            {
                result = await _services.Chat.RunFollowUpsAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.WriteCrashLog("FOLLOWUP", ex);
            }

            RunOnUi(delegate
            {
                if (_followUpCts == cts) { _followUpCts = null; _suggestionsLoading = false; }
                var current = _services.Chat.Conversation;
                if (result != null && current != null && current.Id == result.ConversationId)
                {
                    if (!string.IsNullOrEmpty(result.Title)) ChatTitle.Text = result.Title;
                    _suggestions = current.CurrentSuggestions();
                }
                UpdateSuggestionBar();
                if (result != null && !string.IsNullOrEmpty(result.Title)) { var ignored = RefreshConversationsAsync(); }
            });
            cts.Dispose();
        }

        /// <summary>发下一条之前先收掉回合后任务：两边都会保存同一个会话文件，不能并发。</summary>
        private async Task CancelFollowUpsAsync()
        {
            var cts = _followUpCts;
            _followUpCts = null;
            _suggestionsLoading = false;
            if (cts != null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
            try { await _followUpTask; } catch (Exception) { }
        }

        /// <summary>打开会话或从子页回来时，显示已保存的建议（不会自动重新生成）。</summary>
        private void RestoreSuggestions()
        {
            var conversation = _services.Chat.Conversation;
            if (_followUpCts == null) _suggestionsLoading = false;
            if (!_suggestionsLoading) _suggestions = conversation != null ? conversation.CurrentSuggestions() : new List<string>();
            UpdateSuggestionBar();
        }

        private async void SuggestionRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (_sending) return;
            await CancelFollowUpsAsync();
            var conversation = _services.Chat.Conversation;
            if (conversation != null) { conversation.Suggestions = new List<string>(); conversation.SuggestionsFor = ""; }
            StartFollowUps();
        }

        private void Composer_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSuggestionBar();
        }

        // ───────────── 建议条 ─────────────

        private void UpdateSuggestionBar()
        {
            bool enabled = _services != null && _services.Workspace.Settings.ReplySuggestions;
            bool show = enabled && !_sending && string.IsNullOrEmpty(ComposerBox.Text) &&
                        (_suggestionsLoading || _suggestions.Count > 0);
            SuggestionBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;

            SuggestionPanel.Children.Clear();
            SuggestionRefreshButton.Visibility = _suggestionsLoading ? Visibility.Collapsed : Visibility.Visible;
            if (_suggestionsLoading)
            {
                var loading = new StackPanel { Orientation = Orientation.Horizontal, Height = 30 };
                loading.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16, Foreground = FindBrush("AccentTextBrush"),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
                loading.Children.Add(new TextBlock { Text = "正在想怎么回…", FontSize = 13, Foreground = FindBrush("MidBrush"),
                    VerticalAlignment = VerticalAlignment.Center });
                SuggestionPanel.Children.Add(loading);
                return;
            }

            foreach (var suggestion in _suggestions)
            {
                var text = suggestion;
                var chip = new Button { Content = text, Style = FindStyle("QuickChipButton") };
                AutomationProperties.SetName(chip, "填入：" + text);
                chip.Click += delegate
                {
                    // 建议是完整的一句回复：填进去由用户确认或改几个字再发
                    ComposerBox.Text = text;
                    ComposerBox.SelectionStart = text.Length;
                    ComposerBox.Focus(FocusState.Programmatic);
                };
                SuggestionPanel.Children.Add(chip);
            }
        }

        // ───────────── 快捷消息（"⋯"菜单里的子菜单） ─────────────

        private async Task EnsureQuickMessagesAsync()
        {
            try
            {
                var library = _services.Workspace.Library;
                if (library.QuickMessages == null || library.QuickMessages.Count == 0)
                {
                    library.QuickMessages = QuickMessages.CreateDefaults();
                    await _services.Workspace.SaveLibraryAsync();
                }
            }
            catch (Exception ex)
            {
                App.WriteCrashLog("QUICKMSG", ex);
            }
        }

        private MenuFlyoutSubItem BuildQuickMessagesMenu()
        {
            var sub = new MenuFlyoutSubItem { Text = "快捷消息" };
            var library = _services.Workspace.Library;
            var visible = library.QuickMessages != null ? QuickMessages.Visible(library.QuickMessages) : new List<QuickMessage>();
            foreach (var item in visible)
            {
                var captured = item;
                var entry = new MenuFlyoutItem { Text = item.Title };
                entry.Click += delegate { FillComposer(captured); };
                sub.Items.Add(entry);
            }
            if (visible.Count > 0) sub.Items.Add(new MenuFlyoutSeparator());
            var manage = new MenuFlyoutItem { Text = "管理快捷消息…" };
            manage.Click += delegate { OpenDetails<ContentRulesPage>(); };
            sub.Items.Add(manage);
            return sub;
        }
    }
}
