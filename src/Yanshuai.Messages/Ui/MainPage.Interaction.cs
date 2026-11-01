using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Yanshuai.Messages.Core;
using Yanshuai.Messages.Ui.Pages;

namespace Yanshuai.Messages.Ui
{
    public sealed partial class MainPage
    {
        private sealed class PendingTurn { public string Text; public IList<Attachment> Attachments; }
        private readonly Queue<PendingTurn> _queuedTurns = new Queue<PendingTurn>();

        private void WireFullScreenInteraction()
        {
            _services.Interaction.PermissionHandler = async delegate (ToolPermissionRequest permission, CancellationToken ct)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var request = new InteractionRequest {
                    Permission = permission,
                    PermissionAnswer = delegate (bool allowed) { completion.TrySetResult(allowed); return Task.CompletedTask; }
                };
                using (ct.Register(delegate { completion.TrySetCanceled(); })) {
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate { PresentRequest(request, permission.Title, permission.Detail); });
                    try { return await completion.Task; }
                    finally { await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, CloseRequest); }
                }
            };
            _services.Interaction.AskHandler = async delegate (IReadOnlyList<AskQuestion> questions, CancellationToken ct)
            {
                var completion = new TaskCompletionSource<IReadOnlyList<AskAnswer>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var request = new InteractionRequest {
                    Questions = questions,
                    AskAnswer = delegate (IReadOnlyList<AskAnswer> answers) { completion.TrySetResult(answers); return Task.CompletedTask; }
                };
                using (ct.Register(delegate { completion.TrySetCanceled(); })) {
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate { PresentRequest(request, "向你追问", questions.Count + " 个问题"); });
                    try { return await completion.Task; }
                    finally { await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, CloseRequest); }
                }
            };
        }

        private void PresentRequest(InteractionRequest request, string title, string detail)
        {
            // detail 可能为 null（例如 run_python 的审批请求只设了 Title）。
            // TextBlock.Text 赋 null 在 UWP 上会抛 ArgumentNullException(Null_HString)，
            // 真机上表现为"审批界面出错"，在 UI 线程上直接崩（闪退）。
            NoticeTitle.Text = title ?? "等待你处理";
            NoticeDetail.Text = detail ?? "";
            NoticeBanner.Visibility = Visibility.Visible;
            var context = new PageContext { Services = _services, Profile = _services.Chat.Profile,
                Conversation = _services.Chat.Conversation, Item = request, Close = CloseRequest };
            InteractionFrame.Content = null;
            InteractionFrame.BackStack.Clear(); InteractionFrame.ForwardStack.Clear();
            InteractionFrame.Navigate(typeof(InteractionPage), context);
            InteractionFrame.BackStack.Clear();
            InteractionFrame.Visibility = Visibility.Visible;
        }
        private void CloseRequest()
        {
            NoticeBanner.Visibility = Visibility.Collapsed;
            InteractionFrame.Visibility = Visibility.Collapsed;
            InteractionFrame.Content = null; InteractionFrame.BackStack.Clear(); InteractionFrame.ForwardStack.Clear();
        }
    }
}
