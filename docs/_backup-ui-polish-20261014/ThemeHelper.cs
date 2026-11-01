// Ui/ThemeHelper.cs —— 强调色应用
//
// 三个坑（都在旧项目上踩过）：
//   1) Windows 10 Mobile 上取不到 AccentLight1/2/3 这类系统资源，浅色阶必须自己算；
//   2) 在 App 构造期间读 Application.Resources 会抛 E_UNEXPECTED，
//      所以要等 Window.Activate() 之后再调用，并且整体 try/catch；
//   3) 改 ResourceDictionary 里的画刷是全局生效的，别给页面各改一份。
//
// 这里改的是 ResourceDictionary 里那几个 SolidColorBrush 实例的 Color，
// 页面用 StaticResource 引用画刷对象，因此颜色会跟着变。

using System;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Yanshuai.Messages.Ui
{
    public static class ThemeHelper
    {
        private static bool _light;
        public static void Apply(Yanshuai.Messages.Core.GlobalSettings settings)
        {
            _light = settings.Theme == "light";
            if (settings.Theme == "system") {
                var background = new UISettings().GetColorValue(UIColorType.Background);
                _light = background.R + background.G + background.B > 384;
            }
            var root = Window.Current.Content as FrameworkElement;
            if (root != null) root.RequestedTheme = _light ? ElementTheme.Light : ElementTheme.Dark;
            SetBrush("BgBrush", ParseHex(_light ? "#FFFFFF" : "#000000"));
            SetBrush("ChromeBrush", ParseHex(_light ? "#F2F2F2" : "#1F1F1F"));
            SetBrush("ChromeLowBrush", ParseHex(_light ? "#F7F7F7" : "#171717"));
            SetBrush("ChromeMidBrush", ParseHex(_light ? "#E6E6E6" : "#2B2B2B"));
            SetBrush("LowBrush", ParseHex(_light ? "#D6D6D6" : "#333333"));
            SetBrush("CodeBrush", ParseHex(_light ? "#F2F2F2" : "#0C0C0C"));
            SetBrush("TextBrush", ParseHex(_light ? "#000000" : "#FFFFFF"));
            SetBrush("SoftTextBrush", ParseHex(_light ? "#444444" : "#BDBDBD"));
            SetBrush("MidBrush", ParseHex(_light ? "#666666" : "#999999"));
            SetBrush("FaintBrush", ParseHex(_light ? "#888888" : "#666666"));
            SetBrush("WarnTextBrush", ParseHex(_light ? "#9D5D00" : "#FFB900"));
            SetBrush("ErrTextBrush", ParseHex(_light ? "#B00020" : "#FF99A4"));
            SetBrush("ThinkBrush", ParseHex(_light ? "#5E3B9C" : "#B4A0FF"));
            SetBrush("OkBrush", ParseHex(_light ? "#237A18" : "#6CCB5F"));
            ApplyAccent(settings.FollowSystemAccent, settings.CustomAccent);
            ApplyStatusBar(false);
        }

        public static Color CurrentAccent { get; private set; } = Color.FromArgb(255, 0, 120, 215);

        /// <summary>把强调色三支画刷写进应用资源。整体容错，失败就保持默认色。</summary>
        public static void ApplyAccent(bool followSystem, string customHex)
        {
            try
            {
                var accent = followSystem ? ReadSystemAccent() : ParseHex(customHex);
                if (accent.A == 0 && accent.R == 0 && accent.G == 0 && accent.B == 0)
                {
                    accent = Color.FromArgb(255, 0, 120, 215);
                }

                CurrentAccent = accent;

                SetBrush("AccentBrush", accent);
                SetBrush("AccentTextBrush", _light ? Color.FromArgb(255, (byte)(accent.R * .7), (byte)(accent.G * .7), (byte)(accent.B * .7)) : Lighten(accent, 0.55));
                SetBrush("AccentSoftBrush", Color.FromArgb(0x59, accent.R, accent.G, accent.B));
            }
            catch (Exception)
            {
                // 资源还没就绪时忽略，下次再试
            }
        }

        private static Color ReadSystemAccent()
        {
            try
            {
                var settings = new UISettings();
                return settings.GetColorValue(UIColorType.Accent);
            }
            catch (Exception)
            {
                return Color.FromArgb(255, 0, 120, 215);
            }
        }

        private static void SetBrush(string key, Color color)
        {
            object value;
            if (!Application.Current.Resources.TryGetValue(key, out value)) return;

            var brush = value as SolidColorBrush;
            if (brush != null)
            {
                brush.Color = color;
            }
        }

        /// <summary>提亮到白色方向。amount 0–1。</summary>
        public static Color Lighten(Color color, double amount)
        {
            if (amount < 0) amount = 0;
            if (amount > 1) amount = 1;

            byte r = (byte)(color.R + (255 - color.R) * amount);
            byte g = (byte)(color.G + (255 - color.G) * amount);
            byte b = (byte)(color.B + (255 - color.B) * amount);
            return Color.FromArgb(255, r, g, b);
        }

        /// <summary>#RRGGBB / #AARRGGBB 解析；非法返回全透明。</summary>
        public static Color ParseHex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Colors.Transparent;

            var text = hex.Trim().TrimStart('#');
            try
            {
                if (text.Length == 6)
                {
                    byte r = Convert.ToByte(text.Substring(0, 2), 16);
                    byte g = Convert.ToByte(text.Substring(2, 2), 16);
                    byte b = Convert.ToByte(text.Substring(4, 2), 16);
                    return Color.FromArgb(255, r, g, b);
                }
                if (text.Length == 8)
                {
                    byte a = Convert.ToByte(text.Substring(0, 2), 16);
                    byte r = Convert.ToByte(text.Substring(2, 2), 16);
                    byte g = Convert.ToByte(text.Substring(4, 2), 16);
                    byte b = Convert.ToByte(text.Substring(6, 2), 16);
                    return Color.FromArgb(a, r, g, b);
                }
            }
            catch (Exception)
            {
                // 交给调用方处理
            }
            return Colors.Transparent;
        }

        /// <summary>按背景亮度自动选前景色（自定义气泡色后仍看得清）。</summary>
        public static Color ContrastForeground(Color background)
        {
            double luminance = background.R * 0.299 + background.G * 0.587 + background.B * 0.114;
            return luminance > 186 ? Colors.Black : Colors.White;
        }

        /// <summary>手机状态栏：黑底白字 + 可选进度指示。桌面没有状态栏，静默跳过。</summary>
        public static void ApplyStatusBar(bool busy)
        {
            if (!Windows.Foundation.Metadata.ApiInformation
                    .IsTypePresent("Windows.UI.ViewManagement.StatusBar")) return;

            ApplyMobileStatusBar(busy);
        }

        // Keep the mobile-only WinRT type out of the desktop entry point's
        // async state machine: its fields are resolved before the guard runs.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static async void ApplyMobileStatusBar(bool busy)
        {
            try
            {
                var statusBar = StatusBar.GetForCurrentView();
                if (statusBar == null) return;

                statusBar.BackgroundColor = _light ? Colors.White : Colors.Black;
                statusBar.BackgroundOpacity = 1;
                statusBar.ForegroundColor = _light ? Colors.Black : Colors.White;

                if (busy)
                {
                    statusBar.ProgressIndicator.Text = "正在生成";
                    await statusBar.ProgressIndicator.ShowAsync();
                }
                else
                {
                    await statusBar.ProgressIndicator.HideAsync();
                }
            }
            catch (Exception)
            {
                // 设备不支持就跳过
            }
        }
    }
}
