// App.xaml.cs —— 启动与兜底
//
// 启动顺序：建服务（会装载工作区）→ 建 Frame → 进 MainPage。
// 任何一步失败都不留白屏：落一份崩溃日志到 LocalState\crash.log，并显示一个可读的错误面板。

using System;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Yanshuai.Messages.Core;
using Yanshuai.Messages.Platform;
using Yanshuai.Messages.Ui;

namespace Yanshuai.Messages
{
    public sealed partial class App : Application
    {
        private Frame _rootFrame;
        private bool _backButtonHooked;

        public App()
        {
            Platform.StartupTrace.Reset();
            Platform.StartupTrace.Mark("App ctor: begin");

            InitializeComponent();
            Platform.StartupTrace.Mark("App ctor: InitializeComponent 完成");

            Suspending += OnSuspending;

            UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
            {
                Platform.StartupTrace.Fail("UnhandledException", e.Exception);
                WriteCrashLog("XAML", e.Exception);
                e.Handled = true;
            };

            TaskScheduler.UnobservedTaskException += delegate (object sender, UnobservedTaskExceptionEventArgs e)
            {
                Platform.StartupTrace.Fail("UnobservedTaskException", e.Exception);
                WriteCrashLog("TASK", e.Exception);
                e.SetObserved();
            };

            Platform.StartupTrace.Mark("App ctor: 完成");
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Platform.StartupTrace.Mark("OnLaunched");
            var ignored = StartAsync(e);
        }

        private async Task StartAsync(LaunchActivatedEventArgs e)
        {
            try
            {
                if (e != null && _rootFrame != null && _rootFrame.Content != null) { Window.Current.Activate(); return; }
                ShowStartupProgress();
                Platform.StartupTrace.Mark("StartAsync: 开始建 AppServices");
                var services = await AppServices.CreateAsync().ConfigureAwait(true);
                Platform.StartupTrace.Mark("StartAsync: AppServices 就绪");

                _rootFrame = Window.Current.Content as Frame;
                if (_rootFrame == null)
                {
                    _rootFrame = new Frame { CacheSize = 6 };
                    _rootFrame.NavigationFailed += delegate (object s, NavigationFailedEventArgs args)
                    {
                        Platform.StartupTrace.Fail("NavigationFailed", args.Exception);
                        WriteCrashLog("NAV", args.Exception);
                    };
                    Window.Current.Content = _rootFrame;
                }

                if (_rootFrame.Content == null)
                {
                    Platform.StartupTrace.Mark("StartAsync: 导航到 MainPage");
                    _rootFrame.Navigate(typeof(MainPage), services);
                    Platform.StartupTrace.Mark("StartAsync: 导航返回（MainPage 已构造）");
                }

                HookBackButton();
                Platform.StartupTrace.Mark("StartAsync: 返回键已挂");
                ApplyDesktopWindowSize();
                Window.Current.Activate();
                Platform.StartupTrace.Mark("StartAsync: 窗口已激活，启动完成");
            }
            catch (Exception ex)
            {
                Platform.StartupTrace.Fail("StartAsync catch", ex);
                WriteCrashLog("STARTUP", ex);
                ShowStartupError(ex);
            }
        }

        /// <summary>系统返回键：统一交给 Frame 的返回栈。</summary>
        private void HookBackButton()
        {
            if (_backButtonHooked) return;
            _backButtonHooked = true;
            var navigation = SystemNavigationManager.GetForCurrentView();
            navigation.BackRequested += delegate (object sender, BackRequestedEventArgs e)
            {
                if (_rootFrame == null || e.Handled) return;

                // 页面自己先处理（关面板、从会话页退回列表），处理了就不管
                var page = _rootFrame.Content as IBackHandler;
                if (page != null && page.OnBackRequested()) { e.Handled = true; return; }

                if (_rootFrame.CanGoBack)
                {
                    e.Handled = true;
                    _rootFrame.GoBack();
                }
            };

            _rootFrame.Navigated += delegate
            {
                navigation.AppViewBackButtonVisibility = _rootFrame.CanGoBack
                    ? AppViewBackButtonVisibility.Visible
                    : AppViewBackButtonVisibility.Collapsed;
            };
        }

