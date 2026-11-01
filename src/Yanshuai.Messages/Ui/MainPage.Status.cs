using System;
using System.Linq;
using Windows.Networking.Connectivity;
using Windows.UI.Xaml;
using Yanshuai.Messages.Core;
using Yanshuai.Messages.Ui.Pages;

namespace Yanshuai.Messages.Ui
{
    public sealed partial class MainPage
    {
        private bool _offline;
        private bool _networkSubscribed;
        private void ChatStatus_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_networkSubscribed) { NetworkInformation.NetworkStatusChanged += NetworkChanged; _networkSubscribed = true; }
            UpdateChatStatus();
        }
        private void ChatStatus_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_networkSubscribed) { NetworkInformation.NetworkStatusChanged -= NetworkChanged; _networkSubscribed = false; }
        }
        private void NetworkChanged(object sender) { RunOnUi(UpdateChatStatus); }
        private void ContextWarning_Click(object sender, RoutedEventArgs e) { OpenDetails<ContextPage>(); }
        private void ChatModel_Click(object sender, RoutedEventArgs e) { OpenDetails<ModelPickerPage>(null, "conversation"); }

        private void UpdateChatStatus()
        {
            if (_services == null) return;
            var chat = _services.Chat;
            var profile = chat.Profile;
            var conversation = chat.Conversation;
            var api = ModelSelection.Resolve(_services.Workspace.ApiProfiles, profile, conversation);
            var model = api != null ? api.Model : "";
            try {
                Uri endpoint;
                bool local = api != null && Uri.TryCreate(api.Url, UriKind.Absolute, out endpoint) && endpoint.IsLoopback;
                // LocalAccess permits LAN model services; InternetAccess is not required.
                bool connected = NetworkInformation.GetConnectionProfiles().Any(x => x.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None);
                _offline = !local && !connected;
            } catch (Exception ex) { App.WriteCrashLog("NETWORK_STATE", ex); _offline = false; }
            OfflineHint.Visibility = _offline ? Visibility.Visible : Visibility.Collapsed;
            SendButton.IsEnabled = !_offline;
            ChatSubtitle.Text = _offline ? "离线 · 没有网络连接" : string.IsNullOrEmpty(model) ? "点击选择 Provider 与模型" : api.Name + " · " + model + " ▾";
            ContextWarning.Visibility = Visibility.Collapsed;
            if (conversation == null || api == null) return;
            int limit = ModelContextLimits.GetEffectiveLimit(model, api.MaxContextTokens);
            int used = chat.EstimateContextTokens();
            if (used >= limit * Limits.CompactThreshold) {
                ContextWarningText.Text = "上下文约用 " + Math.Min(999, (int)(100.0 * used / limit)) + "%（" + used + " / " + limit + "）";
                ContextWarning.Visibility = Visibility.Visible;
            }
        }
    }
}