        /// <summary>桌面调试时给一个接近手机比例的窗口（手机忽略这段）。</summary>
        private static void ApplyDesktopWindowSize()
        {
            try
            {
                var view = Windows.UI.ViewManagement.ApplicationView.GetForCurrentView();
                if (view == null) return;

                var bounds = view.VisibleBounds;
                // 只在明显是桌面（宽高都偏大）时才收窄
                if (bounds.Width < 600) return;

                view.SetPreferredMinSize(new Windows.Foundation.Size(360, 640));
                view.TryResizeView(new Windows.Foundation.Size(400, 760));
            }
            catch (Exception)
            {
                // 手机上不支持，忽略
            }
        }

        private void ShowStartupProgress()
        {
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "言枢", FontSize = 34, FontWeight = Windows.UI.Text.FontWeights.Light });
            panel.Children.Add(new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 24, 0, 12) });
            panel.Children.Add(new TextBlock { Text = "正在装载工作区", FontSize = 12 });
            Window.Current.Content = new Grid { Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Black), Children = { panel } };
            Window.Current.Activate();
        }

        private void ShowStartupError(Exception ex)
        {
            var panel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(24),
                MaxWidth = 420
            };

            panel.Children.Add(new TextBlock
            {
                Text = "启动失败",
                FontSize = 26,
                FontWeight = Windows.UI.Text.FontWeights.Light,
                Margin = new Thickness(0, 0, 0, 10)
            });

            panel.Children.Add(new TextBlock
            {
                Text = "应用在启动时遇到问题。日志已写入本地，可在「我的 → 调试日志」查看。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            panel.Children.Add(new TextBlock
            {
                Text = ex.GetType().Name + ": " + ex.Message,
                FontFamily = new Windows.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 260,
                Opacity = 0.7
            });

            var retry = new Button { Content = "重试", MinHeight = 44 };
            retry.Click += async delegate { await StartAsync(null); };
            panel.Children.Add(retry);
            var scroll = new ScrollViewer { Content = panel };
            Window.Current.Content = scroll;
            Window.Current.Activate();
        }

        /// <summary>崩溃日志：完整堆栈，仅本地，不上传。诊断日志另有一份脱敏的。</summary>
        internal static void WriteCrashLog(string tag, Exception ex)
        {
            try
            {
                var builder = new StringBuilder();
                builder.Append(DateTime.UtcNow.ToString("u")).Append(" [").Append(tag).Append("]\n");

                var current = ex;
                int depth = 0;
                while (current != null && depth++ < 6)
                {
                    builder.Append("  ").Append(current.GetType().FullName)
                           .Append(" HResult=0x").Append(current.HResult.ToString("X8")).Append('\n');
                    builder.Append(current.Message).Append('\n');
                    builder.Append(current.StackTrace).Append('\n');
                    current = current.InnerException;
                }

                var path = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "crash.log");
                System.IO.File.AppendAllText(path, builder.ToString() + "\n");
            }
            catch (Exception)
            {
                // 记日志失败不能再抛
            }
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            var ignored = SaveAndCompleteAsync(deferral);
        }

        private static async Task SaveAndCompleteAsync(SuspendingDeferral deferral)
        {
            try
            {
                var services = AppServices.Current;
                if (services != null && services.Chat != null && services.Chat.Conversation != null &&
                    services.Chat.Profile != null && !services.Chat.IsDraft)
                {
                    await services.Workspace.SaveConversationAsync(
                        services.Chat.ProfileSlug, services.Chat.Conversation);
                }
            }
            catch (Exception)
            {
                // 挂起时保存失败只能接受
            }
            finally
            {
                deferral.Complete();
            }
        }
    }

    /// <summary>页面可以实现它来抢在返回键退出前处理（关弹层、退回列表）。</summary>
    public interface IBackHandler
    {
        bool OnBackRequested();
    }
}
